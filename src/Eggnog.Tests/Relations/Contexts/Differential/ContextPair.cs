using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One context of a context differential program: the engine's context behind a <see cref="ContextFacade"/> and the
    /// oracle <see cref="ReferenceContext"/>, driven in lockstep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every operation is applied to both and traced in the program (<see cref="ContextProgram.Trace"/>). Ids are the
    /// engine's; the reference is told them. After every write the engine's lookup paths are compared
    /// (<see cref="LookupPaths"/>), and every rebuild runs the engine's invariant checker
    /// (<see cref="ContextFacade.Rebuild"/>).
    /// </para>
    /// <para>
    /// <see cref="Check"/> rebuilds and compares: the engine's classes and rows with the reference's exactly (both make
    /// a class's minimum canonical); every reference row found by the engine's lookups; every engine row found by a
    /// scan of each of its columns; and every row that changed since the last check found by a scan since that check's
    /// clock reading.
    /// </para>
    /// </remarks>
    sealed class ContextPair
    {

        readonly List<uint> ownIds = new();
        readonly List<ContextPair> children = new();
        uint lastClock;
        List<(LogicalTable Table, ulong[] Row)> lastRows = new();

        /// <summary>
        /// Initializes a pair of <paramref name="program"/> over <paramref name="engine"/> and
        /// <paramref name="reference"/>, named <paramref name="name"/> in the trace.
        /// </summary>
        ContextPair(ContextProgram program, Context engine, ReferenceContext reference, ContextPair? parent, string name)
        {
            Program = program;
            Facade = new ContextFacade(engine, program.Schema) { RawWrites = program.RawChildWrites && engine.IsRoot == false };
            Reference = reference;
            Parent = parent;
            Name = name;
            Depth = parent is null ? 0 : parent.Depth + 1;
        }

        /// <summary>
        /// Creates the root pair of a fresh program.
        /// </summary>
        public static ContextPair Root()
        {
            var program = new ContextProgram();
            return new ContextPair(program, program.Store.Root, new ReferenceContext(program.Schema.Tables, null, new HashSet<ulong>()), null, "r");
        }

        /// <summary>
        /// Gets the program.
        /// </summary>
        public ContextProgram Program { get; }

        /// <summary>
        /// Gets the engine's context, behind the facade.
        /// </summary>
        public ContextFacade Facade { get; }

        /// <summary>
        /// Gets the engine's context.
        /// </summary>
        public Context Engine => Facade.Context;

        /// <summary>
        /// Gets the oracle.
        /// </summary>
        public ReferenceContext Reference { get; }

        /// <summary>
        /// Gets the parent pair, or <c>null</c>.
        /// </summary>
        public ContextPair? Parent { get; }

        /// <summary>
        /// Gets the pair's name in the trace.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the depth.
        /// </summary>
        public int Depth { get; }

        /// <summary>
        /// Gets the live children.
        /// </summary>
        public IReadOnlyList<ContextPair> Children
        {
            get
            {
                lock (children)
                    return children.ToArray();
            }
        }

        /// <summary>
        /// Gets whether the pair is frozen.
        /// </summary>
        public bool IsFrozen => Engine.IsFrozen;

        /// <summary>
        /// Gets whether the pair may be frozen: its parent is.
        /// </summary>
        public bool CanFreeze => IsFrozen == false && (Parent is null || Parent.IsFrozen);

        /// <summary>
        /// Gets the ids the pair can name: minted in it or an ancestor.
        /// </summary>
        public List<uint> VisibleIds()
        {
            var ids = Parent?.VisibleIds() ?? new List<uint>();
            lock (ownIds)
                ids.AddRange(ownIds);

            return ids;
        }

        /// <summary>
        /// Mints an id.
        /// </summary>
        public uint NewId()
        {
            var id = Facade.NewId();
            AddOwn(id);
            Note($"new {id}");
            return id;
        }

        /// <summary>
        /// Unions two ids in each.
        /// </summary>
        public void Union(uint a, uint b)
        {
            Note($"union {a} {b}");
            Facade.Union(a, b);
            Reference.Union(a, b);
            AfterWrite();
        }

        /// <summary>
        /// Writes a row of a function in each.
        /// </summary>
        public void Insert(LogicalTable table, ulong[] key, ulong output)
        {
            Note($"{table.Name}({string.Join(",", key)}) = {output}");
            var row = key.Append(output).ToArray();
            Facade.Insert(table, row);
            Reference.Insert(table, row);
            AfterWrite();
        }

        /// <summary>
        /// Writes a row of a relation in each.
        /// </summary>
        public void Insert(LogicalTable table, ulong[] key)
        {
            Note($"{table.Name}({string.Join(",", key)})");
            Facade.Insert(table, key);
            Reference.Insert(table, key);
            AfterWrite();
        }

        /// <summary>
        /// Gets or adds through the engine. A new class is told to the oracle as the row it is; an existing
        /// one is checked against the oracle's own answer, which a stale lookup can only ever agree with.
        /// </summary>
        public uint GetOrAdd(LogicalTable table, ulong[] key)
        {
            var id = Facade.GetOrAdd(table, key, out var created);
            Note($"{table.Name}({string.Join(",", key)}) -> {id}{(created ? " new" : "")}");
            if (created)
            {
                AddOwn(id);
                Reference.Insert(table, key.Append((ulong)id).ToArray());
            }
            else if (Program.CheckFoundRows)
            {
                var view = Reference.View();
                Assert.True(view.TryLookup(table, key, out var expected), $"{Name}: the engine found {table.Name}({string.Join(",", key)}) where the oracle has nothing");
                Assert.Equal(view.Find(expected), view.Find(id));
            }

            AfterWrite();
            return id;
        }

        /// <summary>
        /// Retracts the row with key <paramref name="key"/> where the engine's retraction rule allows it
        /// (<see cref="ContextFacade.CanRetract"/>), and otherwise asserts that the engine refuses without changing
        /// anything.
        /// </summary>
        public void Delete(LogicalTable table, ulong[] key)
        {
            if (Facade.CanRetract == false)
            {
                Note($"refused delete {table.Name}({string.Join(",", key)})");
                Facade.AssertRetractionRefused(table, key);
                return;
            }

            Note($"delete {table.Name}({string.Join(",", key)})");
            var canonical = Facade.Delete(table, key);
            Reference.Delete(table, canonical.Select(c => (ulong)c).ToArray());
            AfterWrite();
        }

        /// <summary>
        /// Clears <paramref name="table"/> where the engine's retraction rule allows it, and otherwise asserts that the
        /// engine refuses.
        /// </summary>
        public void Clear(LogicalTable table)
        {
            if (Facade.CanRetract == false)
            {
                Note($"refused clear {table.Name}");
                Facade.AssertRetractionRefused(table, new ulong[table.KeyCount]);
                return;
            }

            Note($"clear {table.Name}");
            Facade.Clear(table);
            Reference.Clear(table);
            AfterWrite();
        }

        /// <summary>
        /// Rebuilds the engine, checking its invariants.
        /// </summary>
        public void Rebuild()
        {
            Note("rebuild");
            Facade.Rebuild();
        }

        /// <summary>
        /// Freezes the engine's context.
        /// </summary>
        public void Freeze()
        {
            Note("freeze");
            Engine.Freeze();
        }

        /// <summary>
        /// Creates a child in each.
        /// </summary>
        public ContextPair CreateChild()
        {
            string name;
            lock (children)
                name = $"{Name}{children.Count}";

            Program.Note($"{name} = {Name}.CreateChild()");
            var engine = Engine.CreateChild();
            var reference = Reference.CreateChild(VisibleIds().Select(id => (ulong)id));
            var child = new ContextPair(Program, engine, reference, this, name);
            lock (children)
                children.Add(child);

            return child;
        }

        /// <summary>
        /// Disposes the engine's context; it must have no children.
        /// </summary>
        public void Dispose()
        {
            Note("dispose");
            Engine.Dispose();
            lock (Parent!.children)
                Parent.children.Remove(this);
        }

        /// <summary>
        /// Rebuilds the pair and compares it with the oracle; see the class remarks.
        /// </summary>
        public void Check()
        {
            Note("check");
            Rebuild();
            var view = Reference.View();
            var ids = VisibleIds();
            var tables = Program.Schema.Tables;

            Assert.Equal(DescribeExact(tables, ids, id => view.Find(id), t => view.Rows(t)), DescribeExact(tables, ids, id => Facade.Find((uint)id), t => Facade.Rows(t)));

            foreach (var table in tables)
            {
                foreach (var row in view.Rows(table))
                {
                    var key = row.AsSpan(0, table.KeyCount);
                    if (table.HasOutput)
                    {
                        Assert.True(Facade.TryLookup(table, key, out var output), $"{Name}: the engine misses {table.Name}({string.Join(",", row)})");
                        Assert.Equal(table.OutputIsId ? view.Find(row[table.KeyCount]) : row[table.KeyCount], output);
                    }
                    else
                    {
                        Assert.True(Facade.Contains(table, key), $"{Name}: the engine misses {table.Name}({string.Join(",", row)})");
                    }
                }

                // Column scans (the lift reads in a child) return exactly the visible rows holding the value: none
                // missed (completeness) and none extra, so a hidden, stale or tombstoned row delivered by ScanOwn or
                // ScanAncestorsMentioning is caught (soundness).
                var visible = Facade.Rows(table);
                for (var column = 0; column < table.Arity; column++)
                {
                    foreach (var value in visible.Select(r => r[column]).Distinct())
                    {
                        var expected = visible.Where(r => r[column] == value).Select(r => string.Join(",", r)).OrderBy(r => r, StringComparer.Ordinal).ToArray();
                        var scanned = Facade.ScanColumn(table, column, value).Select(r => string.Join(",", r)).OrderBy(r => r, StringComparer.Ordinal).ToArray();
                        Assert.True(expected.SequenceEqual(scanned), $"{Name}: {table.Name} by column {column} = {value} gives [{string.Join(" | ", scanned)}], the visible rows are [{string.Join(" | ", expected)}]");
                    }
                }
            }

            CheckSince();
            LookupPaths.Check(Engine);
        }

        /// <summary>
        /// Checks that a scan since the last check's clock reading returns every row that changed since.
        /// </summary>
        void CheckSince()
        {
            var tables = Program.Schema.Tables;
            var now = new List<(LogicalTable Table, ulong[] Row)>();
            foreach (var table in tables)
                foreach (var row in Facade.Rows(table))
                    now.Add((table, row));

            var before = new HashSet<string>(lastRows.Select(r => Key(r.Table, Canonical(r.Table, r.Row))));
            var since = new HashSet<string>();
            foreach (var table in tables)
                foreach (var row in Facade.Rows(table, lastClock))
                    since.Add(Key(table, row));

            foreach (var (table, row) in now)
            {
                var key = Key(table, row);
                if (before.Contains(key) == false)
                    Assert.True(since.Contains(key), $"{Name}: a scan since {lastClock} misses the new row {key}");
            }

            lastClock = Facade.Clock;
            lastRows = now;
        }

        /// <summary>
        /// Compares the engine's lookup paths after a write: on the written context, which has caught up with its
        /// ancestors even when its own unions await a rebuild.
        /// </summary>
        void AfterWrite()
        {
            if (Program.CheckWrites == false)
                return;

            if (Engine.IsClean)
                LookupPaths.Check(Engine);
            else
                LookupPaths.CheckLayers(Engine);
        }

        /// <summary>
        /// Records an id minted in this pair.
        /// </summary>
        void AddOwn(uint id)
        {
            lock (ownIds)
                ownIds.Add(id);
        }

        /// <summary>
        /// Appends an operation of this pair to the trace.
        /// </summary>
        void Note(string operation) => Program.Note($"{Name}: {operation}");

        /// <summary>
        /// Rewrites a row's ids to their canonical ids in the engine now.
        /// </summary>
        ulong[] Canonical(LogicalTable table, ulong[] row)
        {
            var copy = (ulong[])row.Clone();
            for (var i = 0; i < copy.Length; i++)
                if (table.IsId[i])
                    copy[i] = Facade.Find((uint)copy[i]);

            return copy;
        }

        /// <summary>
        /// Renders a row as <c>table(cells)</c>, for set membership.
        /// </summary>
        static string Key(LogicalTable table, ulong[] row) => table.Name + "(" + string.Join(",", row) + ")";

        /// <summary>
        /// Describes a view exactly: every id that is not canonical with its canonical id, then each table's rows,
        /// sorted. The engine and the reference both make a class's minimum canonical, so their descriptions are equal.
        /// </summary>
        static string DescribeExact(IReadOnlyList<LogicalTable> tables, List<uint> ids, Func<ulong, ulong> find, Func<LogicalTable, IEnumerable<ulong[]>> rows)
        {
            var text = new StringBuilder();
            foreach (var id in ids.OrderBy(i => i))
                if (find(id) != id)
                    text.Append(id).Append('=').Append(find(id)).Append(' ');

            text.AppendLine();
            foreach (var table in tables)
            {
                var lines = new List<string>();
                foreach (var row in rows(table))
                {
                    var cells = new string[row.Length];
                    for (var column = 0; column < row.Length; column++)
                        cells[column] = table.IsId[column] ? "#" + find(row[column]) : row[column].ToString();

                    lines.Add(string.Join(",", cells));
                }

                lines.Sort(StringComparer.Ordinal);
                text.Append(table.Name).Append(": ").AppendLine(string.Join(" | ", lines));
            }

            return text.ToString();
        }

    }

}
