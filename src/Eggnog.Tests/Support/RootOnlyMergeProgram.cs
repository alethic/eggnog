using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A random program for the tests of the refusal below the root of a merge that would rewrite a row of a function
    /// whose merge is not a lattice (<see cref="FunctionMerge"/>): at the root, classes of <see cref="Node"/>, unary
    /// terms and terms keyed by vectors, pairs and maps of them, rows of plain functions holding vectors, vectors of
    /// vectors, sets, maps, multisets, pairs of both layouts, sets of vectors and function values of those classes, rows
    /// of functions with a merge keyed by such containers, by classes, or holding a vector, unions, and twins: a holder
    /// and a row of one kind of container, of two classes of their own, which a union of the two makes meet, the row's
    /// container made at times by another type of its layout, a pair of a class and an <c>i32</c> or a map of classes to
    /// <c>bool</c>s, as the key of a constructor's term or of a plain function's row; then three batches of merges, two
    /// in a child of the root and one in a grandchild, each a union, often a twin's, a constructor write that gives an
    /// existing term another class, or a <c>vec-union</c> of two rows' vectors; and, under a writable root, root writes
    /// between the child's two batches. The same seed and freezing build the same program, with the same ids, in a new
    /// graph each time.
    /// </summary>
    public sealed class RootOnlyMergeProgram
    {

        static readonly FunctionMerge<long> Sum = FunctionMerge.Of<long>("sum", (a, b) => checked(a + b));

        readonly Constructor<Node, Node> wrap;
        readonly Function<long, Vec<Node>> vectors;
        readonly List<Node> made = new();
        readonly List<Node> wrapped = new();
        readonly List<(int Kind, int A, int B)[]> batches = new();
        readonly List<Action> between = new();
        readonly StringBuilder log = new();
        int vectorRows;

        /// <summary>
        /// Builds the program of <paramref name="seed"/> in a new graph, its root frozen when <paramref name="frozen"/>.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <param name="frozen">Whether the root is frozen before the batches.</param>
        public RootOnlyMergeProgram(int seed, bool frozen)
        {
            var rng = new Random(seed);
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            wrap = graph.DefineConstructor<Node, Node>("Wrap");
            var score = graph.DefineFunction<Node, long, long>("score");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<Vec<Node>>();
            graph.Values.RegisterSet<Node>();
            graph.Values.RegisterSet<Vec<Node>>();
            graph.Values.RegisterMap<Node, long>();
            graph.Values.RegisterMap<long, Node>();
            graph.Values.RegisterMultiSet<Node>();
            graph.Values.RegisterPair<Node, long>();
            graph.Values.RegisterPair<long, Node>();
            graph.Values.RegisterPair<Node, int>();
            graph.Values.RegisterMap<Node, bool>();
            graph.Values.RegisterFn<long, long>();
            var keyedByVector = graph.DefineConstructor<Vec<Node>, Node>("K");
            var keyedByMap = graph.DefineConstructor<Map<Node, long>, Node>("KM");
            var keyedByPair = graph.DefineConstructor<Pair<Node, long>, Node>("KP");
            vectors = graph.DefineFunction<long, Vec<Node>>("hv");
            var holders = (
                VectorsOfVectors: graph.DefineFunction<long, Vec<Vec<Node>>>("hvv"),
                Sets: graph.DefineFunction<long, Set<Node>>("hs"),
                SetsOfVectors: graph.DefineFunction<long, Set<Vec<Node>>>("hsv"),
                Maps: graph.DefineFunction<long, Map<Node, long>>("hm"),
                MapsOfValues: graph.DefineFunction<long, Map<long, Node>>("hml"),
                MultiSets: graph.DefineFunction<long, MultiSet<Node>>("hms"),
                Pairs: graph.DefineFunction<long, Pair<Node, long>>("hp"),
                PairsOfValues: graph.DefineFunction<long, Pair<long, Node>>("hpl"),
                Functions: graph.DefineFunction<long, UnstableFn<long, long>>("hf"));
            var rows = (
                Vectors: graph.DefineFunction<Vec<Node>, long>("rv", Sum),
                Classes: graph.DefineFunction<Node, long>("rn", Sum),
                VectorsOfVectors: graph.DefineFunction<Vec<Vec<Node>>, long>("rvv", Sum),
                Sets: graph.DefineFunction<Set<Node>, long>("rs", Sum),
                SetsOfVectors: graph.DefineFunction<Set<Vec<Node>>, long>("rsv", Sum),
                Maps: graph.DefineFunction<Map<Node, long>, long>("rm", Sum),
                MapsOfValues: graph.DefineFunction<Map<long, Node>, long>("rml", Sum),
                MultiSets: graph.DefineFunction<MultiSet<Node>, long>("rms", Sum),
                Pairs: graph.DefineFunction<Pair<Node, long>, long>("rp", Sum),
                PairsOfValues: graph.DefineFunction<Pair<long, Node>, long>("rpl", Sum),
                Functions: graph.DefineFunction<UnstableFn<long, long>, long>("rf", Sum),
                Last: graph.DefineFunction<long, Vec<Node>>("ro", FunctionMerge.New<Vec<Node>>()));

            // Of the layouts of the rows' pairs and maps, after them, so that the check lists their rows later.
            var aliases = (
                Terms: graph.DefineConstructor<Pair<Node, int>, Node>("KPI"),
                Pairs: graph.DefineFunction<Pair<Node, int>, long>("kpi"),
                Maps: graph.DefineFunction<Map<Node, bool>, long>("kmb"));
            var vec = Primitives.VecOf<Node>();
            var vecOfVecs = Primitives.VecOf<Vec<Node>>();
            var set = Primitives.SetOf<Node>();
            var setOfVecs = Primitives.SetOf<Vec<Node>>();
            var empty = Primitives.MapEmpty<Node, long>().Of();
            var insert = Primitives.MapInsert<Node, long>();
            var emptyOfValues = Primitives.MapEmpty<long, Node>().Of();
            var insertOfValues = Primitives.MapInsert<long, Node>();
            var multiset = Primitives.MultiSetOf<Node>();
            var pair = Primitives.PairOf<Node, long>();
            var pairOfValues = Primitives.PairOf<long, Node>();
            var narrowPair = Primitives.PairOf<Node, int>();
            var emptyOfFlags = Primitives.MapEmpty<Node, bool>().Of();
            var insertOfFlags = Primitives.MapInsert<Node, bool>();
            var function = Primitives.UnstableFn<long, long>(score);
            Root = graph.Root;
            var root = Root;
            var terms = new List<ConstructorTerm<Node>>();
            var wrappedTerms = new List<ConstructorTerm<Node>>();
            var twinClasses = new List<ConstructorTerm<Node>>();
            var twins = new List<(int, int)>();
            var leaves = rng.Next(3, 5);
            for (var i = 0; i < leaves; i++)
                terms.Add(num.Of((long)i));

            // The leaves' classes first, in an order of their own, so that which of two loses is not the order of the
            // containers that hold them.
            foreach (var i in Enumerable.Range(0, leaves).OrderBy(_ => rng.Next()).ToArray())
                root.Make(terms[i]);

            // Few classes, and containers of one mostly, so that rebuilt containers often meet; the values of pairs and
            // maps are small, as the classes' cells are, so that some pairs of the two layouts hold the same cells.
            ConstructorTerm<Node> T() => terms[rng.Next(terms.Count)];
            long L() => rng.Next(8);
            Term<Vec<Node>> V() => rng.Next(4) == 0 ? vec.Of(T(), T()) : vec.Of(T());
            Term<Set<Node>> S() => rng.Next(2) == 0 ? set.Of(T(), T()) : set.Of(T());
            Term<Map<Node, long>> M() => rng.Next(3) == 0 ? insert.Of(insert.Of(empty, T(), L()), T(), L()) : insert.Of(empty, T(), L());
            Term<Set<Vec<Node>>> SV() => rng.Next(2) == 0 ? setOfVecs.Of(V(), V()) : setOfVecs.Of(V());
            Term<MultiSet<Node>> MS() => rng.Next(2) == 0 ? multiset.Of(T(), T()) : multiset.Of(T());

            long next = 1000;
            for (var steps = rng.Next(6, 18); steps > 0; steps--)
            {
                switch (rng.Next(44))
                {
                    case < 5: Write("hv", () => root.Assume(vectors.Of(vectorRows++).Is(V()))); break;
                    case < 10: Write("rv", () => root.Assume(rows.Vectors.Of(V()).Is(1L))); break;
                    case < 12: Write("hs", () => root.Assume(holders.Sets.Of(next++).Is(S()))); break;
                    case < 14: Write("rs", () => root.Assume(rows.Sets.Of(S()).Is(1L))); break;
                    case 14: Write("hvv", () => root.Assume(holders.VectorsOfVectors.Of(next++).Is(vecOfVecs.Of(V())))); break;
                    case 15: Write("rvv", () => root.Assume(rows.VectorsOfVectors.Of(vecOfVecs.Of(V())).Is(1L))); break;
                    case 16: Write("hsv", () => root.Assume(holders.SetsOfVectors.Of(next++).Is(SV()))); break;
                    case 17: Write("rsv", () => root.Assume(rows.SetsOfVectors.Of(SV()).Is(1L))); break;
                    case 18: Write("hm", () => root.Assume(holders.Maps.Of(next++).Is(M()))); break;
                    case 19: Write("rm", () => root.Assume(rows.Maps.Of(M()).Is(1L))); break;
                    case 20: Write("hml", () => root.Assume(holders.MapsOfValues.Of(next++).Is(insertOfValues.Of(emptyOfValues, L(), T())))); break;
                    case 21: Write("rml", () => root.Assume(rows.MapsOfValues.Of(insertOfValues.Of(emptyOfValues, L(), T())).Is(1L))); break;
                    case 22: Write("hms", () => root.Assume(holders.MultiSets.Of(next++).Is(MS()))); break;
                    case 23: Write("rms", () => root.Assume(rows.MultiSets.Of(MS()).Is(1L))); break;
                    case 24: Write("hp", () => root.Assume(holders.Pairs.Of(next++).Is(pair.Of(T(), L())))); break;
                    case 25: Write("rp", () => root.Assume(rows.Pairs.Of(pair.Of(T(), L())).Is(1L))); break;
                    case 26: Write("hpl", () => root.Assume(holders.PairsOfValues.Of(next++).Is(pairOfValues.Of(L(), T())))); break;
                    case 27: Write("rpl", () => root.Assume(rows.PairsOfValues.Of(pairOfValues.Of(L(), T())).Is(1L))); break;
                    case 28: Write("hf", () => root.Assume(holders.Functions.Of(next++).Is(function.Of(T())))); break;
                    case 29: Write("rf", () => root.Assume(rows.Functions.Of(function.Of(T())).Is(1L))); break;
                    case 30: Write("rn", () => root.Assume(rows.Classes.Of(T()).Is(1L))); break;
                    case 31: Write("ro", () => root.Assume(rows.Last.Of(next++).Is(V()))); break;
                    case 32: Write("vec", () => root.Assume(Fact.Exists(V()))); break;
                    case 33:
                    {
                        var argument = T();
                        wrappedTerms.Add(argument);
                        terms.Add(wrap.Of(argument));
                        log.Append("Wrap ");
                        break;
                    }
                    case 34: terms.Add(keyedByVector.Of(V())); log.Append("K "); break;
                    case 35: terms.Add(keyedByMap.Of(M())); log.Append("KM "); break;
                    case 36: terms.Add(keyedByPair.Of(pair.Of(T(), L()))); log.Append("KP "); break;
                    case < 40: Write("union", () => root.Union(root.Make(T()), root.Make(T()))); break;
                    default:
                    {
                        // A holder and a row of one kind of container, in either order, of two classes of their own,
                        // made in either order, whose union makes the containers meet: the one the holder holds, or
                        // the one the row names, loses.
                        var (x, y) = (twinClasses.Count, twinClasses.Count + 1);
                        var (held, named) = (num.Of(100L + x), num.Of(100L + y));
                        twinClasses.Add(held);
                        twinClasses.Add(named);
                        Node heldClass, namedClass;
                        if (rng.Next(2) == 0)
                        {
                            heldClass = root.Make(held);
                            namedClass = root.Make(named);
                        }
                        else
                        {
                            namedClass = root.Make(named);
                            heldClass = root.Make(held);
                        }

                        // A pair of the other layout whose cells are the row's pair's, until a union rebuilds it; a map
                        // keying a term that meets the one a row names once their classes are one; a pair or a map of
                        // the row's layout, of another type, whose container is the row's.
                        var value = L();
                        uint Cell(Node node) => ClassChecks.Require(root, node.Id, nameof(node));
                        var shape = rng.Next(10);
                        var (holder, row, name) = shape switch
                        {
                            0 => (vectors.Of(vectorRows++).Is(vec.Of(held)), rows.Vectors.Of(vec.Of(named)).Is(1L), "vec"),
                            1 => (holders.Sets.Of(next++).Is(set.Of(named, held)), rows.Sets.Of(set.Of(named)).Is(1L), "set"),
                            2 => (holders.Maps.Of(next++).Is(insert.Of(empty, held, value)), rows.Maps.Of(insert.Of(empty, named, value)).Is(1L), "map"),
                            3 => (holders.VectorsOfVectors.Of(next++).Is(vecOfVecs.Of(vec.Of(held))), rows.VectorsOfVectors.Of(vecOfVecs.Of(vec.Of(named))).Is(1L), "nest"),
                            4 => (holders.MultiSets.Of(next++).Is(multiset.Of(held)), rows.MultiSets.Of(multiset.Of(named)).Is(1L), "multiset"),
                            5 => (holders.Functions.Of(next++).Is(function.Of(held)), rows.Functions.Of(function.Of(named)).Is(1L), "fn"),
                            6 => (holders.PairsOfValues.Of(next++).Is(pairOfValues.Of((long)Cell(namedClass), held)), rows.Pairs.Of(pair.Of(named, (long)Cell(heldClass))).Is(1L), "pair"),
                            7 => (Fact.Exists(keyedByMap.Of(insert.Of(empty, held, value))), rows.Classes.Of(keyedByMap.Of(insert.Of(empty, named, value))).Is(1L), "KM"),
                            8 => (holders.Pairs.Of(next++).Is(pair.Of(held, value)), rows.Pairs.Of(pair.Of(named, value)).Is(1L), "pair-alias"),
                            _ => (holders.Maps.Of(next++).Is(insert.Of(empty, held, value % 2)), rows.Maps.Of(insert.Of(empty, named, value % 2)).Is(1L), "map-alias"),
                        };
                        Fact? alias = shape switch
                        {
                            8 when rng.Next(2) == 0 => Fact.Exists(aliases.Terms.Of(narrowPair.Of(named, (int)value))),
                            8 => aliases.Pairs.Of(narrowPair.Of(named, (int)value)).Is(0L),
                            9 => aliases.Maps.Of(insertOfFlags.Of(emptyOfFlags, named, value % 2 == 1)).Is(0L),
                            _ => null,
                        };
                        var facts = rng.Next(2) == 0 ? new List<Fact> { holder, row } : new List<Fact> { row, holder };
                        if (alias is not null)
                            facts.Insert(rng.Next(facts.Count + 1), alias);

                        root.Assume([.. facts]);

                        twins.Add((x, y));
                        log.Append("twin-").Append(name).Append(' ');
                        break;
                    }
                }
            }

            foreach (var term in terms)
                made.Add(root.Make(term));

            // The twins' classes come after the others.
            for (var i = 0; i < twins.Count; i++)
                twins[i] = (twins[i].Item1 + made.Count, twins[i].Item2 + made.Count);

            foreach (var term in twinClasses)
                made.Add(root.Make(term));

            foreach (var term in wrappedTerms)
                wrapped.Add(root.Make(term));

            for (var batch = 0; batch < 3; batch++)
            {
                var merges = new (int, int, int)[rng.Next(1, 4)];
                for (var i = 0; i < merges.Length; i++)
                {
                    var kind = rng.Next(6);
                    merges[i] = kind == 3 && wrapped.Count > 0
                        ? (1, rng.Next(wrapped.Count), Class())
                        : kind >= 4 && vectorRows > 1
                            ? (2, rng.Next(vectorRows), rng.Next(vectorRows))
                            : Union();
                }

                batches.Add(merges);
            }

            // A writable root writes on after the child's first batch, which the child catches up with at its second.
            if (frozen == false && rng.Next(2) == 0)
            {
                for (var i = rng.Next(1, 4); i > 0; i--)
                {
                    var vector = V();
                    var key = next++;
                    between.Add(rng.Next(5) switch
                    {
                        0 => () => root.Assume(vectors.Of(vectorRows++).Is(vector)),
                        1 => () => root.Assume(rows.Vectors.Of(vector).Is(1L)),
                        2 => () => root.Assume(holders.VectorsOfVectors.Of(key).Is(vecOfVecs.Of(vector))),
                        3 => () => root.Assume(rows.VectorsOfVectors.Of(vecOfVecs.Of(vector)).Is(1L)),
                        _ => () => root.Assume(Fact.Exists(vector)),
                    });
                }
            }

            if (frozen)
                root.Freeze();

            // The classes of the leaves mostly, which the containers hold.
            int Class() => rng.Next(4) > 0 ? rng.Next(leaves) : rng.Next(made.Count);

            // A union of a twin's two classes half the time, or of two others.
            (int, int, int) Union()
            {
                if (twins.Count == 0 || rng.Next(2) == 0)
                    return (0, Class(), Class());

                var (x, y) = twins[rng.Next(twins.Count)];
                return (0, x, y);
            }

            void Write(string name, Action write)
            {
                write();
                log.Append(name).Append(' ');
            }
        }

        /// <summary>
        /// Gets the root.
        /// </summary>
        public Scope Root { get; }

        /// <summary>
        /// Gets the batches of merges: the child's two, then the grandchild's. A merge is a union of two classes the root
        /// made (kind 0), a write of a <c>Wrap</c> term the root made as another of them (kind 1), or a <c>vec-union</c> of
        /// two rows of <c>hv</c> (kind 2).
        /// </summary>
        public IReadOnlyList<(int Kind, int A, int B)[]> Batches => batches;

        /// <summary>
        /// Gets whether the root writes between the child's two batches.
        /// </summary>
        public bool WritesBetween => between.Count > 0;

        /// <summary>
        /// Gets the program's root writes, in order, for a failure's report.
        /// </summary>
        public string Description => log.ToString();

        /// <summary>
        /// Gets the scope of batch <paramref name="batch"/>, creating it, and writing the root as the program does before
        /// it: a child of the root for the first two, a child of that for the third.
        /// </summary>
        /// <param name="scopes">The scopes made so far, the child then the grandchild, which the caller disposes.</param>
        /// <param name="batch">The batch.</param>
        /// <returns>The scope.</returns>
        public Scope ScopeOf(List<Scope> scopes, int batch)
        {
            if (batch == 1)
                foreach (var write in between)
                    write();

            if (scopes.Count == 0)
                scopes.Add(Root.CreateChild());

            if (batch == 2 && scopes.Count == 1)
                scopes.Add(scopes[0].CreateChild());

            return scopes[^1];
        }

        /// <summary>
        /// Writes a merge through a batch.
        /// </summary>
        /// <param name="writer">The batch.</param>
        /// <param name="merge">The merge.</param>
        public void Write(ScopeWriter writer, (int Kind, int A, int B) merge)
        {
            switch (merge.Kind)
            {
                case 0:
                    writer.Union(made[merge.A], made[merge.B]);
                    break;
                case 1:
                    writer.Assume(wrap.Of(wrapped[merge.A]).Is(made[merge.B]));
                    break;
                default:
                    writer.Assume(Fact.Exists(Primitives.VecUnion<Node>().Of(vectors.Of((long)merge.A), vectors.Of((long)merge.B))));
                    break;
            }
        }

        /// <summary>
        /// Gets the unions of classes a merge makes in <paramref name="scope"/>, canonical there: its two classes, the
        /// <c>Wrap</c> term's class and the other, or the vectors' elements pairwise, none when they differ in length.
        /// The scope's read brings it up to date with its ancestors first, as a write's entry does.
        /// </summary>
        /// <param name="scope">The scope.</param>
        /// <param name="merge">The merge.</param>
        /// <returns>The unions.</returns>
        public List<(uint, uint)> Unions(Scope scope, (int Kind, int A, int B) merge)
        {
            scope.CountRows(vectors);
            uint Cell(Node node) => scope.Engine.Find(ClassChecks.Require(scope, node.Id, nameof(node)));
            var unions = new List<(uint, uint)>();
            switch (merge.Kind)
            {
                case 0:
                    unions.Add((Cell(made[merge.A]), Cell(made[merge.B])));
                    break;
                case 1:
                    unions.Add((Cell(scope.Make(wrap.Of(wrapped[merge.A]))), Cell(made[merge.B])));
                    break;
                default:
                    if (scope.TryGet(vectors.Of((long)merge.A), out var x) && scope.TryGet(vectors.Of((long)merge.B), out var y) && x.Count == y.Count)
                        for (var i = 0; i < x.Count; i++)
                            unions.Add((Cell(x[i]), Cell(y[i])));
                    break;
            }

            return unions;
        }

    }

}
