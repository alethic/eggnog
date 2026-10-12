using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A random layered state and its reference (colored-contexts spec §17 M9.3): a store with lattice tables of several
    /// shapes (functions of 0 to 4 id keys, relations, constructors, a value-only table, a context-private table), a root
    /// written (with unions and rebuilds) and then, usually, frozen, and a chain of up to three children, each written
    /// before it creates its child and then frozen or left open, with no unions below the root; sometimes a sibling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After every write batch the written context is checked with <see cref="LookupPaths"/> (the three lookup paths
    /// agree) and against a reference: the context's parent's reference plus its own batches, replayed with the merge
    /// semantics (removals of a batch before its inserts, id cells canonicalized through the root's find, the timestamp
    /// ignored); a context-private table starts empty in every child. The root's reference is its actual contents, which
    /// the egglog-parity suites cover. At the end every context is checked again (isolation), and the chain is disposed.
    /// </para>
    /// <para>
    /// These states are built as M9.3's were: no ancestor changes after it has children, so no catch-up is needed (the
    /// context differential, <see cref="ContextDifferentialTests"/>, covers ancestors that change); deletes and clears
    /// happen only where §6.7 allows them (a root before its first child, a child under a frozen parent before its own
    /// children).
    /// </para>
    /// </remarks>
    sealed class RandomLayeredProgram
    {

        readonly Random random;
        readonly Store store;
        readonly List<TableId> tables = new();
        readonly uint[] ids;
        readonly List<(Context Context, Dictionary<TableId, Dictionary<string, uint[]>> Reference)> contexts = new();

        /// <summary>
        /// Initializes the program for <paramref name="seed"/>.
        /// </summary>
        public RandomLayeredProgram(int seed)
        {
            random = new Random(seed);
            store = new Store(ParallelHeuristics.Serial);
            var shapes = Enumerable.Range(0, 7).OrderBy(_ => random.Next()).Take(random.Next(2, 6)).ToList();
            foreach (var shape in shapes)
            {
                switch (shape)
                {
                    case 0:
                        tables.Add(store.AddTable(LayeredTestUtil.MaxFunction(1), name: "max1"));
                        break;
                    case 1:
                        tables.Add(store.AddTable(LayeredTestUtil.MaxFunction(2), name: "max2"));
                        break;
                    case 2:
                        tables.Add(store.AddTable(LayeredTestUtil.MaxFunction(0), name: "max0"));
                        break;
                    case 3:
                        tables.Add(store.AddTable(LayeredTestUtil.MaxFunction(4), name: "max4"));
                        break;
                    case 4:
                        tables.Add(store.AddTable(LayeredTestUtil.Relation(2), name: "rel2"));
                        break;
                    case 5:
                        tables.Add(store.AddTable(LayeredTestUtil.Constructor(store), [], [store.UnionFindTable], "ctor"));
                        break;
                    default:
                        tables.Add(store.AddTable(LayeredTestUtil.ValueOnly(), name: "vals", contextPrivate: random.Next(2) == 0));
                        break;
                }
            }

            if (random.Next(3) == 0)
                tables.Add(store.AddTable(LayeredTestUtil.MaxFunction(1), name: "private", contextPrivate: true));

            ids = Enumerable.Range(0, 10).Select(_ => store.Root.NewId()).ToArray();
        }

        /// <summary>
        /// Gets the number of write batches checked.
        /// </summary>
        public int Batches { get; private set; }

        /// <summary>
        /// Gets the ancestor rows the children hid, summed (<see cref="LayerStatistics.RowsHidden"/>).
        /// </summary>
        public long RowsHidden { get; private set; }

        /// <summary>
        /// Gets the incoming rows the children canonicalized, summed (<see cref="LayerStatistics.RowsCanonicalized"/>).
        /// </summary>
        public long RowsCanonicalized { get; private set; }

        /// <summary>
        /// Gets the live-set unions the children's views built, summed (<see cref="LayerStatistics.FilterMerges"/>).
        /// </summary>
        public long FilterMerges { get; private set; }

        /// <summary>
        /// Gets the number of children that deleted a row they saw through an ancestor.
        /// </summary>
        public int Retracting { get; private set; }

        /// <summary>
        /// Gets the number of clears in children.
        /// </summary>
        public int Clears { get; private set; }

        /// <summary>
        /// Gets the number of frozen children.
        /// </summary>
        public int FrozenChildren { get; private set; }

        /// <summary>
        /// Builds the state, checking after every write, and disposes it.
        /// </summary>
        public void Run()
        {
            var root = store.Root;

            // The root: writes, unions with rebuilds, more writes, deletes and clears; it has no descendant yet.
            for (var batch = random.Next(1, 5); batch > 0; batch--)
                WriteRoot(root);

            for (var unions = random.Next(0, 3); unions > 0; unions--)
                root.Union(ids[random.Next(ids.Length)], ids[random.Next(ids.Length)]);

            root.Rebuild();
            for (var batch = random.Next(0, 3); batch > 0; batch--)
                WriteRoot(root);

            var rootFrozen = random.Next(4) != 0;
            if (rootFrozen)
                root.Freeze();

            contexts.Add((root, Actual(root)));

            // The chain.
            var depth = random.Next(1, 4);
            var parent = 0;
            for (var level = 1; level <= depth; level++)
            {
                var child = AddChild(parent);
                var last = level == depth;
                if (last == false && contexts[parent].Context.IsFrozen && random.Next(10) < 7)
                    contexts[child].Context.Freeze();

                parent = child;
            }

            // Sometimes a sibling under a frozen context of the chain.
            var frozen = contexts.Select((entry, index) => (entry.Context, index)).Where(entry => entry.Context.IsFrozen).ToList();
            if (frozen.Count > 0 && random.Next(2) == 0)
                AddChild(frozen[random.Next(frozen.Count)].index);

            // Isolation: every context still sees exactly its reference.
            foreach (var (context, reference) in contexts)
                Compare(context, reference);

            foreach (var (context, _) in contexts.Skip(1))
            {
                var statistics = context.Tables!.Statistics;
                RowsHidden += statistics.RowsHidden;
                RowsCanonicalized += statistics.RowsCanonicalized;
                FilterMerges += statistics.FilterMerges;
                FrozenChildren += context.IsFrozen ? 1 : 0;
                foreach (var table in tables)
                    if (context.Tables.TryGetLayer(table) is { HasRetractions: true })
                    {
                        Retracting++;
                        break;
                    }
            }

            // Children come after their parents, so disposing backwards never meets a context with live children.
            for (var i = contexts.Count - 1; i > 0; i--)
                contexts[i].Context.Dispose();

            Assert.False(store.Root.Gate.HasLiveDescendants);
        }

        /// <summary>
        /// Creates a child of the context at <paramref name="parentIndex"/>, writes it, and returns its index.
        /// </summary>
        int AddChild(int parentIndex)
        {
            var (parent, parentReference) = contexts[parentIndex];
            var child = parent.CreateChild();
            var reference = new Dictionary<TableId, Dictionary<string, uint[]>>();
            foreach (var table in tables)
                reference[table] = store.Schema[table]!.IsContextPrivate ? new Dictionary<string, uint[]>() : new Dictionary<string, uint[]>(parentReference[table]);

            contexts.Add((child, reference));
            Compare(child, reference);
            var retract = parent.IsFrozen;
            for (var batch = random.Next(1, 7); batch > 0; batch--)
                WriteChild(child, reference, retract);

            return contexts.Count - 1;
        }

        /// <summary>
        /// One random batch at the root, in canonical ids (the root's raw-write contract), then a check.
        /// </summary>
        void WriteRoot(Context root)
        {
            var ops = RandomOps(root, null, true);
            root.Write(state =>
            {
                foreach (var (table, row, remove) in ops)
                {
                    if (remove)
                        state.StageRemove(table, row);
                    else
                        state.StageInsert(table, Stamped(table, row, state.Timestamp));
                }

                return 0;
            });

            if (random.Next(8) == 0 && tables.Count > 0)
                root.ClearTable(tables[random.Next(tables.Count)]);

            root.Rebuild();
            LookupPaths.Check(root);
            Batches++;
        }

        /// <summary>
        /// One random batch in a child, then a check against the reference.
        /// </summary>
        void WriteChild(Context child, Dictionary<TableId, Dictionary<string, uint[]>> reference, bool retract)
        {
            if (retract && random.Next(12) == 0 && tables.Count > 0)
            {
                var cleared = tables[random.Next(tables.Count)];
                child.ClearTable(cleared);
                Clears++;
                reference[cleared].Clear();
                Compare(child, reference);
                Batches++;
                return;
            }

            var ops = RandomOps(child, reference, retract);
            var rawStamp = random.Next(3) == 0;
            child.Write(state =>
            {
                foreach (var (table, row, remove) in ops)
                {
                    if (remove)
                        state.StageRemove(table, row);
                    else
                        state.StageInsert(table, rawStamp ? row : Stamped(table, row, state.Timestamp));
                }

                return 0;
            });

            // The reference: each table's removals first, then its inserts in staging order.
            foreach (var (table, row, remove) in ops)
                if (remove)
                    reference[table].Remove(LayeredTestUtil.KeyOf(row));

            foreach (var (table, row, remove) in ops)
                if (remove == false)
                    Apply(reference[table], table, Canonical(table, row));

            Compare(child, reference);
            Batches++;
        }

        /// <summary>
        /// Draws a batch of inserts and, when <paramref name="retract"/>, removals. Rows of a constructor reuse the value
        /// the context sees for the key, so no union is staged below the root.
        /// </summary>
        List<(TableId Table, uint[] Row, bool Remove)> RandomOps(Context context, Dictionary<TableId, Dictionary<string, uint[]>>? reference, bool retract)
        {
            var ops = new List<(TableId, uint[], bool)>();
            var canonical = context.IsRoot;
            for (var n = random.Next(1, 5); n > 0; n--)
            {
                var table = tables[random.Next(tables.Count)];
                var schema = store.Schema[table]!;
                var key = new uint[schema.KeyCount];
                for (var c = 0; c < key.Length; c++)
                    key[c] = schema.RebuildColumns.Length > 0 ? Id(canonical) : (uint)random.Next(8);

                if (retract && random.Next(4) == 0)
                {
                    ops.Add((table, key, true));
                    continue;
                }

                var row = new uint[schema.Arity];
                key.CopyTo(row, 0);
                if (schema.Root.MergeFunction is MaxMerge max)
                {
                    row[max.ValueColumn] = (uint)random.Next(20);
                }
                else if (schema.Root.MergeFunction is UnionIdMerge union)
                {
                    var seen = reference is null
                        ? LayeredTestUtil.Row(context, table, Canonical(table, row)[..schema.KeyCount])
                        : reference[table].GetValueOrDefault(LayeredTestUtil.KeyOf(Canonical(table, row).AsSpan(0, schema.KeyCount)));

                    // A row staged earlier in this batch for the same canonical key decides too.
                    foreach (var (other, staged, removed) in ops)
                        if (other == table && removed == false && Canonical(table, staged).AsSpan(0, schema.KeyCount).SequenceEqual(Canonical(table, row).AsSpan(0, schema.KeyCount)))
                            seen = Canonical(table, staged);

                    row[union.ValueColumn] = seen is not null ? seen[union.ValueColumn] : Id(true);
                }

                ops.Add((table, row, false));
            }

            return ops;
        }

        /// <summary>
        /// Draws one of the root's ids; canonical in the root when asked.
        /// </summary>
        uint Id(bool canonical)
        {
            var id = ids[random.Next(ids.Length)];
            return canonical ? store.Root.UnionFind.Find(id) : id;
        }

        /// <summary>
        /// Copies a row with <paramref name="stamp"/> in its timestamp column.
        /// </summary>
        uint[] Stamped(TableId table, uint[] row, uint stamp)
        {
            var copy = (uint[])row.Clone();
            if (store.Schema[table]!.SortColumn is var sort and >= 0)
                copy[sort] = stamp;

            return copy;
        }

        /// <summary>
        /// Copies a row with its id cells canonical in the root (no union happens below it).
        /// </summary>
        uint[] Canonical(TableId table, uint[] row)
        {
            var copy = (uint[])row.Clone();
            foreach (var column in store.Schema[table]!.RebuildColumns)
                copy[column.Rep] = store.Root.UnionFind.Find(copy[column.Rep]);

            return copy;
        }

        /// <summary>
        /// Applies one insert to a reference table with the table's merge: a new key is added; an existing one changes
        /// only when the merge replaces it (a larger value); a relation or an equal constructor value changes nothing.
        /// </summary>
        void Apply(Dictionary<string, uint[]> rows, TableId table, uint[] row)
        {
            var schema = store.Schema[table]!;
            var key = LayeredTestUtil.KeyOf(row.AsSpan(0, schema.KeyCount));
            if (rows.TryGetValue(key, out var current) == false)
            {
                rows[key] = row;
                return;
            }

            switch (schema.Root.MergeFunction)
            {
                case MaxMerge max when row[max.ValueColumn] > current[max.ValueColumn]:
                    rows[key] = row;
                    break;
                case UnionIdMerge union:
                    Assert.Equal(current[union.ValueColumn], row[union.ValueColumn]);
                    break;
            }
        }

        /// <summary>
        /// Checks the context's lookup paths and compares what it sees with the reference, timestamps aside.
        /// </summary>
        void Compare(Context context, Dictionary<TableId, Dictionary<string, uint[]>> reference)
        {
            var seen = LookupPaths.Check(context, ids.Select(id => new[] { id }));
            foreach (var table in tables)
            {
                var sort = store.Schema[table]!.SortColumn;
                var expected = reference[table];
                var actual = seen[table];
                Assert.True(expected.Count == actual.Count, $"table {store.Schema.GetName(table)} at depth {context.Depth}: {actual.Count} rows seen, {expected.Count} expected");
                foreach (var (key, row) in expected)
                {
                    Assert.True(actual.TryGetValue(key, out var visible), $"table {store.Schema.GetName(table)} at depth {context.Depth}: key [{key}] missing");
                    for (var c = 0; c < row.Length; c++)
                        if (c != sort)
                            Assert.Equal(row[c], visible![c]);
                }
            }
        }

        /// <summary>
        /// Gets what the root sees, from its tables.
        /// </summary>
        Dictionary<TableId, Dictionary<string, uint[]>> Actual(Context root)
        {
            var result = new Dictionary<TableId, Dictionary<string, uint[]>>();
            foreach (var table in tables)
            {
                var keyCount = store.Schema[table]!.KeyCount;
                result[table] = TableTestUtil.LiveRows(root.Database.GetTable(table)).ToDictionary(row => LayeredTestUtil.KeyOf(row.AsSpan(0, keyCount)), row => row);
            }

            return result;
        }

    }

}
