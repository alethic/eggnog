using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A <see cref="Database"/> set up and driven the way egglog-bridge drives one, and the <see cref="ReferenceView"/>
    /// oracle that should agree with it, run in lockstep: the root-context part of the context differential harness
    /// (<see cref="ContextPair"/>, <see cref="ContextTestSchema"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Schema: a union-find; constructors <c>pair(id, id) -&gt; id</c>, <c>wrap(id) -&gt; id</c> and
    /// <c>named(value) -&gt; id</c> with the union-id merge; a lattice <c>low(id) -&gt; value</c> merged by min; and a
    /// relation <c>tag(id, value)</c>. Each function table is <c>[logical columns..., ts]</c>, sorted by the
    /// timestamp, rebuilding its id columns. The tables read the union-find (one stratum above it) or only write it
    /// (the same stratum), as the constructor says.
    /// </para>
    /// <para>
    /// Driving: rows are staged stamped with the clock and canonicalized first, as the frontend does; a flush is the
    /// bridge's <c>flush_updates</c> (merge everything at the run's timestamp, advance the clock, rebuild if the
    /// union-find grew, egglog-bridge/src/lib.rs:946-959) and a rebuild is its native loop (rebuild every table at the
    /// next timestamp, refresh, advance the clock, until nothing changes; lib.rs:626-682).
    /// </para>
    /// </remarks>
    sealed class CongruenceHarness
    {

        readonly Database db;
        readonly TableId uf;
        readonly CounterId idCounter;
        readonly CounterId clock;
        readonly ParallelHeuristics parallelism;
        readonly bool readUnionFind;
        readonly List<ReferenceTable> schema = new();
        readonly Dictionary<ReferenceTable, TableId> tableIds = new();
        readonly List<TableId> functionTables = new();
        readonly List<(uint A, uint B)> unions = new();
        readonly List<(ReferenceTable Table, uint[] Row)> inserts = new();
        readonly List<uint> ids = new();
        readonly StringBuilder trace = new();
        readonly Random sampler;
        uint lastClock;
        List<(ReferenceTable Table, uint[] Row)> lastRows = new();

        /// <summary>
        /// Initializes a new harness.
        /// </summary>
        /// <param name="parallelism">The parallelism of the database and its tables.</param>
        /// <param name="readUnionFind">Whether the function tables declare the union-find a read dependency.</param>
        /// <param name="seed">The seed for sampling rows in checks.</param>
        public CongruenceHarness(ParallelHeuristics parallelism, bool readUnionFind, int seed)
        {
            this.parallelism = parallelism;
            this.readUnionFind = readUnionFind;
            sampler = new Random(seed);
            db = new Database(parallelism);
            uf = db.AddTable(new DisplacedTable(), name: "$uf");
            idCounter = db.AddCounter();
            clock = db.AddCounter();

            Pair = Define("pair", 2, [true, true, true], true, new UnionIdMerge(new ColumnId(2), uf, new ColumnId(3)), true);
            Wrap = Define("wrap", 1, [true, true], true, new UnionIdMerge(new ColumnId(1), uf, new ColumnId(2)), true);
            Low = Define("low", 1, [true, false], true, new MinMerge(new ColumnId(1)), false, Math.Min);
            Tag = Define("tag", 2, [true, false], false, KeepCurrentMerge.Instance, false);
            Named = Define("named", 1, [false, true], true, new UnionIdMerge(new ColumnId(1), uf, new ColumnId(2)), true);
        }

        /// <summary>
        /// Gets the binary constructor.
        /// </summary>
        public ReferenceTable Pair { get; }

        /// <summary>
        /// Gets the unary constructor.
        /// </summary>
        public ReferenceTable Wrap { get; }

        /// <summary>
        /// Gets the min lattice.
        /// </summary>
        public ReferenceTable Low { get; }

        /// <summary>
        /// Gets the relation.
        /// </summary>
        public ReferenceTable Tag { get; }

        /// <summary>
        /// Gets the constructor keyed by a value.
        /// </summary>
        public ReferenceTable Named { get; }

        /// <summary>
        /// Gets every logical table.
        /// </summary>
        public IReadOnlyList<ReferenceTable> Schema => schema;

        /// <summary>
        /// Gets the database.
        /// </summary>
        public Database Db => db;

        /// <summary>
        /// Gets the database table behind a logical table.
        /// </summary>
        public TableId TableOf(ReferenceTable table) => tableIds[table];

        /// <summary>
        /// Gets every id created so far.
        /// </summary>
        public IReadOnlyList<uint> Ids => ids;

        /// <summary>
        /// Gets the operations performed so far, for failure messages.
        /// </summary>
        public string Trace => trace.ToString();

        /// <summary>
        /// Gets the number of rebuild passes run.
        /// </summary>
        public int RebuildPasses { get; private set; }

        /// <summary>
        /// Gets the highest table generation among the function tables: above zero once a table compacted or was
        /// cleared, which makes cached indexes rebuild in full.
        /// </summary>
        public int MaxGeneration => functionTables.Max(t => db.GetTable(t).Version.Major);

        /// <summary>
        /// Gets the number of checks run.
        /// </summary>
        public int Checks { get; private set; }

        uint Timestamp => (uint)db.ReadCounter(clock);

        DisplacedTable UnionFind => (DisplacedTable)db.GetTable(uf);

        /// <summary>
        /// Mints a fresh id from the id counter.
        /// </summary>
        public uint NewId()
        {
            var id = (uint)db.IncrementCounter(idCounter);
            ids.Add(id);
            Note($"new {id}");
            return id;
        }

        /// <summary>
        /// Stages the union of two ids; merged at the next flush.
        /// </summary>
        public void Union(uint a, uint b)
        {
            Note($"union {a} {b}");
            var buffer = db.NewBuffer(uf);
            buffer.StageInsert([a, b, Timestamp]);
            buffer.Flush();
            unions.Add((a, b));
        }

        /// <summary>
        /// Stages a row (logical columns) into a table; merged at the next flush.
        /// </summary>
        public void Insert(ReferenceTable table, uint[] cells)
        {
            Note($"{table}({string.Join(",", cells)})");
            InsertMany(table, [cells], note: false);
        }

        /// <summary>
        /// Stages many rows into a table through one buffer.
        /// </summary>
        public void InsertMany(ReferenceTable table, IEnumerable<uint[]> rows, bool note = true)
        {
            var buffer = db.NewBuffer(tableIds[table]);
            var find = UnionFind;
            var count = 0;
            foreach (var cells in rows)
            {
                var row = new uint[cells.Length + 1];
                for (var c = 0; c < cells.Length; c++)
                    row[c] = table.IsId[c] ? find.FindNaive(cells[c]) : cells[c];

                row[^1] = Timestamp;
                buffer.StageInsert(row);
                inserts.Add((table, (uint[])cells.Clone()));
                count++;
            }

            buffer.Flush();
            if (note)
                Note($"{count} rows into {table}");
        }

        /// <summary>
        /// Looks a key up after flushing, and creates the row with a fresh id when absent: the bridge's
        /// <c>add_term</c>. An existing answer is checked against the oracle's.
        /// </summary>
        public uint GetOrAdd(ReferenceTable table, uint[] key)
        {
            Flush();
            var canonical = Canonical(table, key);
            if (db.GetTable(tableIds[table]).TryGetRowColumn(canonical, new ColumnId(table.KeyCount), out var found))
            {
                Note($"{table}({string.Join(",", key)}) -> {found}");
                var view = View();
                Assert.True(view.TryLookup(table, key, out var expected), $"the database found {table}({string.Join(",", key)}) where the oracle has nothing");
                Assert.Equal(view.Find(expected), view.Find(found));
                return found;
            }

            var id = NewId();
            Insert(table, key.Append(id).ToArray());
            Flush();
            return id;
        }

        /// <summary>
        /// Clears a table after flushing. The oracle keeps the equalities the table's rows implied, which the
        /// union-find keeps, and forgets the table's rows.
        /// </summary>
        public void Clear(ReferenceTable table)
        {
            Flush();
            Note($"clear {table}");
            var view = View();
            foreach (var id in ids)
            {
                var root = view.Find(id);
                if (root != id)
                    unions.Add((id, root));
            }

            inserts.RemoveAll(i => i.Table == table);
            var before = db.TotalSizeEstimate;
            var count = db.GetTable(tableIds[table]).Count;
            db.ClearTable(tableIds[table]);
            Assert.Equal(0, db.GetTable(tableIds[table]).Count);
            Assert.Equal(before - count, db.TotalSizeEstimate);
        }

        /// <summary>
        /// The bridge's <c>flush_updates</c>: merge everything at the run's timestamp, advance the clock, and rebuild
        /// if the union-find grew.
        /// </summary>
        /// <returns>The result of the merge.</returns>
        public bool Flush()
        {
            var before = UnionFind.Count;
            var changed = db.MergeAll(Timestamp);
            db.IncrementCounter(clock);
            if (UnionFind.Count != before)
                Rebuild();

            Assert.Null(db.Failure);
            return changed;
        }

        /// <summary>
        /// The bridge's native rebuild loop.
        /// </summary>
        public void Rebuild()
        {
            var passes = 0;
            while (true)
            {
                var next = Timestamp;
                var rebuilt = db.ApplyRebuild(uf, functionTables.ToArray(), next);
                var refreshed = db.RefreshRowsForValues(functionTables.ToArray(), ReadOnlySpan<uint>.Empty, next);
                db.IncrementCounter(clock);
                passes++;
                RebuildPasses++;
                if (rebuilt == false && refreshed == false)
                    break;

                Assert.True(passes < 200, "the rebuild loop does not converge");
            }
        }

        /// <summary>
        /// Flushes and rebuilds, then asserts that the database holds what the oracle holds: the same classes over
        /// every id, the same rows modulo renaming, the same lookups, live counts and statistics, column indexes that
        /// answer constant constraints, and fresh timestamps on every row that changed since the last check.
        /// </summary>
        /// <param name="maxIndexRowsPerTable">How many rows per table to probe through the column indexes.</param>
        public void Check(int maxIndexRowsPerTable = int.MaxValue)
        {
            Note("check");
            Checks++;
            Flush();
            Rebuild();
            Assert.False(db.ApplyRebuild(uf, functionTables.ToArray(), Timestamp), "a closed database still rebuilt something");

            var view = View();
            var find = UnionFind;
            Assert.Equal(Describe(view.Find, t => view.Rows(t)), Describe(find.FindNaive, t => StoreRows(t).Select(Logical)));

            foreach (var table in schema)
            {
                var engine = db.GetTable(tableIds[table]);
                foreach (var row in view.Rows(table))
                {
                    var key = Canonical(table, row.AsSpan(0, table.KeyCount).ToArray());
                    if (table.HasOutput)
                    {
                        Assert.True(engine.TryGetRowColumn(key, new ColumnId(table.KeyCount), out var output), $"{table}: no row for ({string.Join(",", key)})");
                        if (table.OutputIsId)
                            Assert.Equal(find.FindNaive(row[table.KeyCount]), find.FindNaive(output));
                        else
                            Assert.Equal(row[table.KeyCount], output);
                    }
                    else
                    {
                        Assert.NotNull(engine.GetRow(key));
                    }
                }
            }

            CheckStatistics();
            CheckIndexes(maxIndexRowsPerTable);
            CheckSince();
        }

        /// <summary>
        /// Builds the oracle's view from everything asserted so far.
        /// </summary>
        ReferenceView View()
        {
            var view = new ReferenceView(schema);
            foreach (var (a, b) in unions)
                view.Union(a, b);

            foreach (var (table, row) in inserts)
                view.Add(table, row);

            view.Close();
            return view;
        }

        /// <summary>
        /// Checks live counts, size estimates and column statistics.
        /// </summary>
        void CheckStatistics()
        {
            var estimator = new ColumnCardEstimator(db);
            long total = UnionFind.Count;
            foreach (var table in schema)
            {
                var id = tableIds[table];
                var rows = StoreRows(table);
                total += rows.Count;
                Assert.Equal(rows.Count, db.EstimateSize(id));
                for (var c = 0; c <= table.Arity; c++)
                {
                    var uniqueness = estimator.GetColumnUniqueness(id, new ColumnId(c));
                    Assert.Equal((ulong)rows.Count, uniqueness.TableSize);
                    Assert.True(uniqueness.ColumnSize >= (ulong)rows.Select(r => r[c]).Distinct().Count(), $"{table}: column {c} counts fewer distinct values than its live rows hold");
                }
            }

            Assert.Equal(total, db.TotalSizeEstimate);
        }

        /// <summary>
        /// Checks that every probed row is found through a constant constraint on each of its columns.
        /// </summary>
        void CheckIndexes(int maxRowsPerTable)
        {
            foreach (var table in schema)
            {
                var id = tableIds[table];
                var engine = db.GetTable(id);
                var rows = StoreRows(table);
                var probed = rows.Count <= maxRowsPerTable ? rows : rows.OrderBy(_ => sampler.Next()).Take(maxRowsPerTable).ToList();
                var cells = new uint[table.Arity + 1];
                foreach (var row in probed)
                {
                    Assert.True(engine.TryGetRow(row.AsSpan(0, table.KeyCount), cells, out var rowId));
                    Assert.Equal(row, cells);
                    for (var c = 0; c < row.Length; c++)
                    {
                        using var processed = db.ProcessConstraints(id, [Constraint.EqConst(new ColumnId(c), row[c])]);
                        Assert.Empty(processed.Slow);
                        var found = false;
                        foreach (var candidate in processed.Rows)
                        {
                            if (candidate == rowId)
                            {
                                found = true;
                                break;
                            }
                        }

                        Assert.True(found, $"{table} by column {c} misses ({string.Join(",", row)})");
                    }
                }
            }
        }

        /// <summary>
        /// Checks that every row whose canonical form is new since the last check carries a timestamp at least the
        /// clock at that check, so that seminaive evaluation sees it.
        /// </summary>
        void CheckSince()
        {
            var before = new HashSet<string>(lastRows.Select(r => Key(r.Table, Canonical(r.Table, r.Row))));
            var now = new List<(ReferenceTable Table, uint[] Row)>();
            foreach (var table in schema)
            {
                foreach (var row in StoreRows(table))
                {
                    var logical = Logical(row);
                    now.Add((table, logical));
                    if (before.Contains(Key(table, logical)) == false)
                        Assert.True(row[^1] >= lastClock, $"{Key(table, logical)} is new since the clock was {lastClock} but carries timestamp {row[^1]}");
                }
            }

            lastClock = Timestamp;
            lastRows = now;
        }

        /// <summary>
        /// Lists the live rows of a table, timestamps included.
        /// </summary>
        List<uint[]> StoreRows(ReferenceTable table) => TableTestUtil.LiveRows(db.GetTable(tableIds[table]));

        static uint[] Logical(uint[] row) => row[..^1];

        uint[] Canonical(ReferenceTable table, uint[] cells)
        {
            var find = UnionFind;
            var copy = (uint[])cells.Clone();
            for (var c = 0; c < copy.Length; c++)
                if (table.IsId[c])
                    copy[c] = find.FindNaive(copy[c]);

            return copy;
        }

        static string Key(ReferenceTable table, uint[] row) => table.Name + "(" + string.Join(",", row) + ")";

        /// <summary>
        /// Describes a view in terms that do not depend on which id each side made canonical: each class is named by
        /// the least id in it.
        /// </summary>
        string Describe(Func<uint, uint> find, Func<ReferenceTable, IEnumerable<uint[]>> rows)
        {
            var names = new Dictionary<uint, uint>();
            foreach (var id in ids)
            {
                var root = find(id);
                names[root] = names.TryGetValue(root, out var name) ? Math.Min(name, id) : id;
            }

            var text = new StringBuilder();
            foreach (var id in ids.OrderBy(i => i))
                if (names[find(id)] != id)
                    text.Append(id).Append('=').Append(names[find(id)]).Append(' ');

            text.AppendLine();
            foreach (var table in schema)
            {
                var lines = new List<string>();
                foreach (var row in rows(table))
                {
                    var cells = new string[row.Length];
                    for (var c = 0; c < row.Length; c++)
                        cells[c] = table.IsId[c] ? "#" + names[find(row[c])] : row[c].ToString();

                    lines.Add(string.Join(",", cells));
                }

                lines.Sort(StringComparer.Ordinal);
                text.Append(table.Name).Append(": ").AppendLine(string.Join(" | ", lines));
            }

            return text.ToString();
        }

        ReferenceTable Define(string name, int keyCount, bool[] isId, bool hasOutput, MergeFunction merge, bool writesUnionFind, Func<uint, uint, uint>? mergeValues = null)
        {
            var table = new ReferenceTable(name, keyCount, isId, hasOutput, mergeValues);
            var toRebuild = Enumerable.Range(0, isId.Length).Where(c => isId[c]).Select(c => new ColumnId(c)).ToArray();
            var engine = new SortedWritesTable(keyCount, isId.Length + 1, new ColumnId(isId.Length), toRebuild, merge, parallelism);
            var id = db.AddTable(engine, readUnionFind ? [uf] : [], writesUnionFind ? [uf] : [], name);
            Assert.Equal(readUnionFind ? 1 : 0, db.Dependencies.LevelOf(id));
            schema.Add(table);
            tableIds[table] = id;
            functionTables.Add(id);
            return table;
        }

        void Note(string operation) => trace.AppendLine(operation);

    }

}
