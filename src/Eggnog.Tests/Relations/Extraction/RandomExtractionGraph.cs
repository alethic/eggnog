using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A small random e-graph over one e-class sort <c>E</c>, with leaves, a base-valued constructor, unary and binary
    /// constructors and a constructor over vectors of <c>E</c>, random unions and random subsumptions; and an
    /// independent reference for its extraction: costs by a Bellman-Ford fixpoint over the rows, and the cheapest terms
    /// of bounded depth by brute-force enumeration.
    /// </summary>
    sealed class RandomExtractionGraph
    {

        /// <summary>
        /// How many of the cheapest terms the enumeration keeps per (class, depth).
        /// </summary>
        const int Keep = 64;

        readonly Dictionary<(uint Class, int Depth), List<(ulong Cost, string Text)>> enumerated = new();

        /// <summary>
        /// Builds the graph of <paramref name="seed"/>.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <param name="positiveCosts">Whether head costs are drawn from 1 to 4 rather than 0 to 3 (some seeds also use
        /// a third of <see cref="ulong.MaxValue"/>).</param>
        /// <param name="costChoices">When given, the head costs to draw from instead, uniformly; the functions are then
        /// also declared in a random order, so that the extractor's tie-break by function does not always put leaves
        /// first.</param>
        public RandomExtractionGraph(int seed, bool positiveCosts, ulong[]? costChoices = null)
        {
            var random = new Random(seed);
            Fx = new ExtractionFixture();
            E = new EqSort("E");
            Vec = new TestVecSort("VecE", E);

            ulong Cost() => costChoices is not null
                ? costChoices[random.Next(costChoices.Length)]
                : seed % 7 == 3 && random.Next(4) == 0 ? ulong.MaxValue / 3 : positiveCosts ? (ulong)random.Next(1, 5) : (ulong)random.Next(0, 4);

            var names = new[] { "L0", "L1", "Num", "U", "W", "B", "V" };
            Costs = names.Select(_ => Cost()).ToArray();

            // Functions[kind] is the function of each kind, whatever order they are declared in.
            var declaration = Enumerable.Range(0, names.Length).ToArray();
            if (costChoices is not null)
            {
                var shuffle = new Random(~seed);
                declaration = declaration.OrderBy(_ => shuffle.Next()).ToArray();
            }

            Functions = new FunctionId[names.Length];
            foreach (var kind in declaration)
            {
                Functions[kind] = kind switch
                {
                    0 or 1 => Fx.Constructor(names[kind], E, Costs[kind]),
                    2 => Fx.Constructor(names[kind], E, Costs[kind], Fx.I64),
                    3 or 4 => Fx.Constructor(names[kind], E, Costs[kind], E),
                    5 => Fx.Constructor(names[kind], E, Costs[kind], E, E),
                    _ => Fx.Constructor(names[kind], E, Costs[kind], Vec),
                };
            }

            Names = names;

            var ids = new List<Value>();
            Value Pick() => Fx.Canon(ids[random.Next(ids.Count)]);
            uint[] Picks(int count) => Enumerable.Range(0, count).Select(_ => Pick().Rep).ToArray();
            var steps = random.Next(4, 24);
            for (var step = 0; step < steps; step++)
            {
                var op = ids.Count == 0 ? random.Next(3) : random.Next(7);
                var made = op switch
                {
                    0 or 1 => Fx.Make(Functions[op]),
                    2 => Fx.Make(Functions[2], Fx.Int(random.Next(-2, 3))),
                    3 or 4 => Fx.Make(Functions[op], Pick()),
                    5 => Fx.Make(Functions[5], Pick(), Pick()),
                    _ => Fx.Make(Functions[6], Fx.EGraph.GetContainerValue(new VecContainer(Picks(random.Next(0, 4))))),
                };
                ids.Add(made);
            }

            for (var unions = random.Next(0, 6); unions > 0; unions--)
                Fx.Union(Pick(), Pick());

            for (var subsumed = random.Next(0, 3); subsumed > 0; subsumed--)
            {
                var function = Functions[random.Next(Functions.Length)];
                var rows = BridgeTestUtil.Rows(Fx.EGraph, function);
                if (rows.Count > 0)
                    Fx.Subsume(function, rows[random.Next(rows.Count)].Values[..^1]);
            }

            // The nodes: every row that is not subsumed, with canonical cells.
            Nodes = new List<(int Function, Value[] Row)>();
            for (var f = 0; f < Functions.Length; f++)
                foreach (var (values, isSubsumed) in BridgeTestUtil.Rows(Fx.EGraph, Functions[f]))
                    if (isSubsumed == false)
                        Nodes.Add((f, values.Select((v, i) => f == 2 && i == 0 ? v : Fx.Canon(v)).ToArray()));

            // The classes: every id among the rows' outputs and children.
            var classes = new SortedSet<uint>();
            foreach (var row in Functions.SelectMany(f => BridgeTestUtil.Rows(Fx.EGraph, f)))
                classes.Add(Fx.Canon(row.Values[^1]).Rep);

            foreach (var (function, row) in Nodes)
                foreach (var child in ChildClasses(function, row))
                    classes.Add(child);

            Classes = classes.ToArray();
            ReferenceCosts = BellmanFord();
        }

        /// <summary>
        /// Gets the fixture that owns the e-graph and the extraction schema.
        /// </summary>
        public ExtractionFixture Fx { get; }

        /// <summary>
        /// Gets the e-class sort.
        /// </summary>
        public EqSort E { get; }

        /// <summary>
        /// Gets the sort of vectors of <see cref="E"/>.
        /// </summary>
        public TestVecSort Vec { get; }

        /// <summary>
        /// Gets the function of each kind, by kind: <c>L0</c>, <c>L1</c>, <c>Num(i64)</c>, <c>U(E)</c>, <c>W(E)</c>,
        /// <c>B(E, E)</c> and <c>V(VecE)</c>.
        /// </summary>
        public FunctionId[] Functions { get; }

        /// <summary>
        /// Gets the name of each kind's function.
        /// </summary>
        public string[] Names { get; }

        /// <summary>
        /// Gets the head cost of each kind's function.
        /// </summary>
        public ulong[] Costs { get; }

        /// <summary>
        /// Gets the nodes: every row that is not subsumed, by function kind, with canonical cells (the <c>Num</c>
        /// literal as stored).
        /// </summary>
        public List<(int Function, Value[] Row)> Nodes { get; }

        /// <summary>
        /// Gets every class among the rows' outputs and the nodes' children, ascending.
        /// </summary>
        public uint[] Classes { get; }

        /// <summary>
        /// Gets the reference cost of every class that has a finite term, from the Bellman-Ford fixpoint.
        /// </summary>
        public Dictionary<uint, ulong> ReferenceCosts { get; }

        /// <summary>
        /// Gets the elements of a vector, canonicalized.
        /// </summary>
        public uint[] Elements(uint container)
        {
            if (Fx.EGraph.ContainerValues.TryGetValue<VecContainer>(container, out var vec) == false)
                throw new InvalidOperationException($"No vector {container}.");

            return vec.Values.ToArray().Select(v => Fx.Canon(new Value(v)).Rep).ToArray();
        }

        /// <summary>
        /// Gets the e-classes a node depends on, with multiplicity.
        /// </summary>
        public IEnumerable<uint> ChildClasses(int function, Value[] row)
        {
            switch (function)
            {
                case 3:
                case 4:
                    yield return row[0].Rep;
                    break;
                case 5:
                    yield return row[0].Rep;
                    yield return row[1].Rep;
                    break;
                case 6:
                    foreach (var element in Elements(row[0].Rep))
                        yield return element;

                    break;
            }
        }

        /// <summary>
        /// Gets a node's cost from given class costs, or <c>null</c> if a class it depends on has none.
        /// </summary>
        public ulong? NodeCost(int function, Value[] row, IReadOnlyDictionary<uint, ulong> classCosts)
        {
            var cost = Costs[function];
            if (function == 2)
                cost = Add(cost, 1);

            foreach (var child in ChildClasses(function, row))
            {
                if (classCosts.TryGetValue(child, out var childCost) == false)
                    return null;

                cost = Add(cost, childCost);
            }

            return cost;
        }

        /// <summary>
        /// Computes every class's cheapest cost by relaxing every node until nothing changes.
        /// </summary>
        Dictionary<uint, ulong> BellmanFord()
        {
            var costs = new Dictionary<uint, ulong>();
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var (function, row) in Nodes)
                {
                    if (NodeCost(function, row, costs) is not { } cost)
                        continue;

                    var owner = row[^1].Rep;
                    if (costs.TryGetValue(owner, out var current) == false || cost < current)
                    {
                        costs[owner] = cost;
                        changed = true;
                    }
                }
            }

            return costs;
        }

        /// <summary>
        /// Enumerates the cheapest terms of <paramref name="owner"/> whose depth is at most <paramref name="depth"/> (a
        /// node counts one level; base values and the vector around elements count none), keeping the
        /// <see cref="Keep"/> cheapest, which is exact for the minimum since costs only add.
        /// </summary>
        public List<(ulong Cost, string Text)> Enumerate(uint owner, int depth)
        {
            if (depth == 0)
                return [];

            if (enumerated.TryGetValue((owner, depth), out var known))
                return known;

            var terms = new List<(ulong Cost, string Text)>();
            foreach (var (function, row) in Nodes)
            {
                if (row[^1].Rep != owner)
                    continue;

                // Each child column's options, then their product, pruned to the cheapest at each step.
                var partial = new List<(ulong Cost, List<string> Parts)> { (Costs[function], new List<string>()) };
                var columns = function switch
                {
                    2 => new[] { Literal(row[0]) },
                    3 or 4 => [Enumerate(row[0].Rep, depth - 1)],
                    5 => [Enumerate(row[0].Rep, depth - 1), Enumerate(row[1].Rep, depth - 1)],
                    6 => [Vector(Elements(row[0].Rep), depth - 1)],
                    _ => Array.Empty<List<(ulong Cost, string Text)>>(),
                };

                foreach (var options in columns)
                {
                    partial = partial
                        .SelectMany(p => options.Select(o => (Add(p.Cost, o.Cost), new List<string>(p.Parts) { o.Text })))
                        .OrderBy(p => p.Item1).ThenBy(p => string.Join(" ", p.Item2), StringComparer.Ordinal)
                        .Take(Keep)
                        .ToList();
                }

                foreach (var (cost, parts) in partial)
                    terms.Add((cost, parts.Count == 0 ? $"({Names[function]})" : $"({Names[function]} {string.Join(" ", parts)})"));
            }

            var result = terms.Distinct().OrderBy(t => t.Cost).ThenBy(t => t.Text, StringComparer.Ordinal).Take(Keep).ToList();
            enumerated[(owner, depth)] = result;
            return result;
        }

        List<(ulong Cost, string Text)> Literal(Value value)
        {
            return [(1, Fx.EGraph.BaseValues.Unwrap<Int64Codec, long>(value).ToString(CultureInfo.InvariantCulture))];
        }

        List<(ulong Cost, string Text)> Vector(uint[] elements, int depth)
        {
            var partial = new List<(ulong Cost, List<string> Parts)> { (0, new List<string>()) };
            foreach (var element in elements)
            {
                var options = Enumerate(element, depth);
                partial = partial
                    .SelectMany(p => options.Select(o => (Add(p.Cost, o.Cost), new List<string>(p.Parts) { o.Text })))
                    .OrderBy(p => p.Item1).ThenBy(p => string.Join(" ", p.Item2), StringComparer.Ordinal)
                    .Take(Keep)
                    .ToList();
            }

            return partial.Select(p => (p.Cost, p.Parts.Count == 0 ? "(vec-empty)" : $"(vec-of {string.Join(" ", p.Parts)})")).ToList();
        }

        /// <summary>
        /// Adds two non-negative costs, saturating.
        /// </summary>
        public static ulong Add(ulong left, ulong right) => left > ulong.MaxValue - right ? ulong.MaxValue : left + right;

    }

}
