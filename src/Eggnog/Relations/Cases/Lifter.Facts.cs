using System;

namespace Eggnog.Relations
{

    internal sealed partial class Lifter
    {

        /// <summary>
        /// The parent keys being built for one source row (the candidate), and the source row's key cells, whose member
        /// lists the candidate's id cells range over.
        /// </summary>
        uint[] keyCells = new uint[8];
        uint[] candidate = new uint[8];
        int[] positions = new int[8];

        /// <summary>
        /// Resolves the spec's tables against the parent's e-graph for this lift: the contradiction function is left
        /// out (<see cref="ContradictParent"/> writes it), and so is a table the parent cannot be written (a child
        /// parent and a merge that is not a lattice merge, which only the root may write), since no case could hold its
        /// own rows of it either.
        /// </summary>
        /// <exception cref="ArgumentException">A function is not declared; a constructor's output is not an id; a relation's
        /// output is an id (it would be written as the seed case's id, which can be local to it); or a case join is
        /// given for an id output other than a container of values, or for a kind other than
        /// <see cref="LiftTableKind.Function"/>.</exception>
        void Resolve(EGraphContext parent, LiftSpec spec)
        {
            tables.Clear();
            var graph = parent.Owner;
            var contradiction = graph.Contradiction;
            foreach (var table in spec.Tables)
            {
                if (table.Function == contradiction)
                    continue;

                var info = graph.GetFunctionInfo(table.Function);
                if (table.Kind == LiftTableKind.Constructor && info.OutputType.IsId == false)
                    throw new ArgumentException($"{info.Name} is lifted as a constructor, but its output is not an id.", nameof(spec));

                if (table.Kind == LiftTableKind.Relation && info.OutputType.IsId)
                    throw new ArgumentException($"{info.Name} is lifted as a relation, but its output is an id; lift it as a constructor or a function.", nameof(spec));

                if (table.CaseJoin is not null && table.Kind != LiftTableKind.Function)
                    throw new ArgumentException($"{info.Name} is lifted as a {table.Kind}; a case join applies to functions only.", nameof(spec));

                if (table.CaseJoin is not null && info.OutputType.IsId && table.ValueOutput == false)
                    throw new ArgumentException($"{info.Name} has an id output; a case join combines base values and containers of them only.", nameof(spec));

                if (parent.IsRoot == false && graph.Store.Schema[info.Table] is { IsLattice: false, IsContextPrivate: false })
                    continue;

                tables.Add(new LiftedTable(table, info));
            }
        }

        /// <summary>
        /// Stages the facts of every listed table that hold in every live case and not yet in the parent, found through
        /// the seed case's source rows (<see cref="CollectSources"/>).
        /// </summary>
        /// <param name="parent">The parent; clean.</param>
        /// <param name="seed">The seed case's index in the live cases.</param>
        /// <param name="options">The limits.</param>
        /// <param name="report">Counts the writes and the skipped keys.</param>
        void LiftFacts(EGraphContext parent, int seed, LiftOptions options, LiftReport report)
        {
            if (tables.Count == 0)
                return;

            if (outputs.Length < live.Count)
                outputs = new uint[live.Count];

            foreach (var table in tables)
            {
                CollectSources(table, seed);
                Expand(table, seed, options, report);
                for (var i = 0; i < candidates.Count; i++)
                    Decide(parent, table, seed, candidates[i], report);
            }
        }

        /// <summary>
        /// Collects the seed case's source rows of <paramref name="table"/> into <see cref="sources"/>: (a) its own
        /// rows; (b) every ancestor row it sees whose key names one of its group roots that is a parent id (the winner
        /// probe), which losers-only marking never copies into the case.
        /// </summary>
        void CollectSources(LiftedTable table, int seed)
        {
            var keyCount = table.KeyCount;
            sources.Reset(keyCount + 1, keyCount);
            var @case = live[seed];
            var function = table.Table.Function;
            var sink = new LiftKeySink(sources, table.IdKeys, keyCount);
            @case.ScanOwn(function, ref sink);
            if (table.HasIdKey == false)
                return;

            var map = maps[seed];
            var roots = map.GroupRoots;
            for (var g = 0; g < roots.Length; g++)
            {
                var root = roots[g];
                if (map.IsLocal(root))
                    continue;

                sink.FilterOn(root);
                @case.ScanAncestorsMentioning(function, root, ref sink);
            }
        }

        /// <summary>
        /// Expands every source row into the parent keys it stands for: each id key cell ranges over its member
        /// list, other cells stay. A row with an empty list (a local class with no parent member) stands for no parent key;
        /// a row standing for more than <see cref="LiftOptions.CandidateLimit"/> keys is skipped and its keys counted.
        /// Candidates keep the source row's output.
        /// </summary>
        void Expand(LiftedTable table, int seed, LiftOptions options, LiftReport report)
        {
            var keyCount = table.KeyCount;
            candidates.Reset(keyCount + 1, keyCount);
            EnsureScratch(keyCount + 1);
            var map = maps[seed];
            var idKeys = table.IdKeys;
            for (var s = 0; s < sources.Count; s++)
            {
                var source = sources[s];
                source.CopyTo(keyCells);
                long product = 1;
                for (var i = 0; i < keyCount && product > 0; i++)
                {
                    positions[i] = 0;
                    if (idKeys[i])
                        product = SaturatingMultiply(product, map.Members(in keyCells[i]).Length);
                }

                if (product == 0)
                    continue;

                if (product > options.CandidateLimit)
                {
                    report.Skipped = SaturatingAdd(report.Skipped, product);
                    continue;
                }

                // An odometer over the id key cells' member lists.
                candidate[keyCount] = keyCells[keyCount];
                while (true)
                {
                    for (var i = 0; i < keyCount; i++)
                        candidate[i] = idKeys[i] ? map.Members(in keyCells[i])[positions[i]] : keyCells[i];

                    candidates.Add(candidate.AsSpan(0, keyCount + 1));
                    var digit = keyCount - 1;
                    while (digit >= 0)
                    {
                        if (idKeys[digit] && ++positions[digit] < map.Members(in keyCells[digit]).Length)
                            break;

                        positions[digit] = 0;
                        digit--;
                    }

                    if (digit < 0)
                        break;
                }
            }
        }

        /// <summary>
        /// Decides one candidate parent key by the table's kind and stages what holds in every live case and not yet in
        /// the parent.
        /// </summary>
        /// <param name="parent">The parent.</param>
        /// <param name="table">The table.</param>
        /// <param name="seed">The seed case's index.</param>
        /// <param name="entry">The candidate: the parent key, then the seed case's output for it.</param>
        /// <param name="report">Counts the writes.</param>
        void Decide(EGraphContext parent, LiftedTable table, int seed, ReadOnlySpan<uint> entry, LiftReport report)
        {
            var info = table.Info;
            var keyCount = table.KeyCount;
            var key = entry.Slice(0, keyCount);
            var function = table.Table.Function;

            // The seed holds the key by construction: its output is the source row's, canonical in the seed.
            outputs[seed] = table.OutputIsId ? finders[seed].Find(entry[keyCount]) : entry[keyCount];
            for (var j = 0; j < live.Count; j++)
            {
                if (j == seed)
                    continue;

                if (live[j].LiftLookup(info, key, out var value) == false)
                    return;

                outputs[j] = table.OutputIsId ? finders[j].Find(value) : value;
            }

            var inParent = parent.LiftLookup(info, key, out var existing);
            switch (table.Table.Kind)
            {
                case LiftTableKind.Relation:
                    if (inParent)
                        return;

                    // A base output (Resolve refuses an id): written only when every live case holds the same value, so
                    // a value one live case does not hold is never lifted. A unit relation always agrees.
                    for (var j = 0; j < live.Count; j++)
                        if (outputs[j] != outputs[seed])
                            return;

                    writes.AddRow(function, key, outputs[seed]);
                    report.Rows++;
                    return;

                case LiftTableKind.Constructor:
                    // A term the parent has already: any equality of its output comes from the meet.
                    if (inParent)
                        return;

                    if (TryCommonParentId(seed, out var member))
                    {
                        writes.AddRow(function, key, member);
                        report.Rows++;
                    }
                    else
                    {
                        writes.AddTerm(function, key);
                        report.Terms++;
                    }

                    return;

                default:
                    if (table.OutputIsId)
                    {
                        // A key the parent holds already adds nothing when every live case's output is the class of the
                        // stored output there: any equality between it and another parent id is the meet's
                        // (Lifter.Equalities). A UnionId or AssertEq merge always lands here, because the case sees the
                        // parent's row; writing a second output next to the stored one would merge two ids in one flush
                        // before the meet's union applies, which an AssertEq merge refuses. Any other merge (a declared
                        // lattice over ids) can move a case's output off the stored class, and then the common parent id
                        // is written over the stored one, as for a base output. Such an id is never equal to the stored
                        // one in the parent: each case's find extends the parent's, so it would map both to one class.
                        if (inParent && HoldsStoredClass(existing))
                            return;

                        if (TryCommonParentId(seed, out var output) == false)
                            return;

                        writes.AddRow(function, key, output);
                        report.Rows++;
                        return;
                    }

                    var joined = outputs[seed];
                    var join = table.Table.CaseJoin;
                    for (var j = 0; j < live.Count; j++)
                    {
                        if (j == seed)
                            continue;

                        if (join is not null)
                            joined = join(joined, outputs[j]);
                        else if (outputs[j] != joined)
                            return;
                    }

                    if (inParent && existing == joined)
                        return;

                    writes.AddRow(function, key, joined);
                    report.Values++;
                    return;
            }
        }

        /// <summary>
        /// Returns whether every live case's output (an id) is the case's class of <paramref name="stored"/>, the id
        /// the parent holds for the key. Costs one find per live case; allocates nothing.
        /// </summary>
        /// <param name="stored">The parent's stored output for the key.</param>
        bool HoldsStoredClass(uint stored)
        {
            for (var j = 0; j < live.Count; j++)
                if (finders[j].Find(stored) != outputs[j])
                    return false;

            return true;
        }

        /// <summary>
        /// Finds the first parent id that every live case's output stands for: a member of the seed's output whose
        /// canonical id in each other live case is that case's output. A member of a block the round withheld
        /// (<see cref="containerMerged"/>) is passed over: the row would rest on an equality of containers the parent
        /// does not hold, and the reasoning of the id-output and constructor rows relies on the meet's equalities being
        /// lifted with them. One count test when the round withheld nothing.
        /// </summary>
        bool TryCommonParentId(int seed, out uint member)
        {
            var seedOutput = outputs[seed];
            var members = maps[seed].Members(in seedOutput);
            foreach (var m in members)
            {
                if (containerMerged.Count > 0 && containerMerged.Contains(m))
                    continue;

                var all = true;
                for (var j = 0; j < live.Count && all; j++)
                    if (j != seed && finders[j].Find(m) != outputs[j])
                        all = false;

                if (all)
                {
                    member = m;
                    return true;
                }
            }

            member = 0;
            return false;
        }

        /// <summary>
        /// Grows the per-key scratch arrays to at least <paramref name="width"/> cells.
        /// </summary>
        void EnsureScratch(int width)
        {
            if (keyCells.Length >= width)
                return;

            keyCells = new uint[width];
            candidate = new uint[width];
            positions = new int[width];
        }

        static long SaturatingMultiply(long a, int b) => b == 0 ? 0 : a > long.MaxValue / b ? long.MaxValue : a * b;

        static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    }

}
