using System;
using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An engine <see cref="Context"/> behind the spike's context API (colored-contexts spec §17 M9.4, X32), so the spike's
    /// differential programs drive the engine: ids, unions, rows of functions and relations, get-or-add of constructors,
    /// lookups, scans by column and since a clock reading, plus the retractions the spike lacks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows are logical (keys, then the output); the facade appends the engine's timestamp, the write batch's epoch. Every
    /// write canonicalizes its id cells first, as the spike's <c>Insert</c> and <c>GetOrAdd</c> do (<c>Canonicalize</c>
    /// after <c>Prepare</c>): through the context's find (<see cref="Context.UnionFind"/>), inside the batch, where the
    /// context has caught up with its parent but may still have unions of its own to rebuild over, so its public
    /// <see cref="Context.Find"/>, which requires a clean context, cannot be used there. A child canonicalizes again at the
    /// merge (X38); at the root this is what keeps the program's raw writes canonical, as egglog's callers keep theirs.
    /// </para>
    /// <para>
    /// A get-or-add predicts the row (<see cref="ExecutionState.PredictRow(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, Span{uint})"/>):
    /// before a rebuild a lookup may miss a row whose key is stale and mint a new class, which the rebuild then merges, so
    /// the answer is late rather than wrong, as in the spike. A retraction rebuilds first and deletes the row with the key
    /// made canonical, so it applies to the closed view (<see cref="SpikeReferenceView.Delete"/>). <see cref="Rebuild"/>
    /// also runs the engine's invariant checker.
    /// </para>
    /// </remarks>
    sealed class SpikeContextFacade
    {

        /// <summary>
        /// Initializes a facade over <paramref name="context"/>.
        /// </summary>
        public SpikeContextFacade(Context context, SpikeTestSchema schema)
        {
            Context = context;
            Schema = schema;
        }

        /// <summary>
        /// Gets the engine context.
        /// </summary>
        public Context Context { get; }

        /// <summary>
        /// Gets the schema.
        /// </summary>
        public SpikeTestSchema Schema { get; }

        /// <summary>
        /// Gets whether <see cref="Insert"/> passes rows raw: id cells as given and timestamp 0, leaving canonicalizing
        /// and stamping to the child's merge (X38). For children only.
        /// </summary>
        public bool RawWrites { get; init; }

        /// <summary>
        /// Gets the store's clock: rows written afterwards carry a timestamp at least this large.
        /// </summary>
        public uint Clock => Context.Store.ReadClock();

        /// <summary>
        /// Gets whether the context may retract rows of a shared table (§6.7): no live descendant, and the root or a child
        /// of a frozen parent (R1).
        /// </summary>
        public bool CanRetract => Context.Gate.HasLiveDescendants == false && (Context.IsRoot || Context.Parent!.IsFrozen);

        /// <summary>
        /// Mints an id.
        /// </summary>
        public uint NewId() => Context.NewId();

        /// <summary>
        /// Unions two ids; rows are brought up to date by the next rebuild.
        /// </summary>
        public bool Union(uint a, uint b) => Context.Union(a, b);

        /// <summary>
        /// Writes a logical row, its id cells canonicalized.
        /// </summary>
        public void Insert(SpikeTable table, ReadOnlySpan<ulong> row)
        {
            var cells = new uint[table.EngineArity];
            for (var i = 0; i < table.Arity; i++)
                cells[i] = checked((uint)row[i]);

            Context.Write((Facade: this, Table: table, Cells: cells), static (state, args) =>
            {
                if (args.Facade.RawWrites == false)
                {
                    args.Facade.Canonicalize(args.Table, args.Cells, args.Table.Arity);
                    args.Cells[args.Table.Arity] = state.Timestamp;
                }

                state.StageInsert(args.Table.Id, args.Cells);
                return 0;
            });
        }

        /// <summary>
        /// Gets the class a constructor maps <paramref name="key"/> to, minting one for it if the context has none.
        /// </summary>
        public uint GetOrAdd(SpikeTable table, ReadOnlySpan<ulong> key, out bool created)
        {
            var cells = new uint[table.KeyCount];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = checked((uint)key[i]);

            var (output, made) = Context.Write((Facade: this, Table: table, Key: cells), static (state, args) =>
            {
                args.Facade.Canonicalize(args.Table, args.Key, args.Table.KeyCount);
                var before = state.PredictedCount;
                MergeVal[] values = [MergeVal.FromCounter(args.Facade.Context.Store.IdCounter), MergeVal.FromConstant(new Value(state.Timestamp))];
                var row = state.PredictRow(args.Table.Id, args.Key, values);
                return (row[args.Table.KeyCount], state.PredictedCount > before);
            });

            created = made;
            return made ? output : Context.UnionFind.Find(output);
        }

        /// <summary>
        /// Retracts the row with key <paramref name="key"/>: rebuilds, then deletes the row with the key made canonical.
        /// The caller has checked <see cref="CanRetract"/>.
        /// </summary>
        /// <returns>The canonical key deleted.</returns>
        public uint[] Delete(SpikeTable table, ReadOnlySpan<ulong> key)
        {
            Rebuild();
            var cells = new uint[table.KeyCount];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = table.IsId[i] ? Context.Find(checked((uint)key[i])) : checked((uint)key[i]);

            Context.Write((Table: table, Key: cells), static (state, args) =>
            {
                state.StageRemove(args.Table.Id, args.Key);
                return 0;
            });

            return cells;
        }

        /// <summary>
        /// Asserts that a retraction from <paramref name="table"/> is refused (§6.7) without changing anything: a staged
        /// removal and a clear both throw, and the context is not faulted.
        /// </summary>
        public void AssertRetractionRefused(SpikeTable table, ReadOnlySpan<ulong> key)
        {
            var cells = new uint[table.KeyCount];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = checked((uint)key[i]);

            var state = Context.Database.CreateExecutionState();
            Assert.Throws<InvalidOperationException>(() => state.StageRemove(table.Id, cells));
            Assert.Throws<InvalidOperationException>(() => Context.ClearTable(table.Id));
            Assert.False(Context.IsFaulted);
        }

        /// <summary>
        /// Clears <paramref name="table"/>; the caller has checked <see cref="CanRetract"/>.
        /// </summary>
        public void Clear(SpikeTable table)
        {
            Rebuild();
            Context.ClearTable(table.Id);
        }

        /// <summary>
        /// Rebuilds the context and checks its invariants (§12.7).
        /// </summary>
        public void Rebuild()
        {
            Context.Rebuild();
            if (Context.IsFrozen == false)
                Context.CheckInvariants();
        }

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/>; the context must be clean.
        /// </summary>
        public uint Find(uint id) => Context.Find(id);

        /// <summary>
        /// Looks up a function's output for <paramref name="key"/>, the key made canonical; the output is canonical.
        /// </summary>
        public bool TryLookup(SpikeTable table, ReadOnlySpan<ulong> key, out ulong output)
        {
            var row = new uint[table.EngineArity];
            if (Context.TryGetRow(table.Id, CanonicalKey(table, key), row) == false)
            {
                output = 0;
                return false;
            }

            output = table.OutputIsId ? Context.Find(row[table.KeyCount]) : row[table.KeyCount];
            return true;
        }

        /// <summary>
        /// Gets whether a key has a row, the key made canonical.
        /// </summary>
        public bool Contains(SpikeTable table, ReadOnlySpan<ulong> key)
        {
            var row = new uint[table.EngineArity];
            return Context.TryGetRow(table.Id, CanonicalKey(table, key), row);
        }

        /// <summary>
        /// Gets the logical rows of <paramref name="table"/> the context sees with a timestamp of at least
        /// <paramref name="since"/>.
        /// </summary>
        public List<ulong[]> Rows(SpikeTable table, uint since = 0)
        {
            var result = new List<ulong[]>();
            foreach (var (_, _, cells) in LayeredTestUtil.ScanVisible(Context.GetView(table.Id)))
                if (cells[table.Arity] >= since)
                    result.Add(Logical(table, cells));

            return result;
        }

        /// <summary>
        /// Gets the logical rows of <paramref name="table"/> whose <paramref name="column"/> holds
        /// <paramref name="value"/> (made canonical for an id column). In a child, an id column is answered through the lift
        /// reads (§6.10): the own layer's rows (<see cref="Context.ScanOwn{TSink}"/>) and the ancestor rows naming the id
        /// (<see cref="Context.ScanAncestorsMentioning{TSink}"/>); otherwise by a scan of the view.
        /// </summary>
        public List<ulong[]> ScanColumn(SpikeTable table, int column, ulong value)
        {
            var result = new List<ulong[]>();
            if (table.IsId[column])
                value = Context.Find(checked((uint)value));

            if (Context.IsRoot || table.IsId[column] == false)
            {
                foreach (var row in Rows(table))
                    if (row[column] == value)
                        result.Add(row);

                return result;
            }

            var own = new RowCollector();
            Context.ScanOwn(table.Id, ref own);
            var ancestors = new RowCollector();
            Context.ScanAncestorsMentioning(table.Id, (uint)value, ref ancestors);
            foreach (var (_, cells) in own.Rows)
                if (cells[column] == value)
                    result.Add(Logical(table, cells));

            foreach (var (_, cells) in ancestors.Rows)
                if (cells[column] == value)
                    result.Add(Logical(table, cells));

            return result;
        }

        /// <summary>
        /// Rewrites the first <paramref name="count"/> cells of <paramref name="cells"/> that hold ids to their canonical
        /// ids in the context (inside a write batch, after the context caught up with its parent).
        /// </summary>
        void Canonicalize(SpikeTable table, uint[] cells, int count)
        {
            var find = Context.UnionFind;
            for (var i = 0; i < count; i++)
                if (table.IsId[i])
                    cells[i] = find.Find(cells[i]);
        }

        /// <summary>
        /// Copies a key with its ids made canonical; the context must be clean.
        /// </summary>
        uint[] CanonicalKey(SpikeTable table, ReadOnlySpan<ulong> key)
        {
            var cells = new uint[table.KeyCount];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = table.IsId[i] ? Context.Find(checked((uint)key[i])) : checked((uint)key[i]);

            return cells;
        }

        /// <summary>
        /// Drops the timestamp of an engine row.
        /// </summary>
        static ulong[] Logical(SpikeTable table, uint[] cells)
        {
            var row = new ulong[table.Arity];
            for (var i = 0; i < row.Length; i++)
                row[i] = cells[i];

            return row;
        }

    }

}
