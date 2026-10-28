using Eggnog.Tests.Support;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// The graph a random container program (<see cref="ContainerProgramGenerator"/>) runs over: a sort with
    /// <c>Num</c> and <c>Add</c>; vectors, sets, maps, multisets, pairs and function values of classes, and vectors of
    /// vectors of classes; constructors over a vector, a set, a map and a vector of vectors; a function of each container
    /// keyed by a number, and a function keyed by a vector whose merge is a lattice (<c>max</c>); relations that trigger
    /// the rules; and one ruleset whose rules compute with container primitives (<c>vec-push</c>, <c>set-insert</c>,
    /// <c>map-insert</c>, <c>set-union</c>, <c>vec-of</c>, <c>vec-union</c>, <c>multiset-union-values</c>,
    /// <c>unstable-app</c>) and with the pairs' elements. Each instance is a fresh graph, so a program and its replay
    /// each build one.
    /// </summary>
    sealed class ContainerProgramTheory
    {

        /// <summary>
        /// Builds the graph, its tables, its rules and its query.
        /// </summary>
        public ContainerProgramTheory()
        {
            var g = Graph = new EGraph();
            g.DefineSort<Node>("Math");
            Num = g.DefineConstructor<long, Node>("Num", 1);
            Add = g.DefineConstructor<Node, Node, Node>("Add", 3);
            g.Values.RegisterVec<Node>();
            g.Values.RegisterVec<Vec<Node>>();
            g.Values.RegisterSet<Node>();
            g.Values.RegisterMap<Node, Node>();
            g.Values.RegisterMultiSet<Node>();
            g.Values.RegisterPair<Node, Node>();
            g.Values.RegisterFn<Node, Node>();
            L = g.DefineConstructor<Vec<Node>, Node>("L", 2);
            LL = g.DefineConstructor<Vec<Vec<Node>>, Node>("LL", 2);
            SN = g.DefineConstructor<Set<Node>, Node>("SN", 2);
            MN = g.DefineConstructor<Map<Node, Node>, Node>("MN", 2);
            FV = g.DefineFunction<long, Vec<Node>>("fv");
            FVV = g.DefineFunction<long, Vec<Vec<Node>>>("fvv");
            FS = g.DefineFunction<long, Set<Node>>("fs");
            FM = g.DefineFunction<long, Map<Node, Node>>("fm");
            FMS = g.DefineFunction<long, MultiSet<Node>>("fms");
            FP = g.DefineFunction<long, Pair<Node, Node>>("fp");
            FF = g.DefineFunction<long, UnstableFn<Node, Node>>("ff");
            H = g.DefineFunction<Vec<Node>, long>("h", ValueLattice.Max<long>());
            Go = g.DefineRelation<long>("go");
            VU = g.DefineRelation<long, long>("vu");
            PU = g.DefineRelation<long>("pu");
            Tables = [Num, Add, L, LL, SN, MN, FV, FVV, FS, FM, FMS, FP, FF, H, Go, VU, PU];
            Constructors = [Num, Add, L, LL, SN, MN];
            Rules = g.DefineRuleset("r");
            DefineRules();

            var l = L;
            Var<Node>? head = null;
            Var<Node>? first = null;
            Heads = g.DefineQuery("heads", q =>
            {
                var n = head = q.Var<Node>("n");
                var v = q.Var<Vec<Node>>("v");
                var x = first = q.Var<Node>("x");
                q.When(n.Is(l.Of(v)), x.Is(Primitives.VecGet<Node>().Of(v, 0L)));
            });
            Head = head!;
            First = first!;
        }

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets <c>(Num i64)</c>.</summary>
        public Constructor<long, Node> Num { get; }

        /// <summary>Gets <c>(Add Math Math)</c>.</summary>
        public Constructor<Node, Node, Node> Add { get; }

        /// <summary>Gets a constructor over a vector of classes.</summary>
        public Constructor<Vec<Node>, Node> L { get; }

        /// <summary>Gets a constructor over a vector of vectors of classes.</summary>
        public Constructor<Vec<Vec<Node>>, Node> LL { get; }

        /// <summary>Gets a constructor over a set of classes.</summary>
        public Constructor<Set<Node>, Node> SN { get; }

        /// <summary>Gets a constructor over a map of classes.</summary>
        public Constructor<Map<Node, Node>, Node> MN { get; }

        /// <summary>Gets a function to a vector of classes.</summary>
        public Function<long, Vec<Node>> FV { get; }

        /// <summary>Gets a function to a vector of vectors of classes.</summary>
        public Function<long, Vec<Vec<Node>>> FVV { get; }

        /// <summary>Gets a function to a set of classes.</summary>
        public Function<long, Set<Node>> FS { get; }

        /// <summary>Gets a function to a map of classes.</summary>
        public Function<long, Map<Node, Node>> FM { get; }

        /// <summary>Gets a function to a multiset of classes.</summary>
        public Function<long, MultiSet<Node>> FMS { get; }

        /// <summary>Gets a function to a pair of classes.</summary>
        public Function<long, Pair<Node, Node>> FP { get; }

        /// <summary>Gets a function to a function value of classes.</summary>
        public Function<long, UnstableFn<Node, Node>> FF { get; }

        /// <summary>Gets a function keyed by a vector of classes, merged with <c>max</c>.</summary>
        public Function<Vec<Node>, long> H { get; }

        /// <summary>Gets the relation that triggers most rules.</summary>
        public Relation<long> Go { get; }

        /// <summary>Gets the relation that triggers the vector and set unions.</summary>
        public Relation<long, long> VU { get; }

        /// <summary>Gets the relation that triggers the multiset and pair unions.</summary>
        public Relation<long> PU { get; }

        /// <summary>Gets every table, in the order the view lists them.</summary>
        public TableHandle[] Tables { get; }

        /// <summary>Gets the constructors, which name classes in the view.</summary>
        public TableHandle[] Constructors { get; }

        /// <summary>Gets the ruleset.</summary>
        public Ruleset Rules { get; }

        /// <summary>Gets a query over the vectors <c>L</c> holds and their first elements.</summary>
        public Query Heads { get; }

        /// <summary>Gets the variable of <see cref="Heads"/> bound to the <c>L</c> term.</summary>
        public Var<Node> Head { get; }

        /// <summary>Gets the variable of <see cref="Heads"/> bound to the vector's first element.</summary>
        public Var<Node> First { get; }

        /// <summary>
        /// Writes the facts every program starts from into <paramref name="root"/>: six numbers, and one container of
        /// each kind.
        /// </summary>
        public void Setup(Scope root)
        {
            var num = Num;
            for (var i = 0L; i < 6; i++)
                root.Make(num.Of(i));

            root.Assume(FV.Of(0L).Is(Primitives.VecOf<Node>().Of(num.Of(1L), num.Of(2L))));
            root.Assume(FVV.Of(0L).Is(Primitives.VecOf<Vec<Node>>().Of(Primitives.VecOf<Node>().Of(num.Of(1L)), Primitives.VecOf<Node>().Of(num.Of(2L)))));
            root.Assume(FS.Of(0L).Is(Primitives.SetOf<Node>().Of(num.Of(1L), num.Of(3L))));
            root.Assume(FM.Of(0L).Is(Primitives.MapInsert<Node, Node>().Of(Primitives.MapEmpty<Node, Node>().Of(), num.Of(1L), num.Of(2L))));
            root.Assume(FMS.Of(0L).Is(Primitives.MultiSetOf<Node>().Of(num.Of(1L), num.Of(1L), num.Of(4L))));
            root.Assume(FP.Of(0L).Is(Primitives.PairOf<Node, Node>().Of(num.Of(2L), num.Of(3L))));
            root.Assume(FF.Of(0L).Is(Primitives.UnstableFn<Node, Node>(Add).Of(num.Of(1L))));
            root.Make(L.Of(Primitives.VecOf<Node>().Of(num.Of(0L))));
        }

        /// <summary>
        /// Defines the ruleset's rules.
        /// </summary>
        void DefineRules()
        {
            var num = Num;
            var l = L;
            var ll = LL;
            var sn = SN;
            var mn = MN;
            var fv = FV;
            var fs = FS;
            var fm = FM;
            var fms = FMS;
            var fp = FP;
            var ff = FF;
            var h = H;
            var go = Go;
            var vu = VU;
            var pu = PU;
            var r = Rules;
            r.DefineRule("push", b =>
            {
                var k = b.Var<long>("k");
                var v = b.Var<Vec<Node>>("v");
                var x = b.Var<Node>("x");
                b.When(go.Of(k), v.Is(fv.Of(k)), x.Is(Primitives.VecGet<Node>().Of(v, 0L)));
                b.Then(Fact.Exists(l.Of(Primitives.VecPush<Node>().Of(v, x))));
            });
            r.DefineRule("nest", b =>
            {
                var k = b.Var<long>("k");
                var v = b.Var<Vec<Node>>("v");
                b.When(go.Of(k), v.Is(fv.Of(k)));
                b.Then(Fact.Exists(ll.Of(Primitives.VecOf<Vec<Node>>().Of(v, v))));
            });
            r.DefineRule("set insert", b =>
            {
                var k = b.Var<long>("k");
                var s = b.Var<Set<Node>>("s");
                var v = b.Var<Vec<Node>>("v");
                var x = b.Var<Node>("x");
                b.When(go.Of(k), s.Is(fs.Of(k)), v.Is(fv.Of(k)), x.Is(Primitives.VecGet<Node>().Of(v, 1L)));
                b.Then(Fact.Exists(sn.Of(Primitives.Insert<Node>().Of(s, x))));
            });
            r.DefineRule("map insert", b =>
            {
                var k = b.Var<long>("k");
                var m = b.Var<Map<Node, Node>>("m");
                var v = b.Var<Vec<Node>>("v");
                var x = b.Var<Node>("x");
                b.When(go.Of(k), m.Is(fm.Of(k)), v.Is(fv.Of(k)), x.Is(Primitives.VecGet<Node>().Of(v, 0L)));
                b.Then(Fact.Exists(mn.Of(Primitives.MapInsert<Node, Node>().Of(m, x, num.Of(5L)))));
            });
            r.DefineRule("vec union", b =>
            {
                var (a, c) = b.Vars<long, long>("a", "c");
                var (v, u) = b.Vars<Vec<Node>, Vec<Node>>("v", "u");
                b.When(vu.Of(a, c), v.Is(fv.Of(a)), u.Is(fv.Of(c)));
                b.Then(Fact.Exists(Primitives.VecUnion<Node>().Of(v, u)));
            });
            r.DefineRule("set union", b =>
            {
                var (a, c) = b.Vars<long, long>("a", "c");
                var (s, t) = b.Vars<Set<Node>, Set<Node>>("s", "t");
                b.When(vu.Of(a, c), s.Is(fs.Of(a)), t.Is(fs.Of(c)));
                b.Then(Fact.Exists(sn.Of(Primitives.Union<Node>().Of(s, t))));
            });
            r.DefineRule("apply", b =>
            {
                var k = b.Var<long>("k");
                var f = b.Var<UnstableFn<Node, Node>>("f");
                var v = b.Var<Vec<Node>>("v");
                var x = b.Var<Node>("x");
                b.When(go.Of(k), f.Is(ff.Of(k)), v.Is(fv.Of(k)), x.Is(Primitives.VecGet<Node>().Of(v, 1L)));
                b.Then(Fact.Exists(Primitives.UnstableApp<Node, Node>().Of(f, x)));
            });
            r.DefineRule("multiset union", b =>
            {
                var k = b.Var<long>("k");
                var ms = b.Var<MultiSet<Node>>("ms");
                b.When(pu.Of(k), ms.Is(fms.Of(k)));
                b.Then(Fact.Exists(Primitives.MultiSetUnionValues<Node>().Of(ms)));
            });
            r.DefineRule("pair union", b =>
            {
                var k = b.Var<long>("k");
                var p = b.Var<Pair<Node, Node>>("p");
                var (x, y) = b.Vars<Node, Node>("x", "y");
                b.When(pu.Of(k), p.Is(fp.Of(k)), x.Is(Primitives.PairFirst<Node, Node>().Of(p)), y.Is(Primitives.PairSecond<Node, Node>().Of(p)));
                b.Then(x.Is(y));
            });
            r.DefineRule("pair vector", b =>
            {
                var k = b.Var<long>("k");
                var p = b.Var<Pair<Node, Node>>("p");
                var (x, y) = b.Vars<Node, Node>("x", "y");
                b.When(go.Of(k), p.Is(fp.Of(k)), x.Is(Primitives.PairFirst<Node, Node>().Of(p)), y.Is(Primitives.PairSecond<Node, Node>().Of(p)));
                b.Then(h.Of(Primitives.VecOf<Node>().Of(x, y)).Is(k));
            });
            r.DefineRule("third", b =>
            {
                var n = b.Var<Node>("n");
                var v = b.Var<Vec<Node>>("v");
                var x = b.Var<Node>("x");
                b.When(go.Of(99L), n.Is(l.Of(v)), x.Is(Primitives.VecGet<Node>().Of(v, 2L)));
                b.Then(Fact.Exists(l.Of(Primitives.VecOf<Node>().Of(x))));
            });
        }

    }

}
