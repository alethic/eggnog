using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// The fixture of <see cref="ContainerViewColumnTypeTests"/>: a frozen root holding a vector, a set, a multiset, a
    /// map, a pair and a function value of classes, each with a twin of other classes, the rules that read them, and two
    /// children of it: <see cref="Plain"/>, which holds nothing itself, and <see cref="Copy"/>, whose layers swap each
    /// container with its twin (the engine's test hook, <c>ContainerLayers.Place</c>), as renames in a child's rebuild
    /// could leave them: the copy reads the twin's content under the container's root id and the container's under the
    /// twin's, so that it reads each id as the root reads the other, and every content once.
    /// </summary>
    internal sealed class ContainerViewTheory
    {

        public ContainerViewTheory()
        {
            Graph.DefineSort<Node>("Math");
            Num = Graph.DefineConstructor<long, Node>("Num");
            Add = Graph.DefineConstructor<Node, Node, Node>("Add");
            var values = Graph.Values;
            values.RegisterVec<Node>();
            values.RegisterSet<Node>();
            values.RegisterMultiSet<Node>();
            values.RegisterMap<Node, long>();
            values.RegisterPair<Node, Node>();
            values.RegisterVec<long>();
            values.RegisterMultiSet<long>();
            values.RegisterFn<long, long>();
            values.RegisterFn<Node, long>();
            values.RegisterFn<Node, ValueTuple>();
            values.RegisterFn<Node, MultiSet<Node>>();
            values.RegisterFn<Node, Node, Node>();
            L = Graph.DefineConstructor<Vec<Node>, Node>("L");

            Vector = Graph.DefineFunction<Vec<Node>>("vector");
            Set = Graph.DefineFunction<Set<Node>>("set");
            Bag = Graph.DefineFunction<MultiSet<Node>>("bag");
            Dict = Graph.DefineFunction<Map<Node, long>>("dict");
            Couple = Graph.DefineFunction<Pair<Node, Node>>("couple");
            Fn = Graph.DefineFunction<UnstableFn<long, long>>("fn");
            TwinVector = Graph.DefineFunction<Vec<Node>>("twin-vector");
            TwinSet = Graph.DefineFunction<Set<Node>>("twin-set");
            TwinBag = Graph.DefineFunction<MultiSet<Node>>("twin-bag");
            TwinDict = Graph.DefineFunction<Map<Node, long>>("twin-dict");
            TwinCouple = Graph.DefineFunction<Pair<Node, Node>>("twin-couple");
            TwinFn = Graph.DefineFunction<UnstableFn<long, long>>("twin-fn");
            Rank = Graph.DefineFunction<Set<Node>, long>("rank");
            Score = Graph.DefineFunction<Node, long, long>("score");
            Weight = Graph.DefineFunction<Node, long>("weight");
            IsEven = Graph.DefineFunction<Node, ValueTuple>("is-even");
            ToBag = Graph.DefineFunction<Node, MultiSet<Node>>("to-bag");
            Fold = Graph.DefineFunction<Node, Node, Node>("fold");
            Empty = Graph.DefineFunction<MultiSet<Node>>("empty");
            Even = Graph.DefineFunction<MultiSet<Node>>("even");
            Flat = Graph.DefineFunction<MultiSet<Node>>("flat-expected");

            var root = Root;
            One = root.Make(Num.Of(1L));
            Two = root.Make(Num.Of(2L));
            Three = root.Make(Num.Of(3L));
            var of = Primitives.MultiSetOf<Node>();
            root.Assume(
                Vector.Of().Is(Primitives.VecOf<Node>().Of(Num.Of(1L))),
                Set.Of().Is(Primitives.SetOf<Node>().Of(Num.Of(1L))),
                Bag.Of().Is(of.Of(Num.Of(1L), Num.Of(1L), Num.Of(3L))),
                Dict.Of().Is(Primitives.MapInsert<Node, long>().Of(Primitives.MapEmpty<Node, long>().Of(), Num.Of(1L), 10L)),
                Couple.Of().Is(Primitives.PairOf<Node, Node>().Of(Num.Of(1L), Num.Of(3L))),
                Score.Of(Num.Of(1L), 1L).Is(5L),
                Score.Of(Num.Of(2L), 1L).Is(7L),
                Fn.Of().Is(Primitives.UnstableFn<long, long>(Score).Of(Num.Of(1L))),
                Weight.Of(Num.Of(1L)).Is(1L),
                Weight.Of(Num.Of(2L)).Is(2L),
                Weight.Of(Num.Of(3L)).Is(3L),
                IsEven.Of(Num.Of(2L)).Is(default(ValueTuple)),
                ToBag.Of(Num.Of(2L)).Is(of.Of(Num.Of(1L))),
                Fold.Of(Num.Of(1L), Num.Of(1L)).Is(Num.Of(1L)),
                Fold.Of(Num.Of(1L), Num.Of(3L)).Is(Num.Of(3L)),
                Fold.Of(Num.Of(2L), Num.Of(3L)).Is(Num.Of(2L)),

                // The multisets of classes the copy's filter and flat-map make, which the root holds too.
                Empty.Of().Is(of.Of()),
                Even.Of().Is(of.Of(Num.Of(2L))),
                Flat.Of().Is(of.Of(Num.Of(1L), Num.Of(3L), Num.Of(3L), Num.Of(3L))),

                // The twins, which the copy holds under the containers' ids.
                TwinVector.Of().Is(Primitives.VecOf<Node>().Of(Num.Of(2L))),
                TwinSet.Of().Is(Primitives.SetOf<Node>().Of(Num.Of(2L))),
                TwinBag.Of().Is(of.Of(Num.Of(2L), Num.Of(3L), Num.Of(3L), Num.Of(3L))),
                TwinDict.Of().Is(Primitives.MapInsert<Node, long>().Of(Primitives.MapEmpty<Node, long>().Of(), Num.Of(2L), 20L)),
                TwinCouple.Of().Is(Primitives.PairOf<Node, Node>().Of(Num.Of(2L), Num.Of(3L))),
                TwinFn.Of().Is(Primitives.UnstableFn<long, long>(Score).Of(Num.Of(2L))),

                // Keyed by the set and by (set-of (Num 1) (Num 2)): the set orders first at the root, as (set-of (Num 1))
                // is a prefix, and last in the copy, where it is (set-of (Num 2)).
                Rank.Of(Primitives.SetOf<Node>().Of(Num.Of(1L))).Is(1L),
                Rank.Of(Primitives.SetOf<Node>().Of(Num.Of(1L), Num.Of(2L))).Is(2L));
            Term = root.Make(L.Of(Vector.Of()));
            Rules = DefineRules();

            X = CellOf(Vector);
            S = CellOf(Set);
            M = CellOf(Bag);
            D = CellOf(Dict);
            P = CellOf(Couple);
            F = CellOf(Fn);
            TwinX = CellOf(TwinVector);
            TwinS = CellOf(TwinSet);
            TwinM = CellOf(TwinBag);
            TwinD = CellOf(TwinDict);
            TwinP = CellOf(TwinCouple);
            TwinF = CellOf(TwinFn);
            root.Freeze();

            Plain = root.CreateChild();
            Copy = root.CreateChild();
            var engine = Graph.Engine.ContainerValues;
            var layers = Copy.Engine.Context.Context.ContainerLayers!;
            Swap(layers, engine.GetEnvironment<Relations.VecContainer>(), X, TwinX);
            Swap(layers, engine.GetEnvironment<Relations.SetContainer>(), S, TwinS);
            Swap(layers, engine.GetEnvironment<Relations.MultiSetContainer>(), M, TwinM);
            Swap(layers, engine.GetEnvironment<Relations.MapContainer>(), D, TwinD);
            Swap(layers, engine.GetEnvironment<Relations.PairContainer>(), P, TwinP);
            Swap(layers, engine.GetEnvironment<Relations.FunctionContainer>(), F, TwinF);
        }

        public EGraph Graph { get; } = new();

        public Scope Root => Graph.Root;

        public Scope Plain { get; }

        public Scope Copy { get; }

        public Constructor<long, Node> Num { get; }

        public Constructor<Node, Node, Node> Add { get; }

        public Constructor<Vec<Node>, Node> L { get; }

        public Function<Vec<Node>> Vector { get; }

        public Function<Set<Node>> Set { get; }

        public Function<MultiSet<Node>> Bag { get; }

        public Function<Map<Node, long>> Dict { get; }

        public Function<Pair<Node, Node>> Couple { get; }

        public Function<UnstableFn<long, long>> Fn { get; }

        public Function<Vec<Node>> TwinVector { get; }

        public Function<Set<Node>> TwinSet { get; }

        public Function<MultiSet<Node>> TwinBag { get; }

        public Function<Map<Node, long>> TwinDict { get; }

        public Function<Pair<Node, Node>> TwinCouple { get; }

        public Function<UnstableFn<long, long>> TwinFn { get; }

        public Function<Set<Node>, long> Rank { get; }

        public Function<Node, long, long> Score { get; }

        public Function<Node, long> Weight { get; }

        public Function<Node, ValueTuple> IsEven { get; }

        public Function<Node, MultiSet<Node>> ToBag { get; }

        public Function<Node, Node, Node> Fold { get; }

        public Function<MultiSet<Node>> Empty { get; }

        public Function<MultiSet<Node>> Even { get; }

        public Function<MultiSet<Node>> Flat { get; }

        public Node One { get; }

        public Node Two { get; }

        public Node Three { get; }

        public Node Term { get; }

        /// <summary>
        /// Gets the root's vector (vec-of (Num 1)), the copy's (vec-of (Num 2)).
        /// </summary>
        public uint X { get; }

        /// <summary>
        /// Gets the root's set (set-of (Num 1)), the copy's (set-of (Num 2)).
        /// </summary>
        public uint S { get; }

        /// <summary>
        /// Gets the root's multiset (multiset-of (Num 1) (Num 1) (Num 3)), the copy's (multiset-of (Num 2) (Num 3)
        /// (Num 3) (Num 3)).
        /// </summary>
        public uint M { get; }

        /// <summary>
        /// Gets the root's map {(Num 1): 10}, the copy's {(Num 2): 20}.
        /// </summary>
        public uint D { get; }

        /// <summary>
        /// Gets the root's pair (pair (Num 1) (Num 3)), the copy's (pair (Num 2) (Num 3)).
        /// </summary>
        public uint P { get; }

        /// <summary>
        /// Gets the root's function value (unstable-fn "score" (Num 1)), the copy's (unstable-fn "score" (Num 2)).
        /// </summary>
        public uint F { get; }

        /// <summary>
        /// Gets the twin of <see cref="X"/>: the root's (vec-of (Num 2)), the copy's (vec-of (Num 1)).
        /// </summary>
        public uint TwinX { get; }

        /// <summary>
        /// Gets the twin of <see cref="S"/>: the root's (set-of (Num 2)), the copy's (set-of (Num 1)).
        /// </summary>
        public uint TwinS { get; }

        /// <summary>
        /// Gets the twin of <see cref="M"/>: the root's (multiset-of (Num 2) (Num 3) (Num 3) (Num 3)), the copy's
        /// (multiset-of (Num 1) (Num 1) (Num 3)).
        /// </summary>
        public uint TwinM { get; }

        /// <summary>
        /// Gets the twin of <see cref="D"/>: the root's {(Num 2): 20}, the copy's {(Num 1): 10}.
        /// </summary>
        public uint TwinD { get; }

        /// <summary>
        /// Gets the twin of <see cref="P"/>: the root's (pair (Num 2) (Num 3)), the copy's (pair (Num 1) (Num 3)).
        /// </summary>
        public uint TwinP { get; }

        /// <summary>
        /// Gets the twin of <see cref="F"/>: the root's (unstable-fn "score" (Num 2)), the copy's (unstable-fn "score"
        /// (Num 1)).
        /// </summary>
        public uint TwinF { get; }

        public Ruleset Rules { get; }

        public Function<Node> Picked { get; private set; } = null!;

        public Function<Node> PickedInBody { get; private set; } = null!;

        public Function<Node> PickedMax { get; private set; } = null!;

        public Function<long> Counted { get; private set; } = null!;

        public Function<long, long> Flags { get; private set; } = null!;

        public Function<Node> Gotten { get; private set; } = null!;

        public Function<long> Mapped { get; private set; } = null!;

        public Function<MultiSet<long>> Weights { get; private set; } = null!;

        public Function<MultiSet<Node>> Filtered { get; private set; } = null!;

        public Function<MultiSet<Node>> FlatMapped { get; private set; } = null!;

        public Function<Node> Reduced { get; private set; } = null!;

        public Function<Vec<long>> VectorWeights { get; private set; } = null!;

        public Function<long> Applied { get; private set; } = null!;

        /// <summary>
        /// Places in <paramref name="layers"/> the root's container under <paramref name="twin"/> under
        /// <paramref name="cell"/>, and the root's under <paramref name="cell"/> under <paramref name="twin"/>.
        /// </summary>
        static void Swap<TContainer>(Relations.ContainerLayers layers, Relations.ContainerEnv<TContainer> env, uint cell, uint twin)
            where TContainer : struct, Relations.IContainerValue<TContainer>
        {
            Assert.True(env.TryGetContainer(cell, out var held));
            Assert.True(env.TryGetContainer(twin, out var other));
            layers.Place(env, other, cell);
            layers.Place(env, held, twin);
        }

        /// <summary>
        /// Gets the cell the root holds <paramref name="function"/>'s value under.
        /// </summary>
        uint CellOf<T>(Function<T> function)
        {
            var type = Graph.Values.TypeOf<T>();
            Assert.True(type.TryEncodeExisting(Root.Get(function.Of()), Root.ContainerView, out var cell));
            return cell;
        }

        /// <summary>
        /// Defines the naive rules that read the containers by cell, each writing what it read.
        /// </summary>
        Ruleset DefineRules()
        {
            Picked = Graph.DefineFunction<Node>("picked");
            PickedInBody = Graph.DefineFunction<Node>("picked-in-body");
            PickedMax = Graph.DefineFunction<Node>("picked-max");
            Counted = Graph.DefineFunction<long>("counted");
            Flags = Graph.DefineFunction<long, long>("flags");
            Gotten = Graph.DefineFunction<Node>("gotten");
            Mapped = Graph.DefineFunction<long>("mapped");
            Weights = Graph.DefineFunction<MultiSet<long>>("weights");
            Filtered = Graph.DefineFunction<MultiSet<Node>>("filtered");
            FlatMapped = Graph.DefineFunction<MultiSet<Node>>("flat-mapped");
            Reduced = Graph.DefineFunction<Node>("reduced");
            VectorWeights = Graph.DefineFunction<Vec<long>>("vector-weights");
            Applied = Graph.DefineFunction<long>("applied");

            var rules = Graph.DefineRuleset("read");
            rules.DefineRule("bag", r =>
            {
                var m = r.Var<MultiSet<Node>>("m");
                var p = r.Var<Node>("p");
                r.Naive();
                r.When(m.Is(Bag.Of()), p.Is(Primitives.MultiSetPick<Node>().Of(m)));
                r.Then(
                    Picked.Of().Is(Primitives.MultiSetPick<Node>().Of(m)),
                    PickedInBody.Of().Is(p),
                    PickedMax.Of().Is(Primitives.MultiSetPickMax<Node>().Of(m)),
                    Counted.Of().Is(Primitives.MultiSetCount<Node>().Of(m, Num.Of(3L))),
                    Weights.Of().Is(Primitives.UnstableMultiSetMap<Node, long>().Of(Primitives.UnstableFn<Node, long>(Weight).Of(), m)),
                    Filtered.Of().Is(Primitives.UnstableMultiSetFilter<Node>().Of(Primitives.UnstableFn<Node, ValueTuple>(IsEven).Of(), m)),
                    FlatMapped.Of().Is(Primitives.UnstableMultiSetFlatMap<Node>().Of(Primitives.UnstableFn<Node, MultiSet<Node>>(ToBag).Of(), m)),
                    Reduced.Of().Is(Primitives.UnstableMultiSetReduce<Node>().Of(Primitives.UnstableFn<Node, Node, Node>(Add).Of(), Num.Of(1L), m)));
            });
            rules.DefineRule("bag-contains", r =>
            {
                var m = r.Var<MultiSet<Node>>("m");
                r.Naive();
                r.When(m.Is(Bag.Of()));
                r.Where(Primitives.MultiSetContains<Node>().Of(m, Num.Of(2L)));
                r.Then(Flags.Of(0L).Is(1L));
            });
            rules.DefineRule("set", r =>
            {
                var s = r.Var<Set<Node>>("s");
                r.Naive();
                r.When(s.Is(Set.Of()));
                r.Then(Gotten.Of().Is(Primitives.Get<Node>().Of(s, 0L)));
            });
            rules.DefineRule("set-contains", r =>
            {
                var s = r.Var<Set<Node>>("s");
                r.Naive();
                r.When(s.Is(Set.Of()));
                r.Where(Primitives.Contains<Node>().Of(s, Num.Of(2L)));
                r.Then(Flags.Of(1L).Is(1L));
            });
            rules.DefineRule("map", r =>
            {
                var d = r.Var<Map<Node, long>>("d");
                var v = r.Var<long>("v");
                r.Naive();
                r.When(d.Is(Dict.Of()), v.Is(Primitives.MapGet<Node, long>().Of(d, Num.Of(2L))));
                r.Then(Mapped.Of().Is(v));
            });
            rules.DefineRule("vector", r =>
            {
                var v = r.Var<Vec<Node>>("v");
                r.Naive();
                r.When(v.Is(Vector.Of()));
                r.Then(VectorWeights.Of().Is(Primitives.UnstableVecMap<Node, long>().Of(Primitives.UnstableFn<Node, long>(Weight).Of(), v)));
            });
            rules.DefineRule("apply", r =>
            {
                var f = r.Var<UnstableFn<long, long>>("f");
                r.Naive();
                r.When(f.Is(Fn.Of()));
                r.Then(Applied.Of().Is(Primitives.UnstableApp<long, long>().Of(f, 1L)));
            });

            return rules;
        }

    }

}
