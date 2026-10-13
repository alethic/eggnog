using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The oracle of the bridge differential suite: a naive e-graph and a naive rule evaluator. The e-graph is the
    /// database tests' <see cref="ReferenceView"/> extended to the bridge's functions: a union-find that keeps the
    /// smallest id canonical, one dictionary of rows per function keyed by canonical keys of any length
    /// (<see cref="BridgeDiffKey"/>), and <see cref="Close"/>, which canonicalizes every row and merges rows that meet
    /// on a key until no merge unions anything. Merges follow the function's kind: union for id outputs (constructors,
    /// relations, whose rows have ids of their own, and id functions), minimum or maximum for the integer lattices, and
    /// the maximum of the subsumption flags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here is incremental, timestamped, planned or batched, which is the point: <see cref="Step"/> re-matches
    /// every rule against the whole closed e-graph by nested loops (<see cref="Match"/>), applies every action of every
    /// match, and closes again. That is the bridge's semantics with none of its machinery: Jacobi iterations (every
    /// rule matches the same snapshot, lookups read the snapshot, and writes land together afterwards), a fresh id per
    /// missing constructor or relation key, and congruence closure. The bridge is tested by agreeing with it after
    /// every seed phase and every iteration, modulo the choice of ids (<see cref="BridgeDiffComparer"/>).
    /// </para>
    /// <para>
    /// The only coupling to the bridge is numbering: ids are minted densely from zero, one per term seed and relation
    /// insertion (as <see cref="EGraph.AddTerm"/> mints them) and one per row of a bulk load, in order, so the ids the
    /// seeds mint coincide (rules can name them as constants); ids minted by rules need not, and are matched by the
    /// comparison.
    /// </para>
    /// </remarks>
    sealed partial class BridgeDiffOracle
    {

        readonly BridgeDiffProgram program;
        readonly uint unit;
        readonly List<uint> parent = new();
        readonly Dictionary<BridgeDiffKey, (uint Output, bool Subsumed)>[] tables;
        readonly uint[] seedIds;
        int nextSeed;
        int initialIds = -1;
        int unionsSinceClose;

        /// <summary>
        /// Initializes an empty oracle for <paramref name="program"/>.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="unit">The cell of the unit value, as the e-graphs store it.</param>
        public BridgeDiffOracle(BridgeDiffProgram program, uint unit)
        {
            this.program = program;
            this.unit = unit;
            tables = program.Functions.Select(_ => new Dictionary<BridgeDiffKey, (uint, bool)>()).ToArray();
            seedIds = Enumerable.Repeat(uint.MaxValue, program.Seeds.Length).ToArray();
        }

        /// <summary>
        /// Gets the number of ids minted.
        /// </summary>
        public int IdCount => parent.Count;

        /// <summary>
        /// Gets the total number of rows.
        /// </summary>
        public int TotalRows => tables.Sum(t => t.Count);

        /// <summary>
        /// Finds the canonical id of <paramref name="id"/>.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The smallest id of its class.</returns>
        public uint Find(uint id)
        {
            while (parent[(int)id] != id)
            {
                // Path halving.
                var next = parent[(int)id];
                parent[(int)id] = parent[(int)next];
                id = next;
            }

            return id;
        }

        /// <summary>
        /// Unions the classes of <paramref name="a"/> and <paramref name="b"/>, keeping the smaller id canonical.
        /// </summary>
        /// <param name="a">An id.</param>
        /// <param name="b">An id.</param>
        /// <returns><c>true</c> if the classes were distinct.</returns>
        public bool Union(uint a, uint b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b)
                return false;

            parent[(int)Math.Max(a, b)] = Math.Min(a, b);
            unionsSinceClose++;
            return true;
        }

        /// <summary>
        /// Runs the seeds of phase <paramref name="phase"/>, which must come next, closing after each.
        /// </summary>
        /// <param name="phase">The phase: 0 before the rules, <c>k</c> before iteration <c>k</c>.</param>
        public void RunSeeds(int phase)
        {
            var seeds = program.Seeds;
            for (; nextSeed < seeds.Length && seeds[nextSeed].Phase == phase; nextSeed++)
            {
                var seed = seeds[nextSeed];
                var args = seed.Args.Select(Resolve).ToArray();
                switch (seed.Kind)
                {
                    case BridgeDiffSeedKind.Term:
                    case BridgeDiffSeedKind.Set when seed.IsInsertion(program.Functions):
                    {
                        // AddTerm mints an id even for a term that exists, and the merge unions it into the term's
                        // class.
                        var id = Mint();
                        var keys = args.Take(program.Functions[seed.Function].KeyCount);
                        Write(seed.Function, keys.Append(id).ToArray(), false);
                        seedIds[nextSeed] = id;
                        break;
                    }
                    case BridgeDiffSeedKind.Union:
                        Union(args[0], args[1]);
                        break;
                    case BridgeDiffSeedKind.Bulk:
                        for (var k = args[0]; k < args[0] + args[1]; k++)
                            Write(seed.Function, [k, Mint()], false);

                        break;
                    default:
                        Write(seed.Function, args, false);
                        break;
                }

                Close();
            }

            if (phase == 0)
                initialIds = IdCount;
        }

        /// <summary>
        /// Takes the oracle's view: the canonical id of every id minted, and every function's rows.
        /// </summary>
        /// <returns>The view.</returns>
        public BridgeDiffView View()
        {
            var find = new uint[parent.Count];
            for (var id = 0; id < find.Length; id++)
                find[id] = Find((uint)id);

            var rows = new List<BridgeDiffRow>[tables.Length];
            for (var f = 0; f < tables.Length; f++)
                rows[f] = tables[f].Select(e => new BridgeDiffRow(Cells(e.Key, e.Value.Output), e.Value.Subsumed)).ToList();

            return new BridgeDiffView(find, rows, (uint[])seedIds.Clone(), initialIds);
        }

        /// <summary>
        /// Closes the e-graph: canonicalizes every row and merges rows that meet on a key, until a pass unions nothing.
        /// The <c>Close</c> of <see cref="ReferenceView"/>, with the bridge's merges. Without a union since the last
        /// close the tables are closed already (<see cref="Write"/> keeps keys canonical and merges in place), and
        /// nothing is done.
        /// </summary>
        void Close()
        {
            if (unionsSinceClose == 0)
                return;

            bool unioned;
            do
            {
                unioned = false;
                for (var f = 0; f < tables.Length; f++)
                {
                    var function = program.Functions[f];
                    var old = tables[f];
                    var closed = new Dictionary<BridgeDiffKey, (uint Output, bool Subsumed)>(old.Count);
                    foreach (var (key, row) in old)
                    {
                        var canonical = CanonicalKey(function, key);
                        var output = function.OutputIsId ? Find(row.Output) : row.Output;
                        if (closed.TryGetValue(canonical, out var existing))
                        {
                            unioned |= Merge(function, existing, output, row.Subsumed, out var merged);
                            closed[canonical] = merged;
                        }
                        else
                        {
                            closed[canonical] = (output, row.Subsumed);
                        }
                    }

                    tables[f] = closed;
                }
            }
            while (unioned);

            unionsSinceClose = 0;
        }

        /// <summary>
        /// Writes a row (keys then output) into function <paramref name="f"/>, merging it with the row the function
        /// holds for the key. Does not close.
        /// </summary>
        /// <returns>Whether the write changed anything: a new key, a union, a new lattice value or a new
        /// subsumption.</returns>
        bool Write(int f, uint[] cells, bool subsumed)
        {
            var function = program.Functions[f];
            var key = CanonicalKey(function, new BridgeDiffKey(cells, function.KeyCount));
            var output = cells[function.KeyCount];
            if (function.OutputIsId)
                output = Find(output);

            var table = tables[f];
            if (table.TryGetValue(key, out var existing) == false)
            {
                table[key] = (output, subsumed);
                return true;
            }

            var unioned = Merge(function, existing, output, subsumed, out var merged);
            table[key] = merged;
            return unioned || merged != existing;
        }

        /// <summary>
        /// Merges an incoming output and subsumption flag into an existing row with the function's merge.
        /// </summary>
        /// <returns>Whether the merge made an effective union.</returns>
        bool Merge(BridgeDiffFunction function, (uint Output, bool Subsumed) existing, uint output, bool subsumed, out (uint Output, bool Subsumed) merged)
        {
            var unioned = false;
            var result = existing.Output;
            switch (function.Kind)
            {
                case BridgeDiffFunctionKind.Constructor:
                case BridgeDiffFunctionKind.Relation:
                case BridgeDiffFunctionKind.IdFunction:
                    unioned = Union(existing.Output, output);
                    result = Find(existing.Output);
                    break;
                case BridgeDiffFunctionKind.Min:
                    result = Math.Min(existing.Output, output);
                    break;
                case BridgeDiffFunctionKind.Max:
                    result = Math.Max(existing.Output, output);
                    break;
            }

            merged = (result, existing.Subsumed || subsumed);
            return unioned;
        }

        /// <summary>
        /// Gets the cell of a constant entry: its value, or the unit value for a constant of the unit sort.
        /// </summary>
        uint Constant(BridgeDiffEntry entry) => entry.Sort == BridgeDiffSort.Unit ? unit : entry.Value;

        /// <summary>
        /// Mints a fresh id.
        /// </summary>
        uint Mint()
        {
            var id = (uint)parent.Count;
            parent.Add(id);
            return id;
        }

        /// <summary>
        /// Canonicalizes the id cells of a key; returns the key itself when they already are.
        /// </summary>
        BridgeDiffKey CanonicalKey(BridgeDiffFunction function, BridgeDiffKey key)
        {
            for (var c = 0; c < key.Count; c++)
            {
                if (function.IsIdColumn(c) == false || Find(key[c]) == key[c])
                    continue;

                var cells = key.Cells.ToArray();
                for (var d = c; d < cells.Length; d++)
                    if (function.IsIdColumn(d))
                        cells[d] = Find(cells[d]);

                return new BridgeDiffKey(cells, cells.Length);
            }

            return key;
        }

        /// <summary>
        /// Rebuilds the cells of a row, keys then output, from its dictionary entry.
        /// </summary>
        static uint[] Cells(BridgeDiffKey key, uint output)
        {
            var cells = new uint[key.Count + 1];
            key.Cells.CopyTo(cells);
            cells[key.Count] = output;
            return cells;
        }

        /// <summary>
        /// Resolves a seed argument: the canonical id of an earlier term seed's class, or the constant.
        /// </summary>
        uint Resolve(BridgeDiffEntry entry)
        {
            if (entry.IsVar == false)
                return Constant(entry);

            var id = seedIds[entry.Var];
            if (id == uint.MaxValue)
                throw new InvalidOperationException($"Seed $s{entry.Var} produced no id.");

            return Find(id);
        }

    }

}
