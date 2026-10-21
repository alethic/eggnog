using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's extractor run literally over a <see cref="RandomExtractionGraph"/>, independently of the engine's: the
    /// passes of <c>bellman_ford</c> over the rows in egglog's order, stamping every update with the next rank until a
    /// pass changes nothing, then each class's parent edge, the first row in the same order that costs the class's cost
    /// and whose class's rank exceeds every child's (src/extract.rs:407-499); and the variants of a class sorted by cost,
    /// function name and raw row values (src/extract.rs:651-703). Where a cost saturated at <see cref="ulong.MaxValue"/>
    /// leaves a class no parent edge, on which egglog's reconstruction panics, it takes eggnog's fallback: the first row
    /// whose children all had their first updates before the class had its own.
    /// </summary>
    sealed class EgglogExtractionReference
    {

        readonly RandomExtractionGraph graph;
        readonly Dictionary<uint, long> ranks = new();
        readonly Dictionary<uint, long> firstRanks = new();

        /// <summary>
        /// Runs egglog's extractor over <paramref name="graph"/>.
        /// </summary>
        /// <param name="graph">The graph.</param>
        public EgglogExtractionReference(RandomExtractionGraph graph)
        {
            this.graph = graph;

            // Every function produces E, so the breadth-first search from E lists them in declaration order; each
            // table's rows come in storage order, subsumed rows skipped.
            Rows = new List<(int Function, Value[] Row)>();
            foreach (var kind in Enumerable.Range(0, graph.Functions.Length).OrderBy(k => graph.Functions[k].Rep))
            {
                graph.Fx.EGraph.ForEach(graph.Functions[kind], row =>
                {
                    if (row.Subsumed == false)
                        Rows.Add((kind, row.Values.ToArray()));
                });
            }

            Costs = new Dictionary<uint, ulong>();
            long rank = 0;
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var (function, row) in Rows)
                {
                    if (graph.NodeCost(function, row, Costs) is not { } cost)
                        continue;

                    var owner = row[^1].Rep;
                    var known = Costs.TryGetValue(owner, out var current);
                    if (known == false || cost < current)
                    {
                        Costs[owner] = cost;
                        ranks[owner] = ++rank;
                        if (known == false)
                            firstRanks[owner] = rank;

                        changed = true;
                    }
                }
            }

            Choices = new Dictionary<uint, (int Function, Value[] Row)>();
            foreach (var (function, row) in Rows)
            {
                var owner = row[^1].Rep;
                if (Choices.ContainsKey(owner) || Costs.TryGetValue(owner, out var best) == false)
                    continue;

                if (graph.NodeCost(function, row, Costs) != best)
                    continue;

                var childRank = graph.ChildClasses(function, row).Select(c => ranks[c]).DefaultIfEmpty(0).Max();
                if (ranks[owner] > childRank)
                    Choices[owner] = (function, row);
            }

            var missing = Costs.Keys.Where(c => Choices.ContainsKey(c) == false).ToHashSet();
            Fallbacks = new HashSet<uint>();
            foreach (var (function, row) in Rows)
            {
                var owner = row[^1].Rep;
                if (missing.Contains(owner) == false || Fallbacks.Contains(owner))
                    continue;

                if (graph.NodeCost(function, row, Costs) != Costs[owner])
                    continue;

                var childRank = graph.ChildClasses(function, row).Select(c => firstRanks[c]).DefaultIfEmpty(0).Max();
                if (firstRanks[owner] > childRank)
                {
                    Choices[owner] = (function, row);
                    Fallbacks.Add(owner);
                }
            }
        }

        /// <summary>
        /// Gets the rows in the order egglog's passes meet them.
        /// </summary>
        public List<(int Function, Value[] Row)> Rows { get; }

        /// <summary>
        /// Gets the cost of every class with a term.
        /// </summary>
        public Dictionary<uint, ulong> Costs { get; }

        /// <summary>
        /// Gets each class's parent edge, or its fallback.
        /// </summary>
        public Dictionary<uint, (int Function, Value[] Row)> Choices { get; }

        /// <summary>
        /// Gets the saturated classes egglog finds no parent edge for, which take the fallback.
        /// </summary>
        public HashSet<uint> Fallbacks { get; }

        /// <summary>
        /// Gets up to <paramref name="count"/> variants of <paramref name="owner"/>: its rows with a cost, sorted by cost,
        /// then function name, then raw row values.
        /// </summary>
        public List<(ulong Cost, int Function, Value[] Row)> Variants(uint owner, int count)
        {
            var variants = new List<(ulong Cost, int Function, Value[] Row)>();
            foreach (var (function, row) in Rows)
                if (row[^1].Rep == owner && graph.NodeCost(function, row, Costs) is { } cost)
                    variants.Add((cost, function, row));

            variants.Sort((x, y) =>
            {
                if (x.Cost != y.Cost)
                    return x.Cost.CompareTo(y.Cost);

                var names = string.CompareOrdinal(graph.Names[x.Function], graph.Names[y.Function]);
                if (names != 0)
                    return names;

                return x.Row.Select(v => v.Rep).ToArray().AsSpan().SequenceCompareTo(y.Row.Select(v => v.Rep).ToArray());
            });

            return variants.Take(count).ToList();
        }

    }

}
