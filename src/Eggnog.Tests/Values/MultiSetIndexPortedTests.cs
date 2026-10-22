using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// egglog's <c>unstable-multiset-fill-index</c> and <c>unstable-multiset-clear-index</c> (src/sort/multiset.rs at
    /// 5e655cae), ported from egglog's tests: <c>tests/web-demo/multiset.egg</c>, "Verify unstable multiset fill index and
    /// counting", and <c>tests/naive_unstable_fn_via_global.rs</c>.
    /// </summary>
    public sealed class MultiSetIndexPortedTests
    {

        sealed class Theory
        {

            public Theory()
            {
                Graph.DefineSort<Node>("Math");
                Num = Graph.DefineConstructor<long, Node>("Num");
                Graph.Values.RegisterMultiSet<Node>();
                Xs = Graph.DefineFunction<MultiSet<Node>>("xs");
                Product = Graph.DefineConstructor<MultiSet<Node>, Node>("product");

                // (function ms-count (Maths Math) i64 :merge (+ old new)) (sort MSIndexFn (UnstableFn (Maths Math) i64))
                Count = Graph.DefineFunction<MultiSet<Node>, Node, long>("ms-count", FunctionMerge.Expression<long>(static (old, @new) => Primitives.Add.Of(old, @new)));
                Graph.Values.RegisterFn<MultiSet<Node>, Node, long>();
                Rules = Graph.DefineRuleset("rules");
            }

            public EGraph Graph { get; } = new();

            public Scope Root => Graph.Root;

            public Constructor<long, Node> Num { get; }

            public Function<MultiSet<Node>> Xs { get; }

            public Constructor<MultiSet<Node>, Node> Product { get; }

            public Function<MultiSet<Node>, Node, long> Count { get; }

            public Ruleset Rules { get; }

            public Term<UnstableFn<MultiSet<Node>, Node, long>> CountFn => Primitives.UnstableFn<MultiSet<Node>, Node, long>(Count).Of();

            /// <summary>
            /// (rule ((= outer (product inner))) ((unstable-multiset-fill-index inner (unstable-fn "ms-count"))) :naive)
            /// </summary>
            public Rule DefineFill() => Rules.DefineRule("fill", r =>
            {
                var outer = r.Var<Node>("outer");
                var inner = r.Var<MultiSet<Node>>("inner");
                r.Naive();
                r.When(outer.Is(Product.Of(inner)));
                r.Then(Fact.Exists(Primitives.UnstableMultiSetFillIndex<Node>().Of(inner, CountFn)));
            });

        }

        /// <summary>
        /// web-demo/multiset.egg: the index counts each element of a product's multiset once, however often the rule runs
        /// again, adds the counts of elements that merge, and is cleared.
        /// </summary>
        [Fact]
        public void AnIndexCountsAMultisetsElements()
        {
            // (datatype Math (Num i64)) (sort Maths (MultiSet Math)) (let $xs (multiset-of (Num 1) (Num 2) (Num 3)))
            // (push) (union (Num 1) (Num 2)) (constructor product (Maths) Math) (let $aa (Num 100)) (let $zz (product $xs))
            var t = new Theory();
            var of = Primitives.MultiSetOf<Node>();
            t.Root.Assume(t.Xs.Of().Is(of.Of(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L))));
            t.Root.Union(t.Root.Make(t.Num.Of(1L)), t.Root.Make(t.Num.Of(2L)));
            var aa = t.Root.Make(t.Num.Of(100L));
            var zz = t.Root.Make(t.Product.Of(t.Root.Get(t.Xs.Of())));
            var rule = t.DefineFill();
            Assert.Equal("(rule ((= outer (product inner))) ((unstable-multiset-fill-index inner (unstable-fn \"ms-count\"))) :ruleset rules :naive)", rule.ToString());

            // (run 10) (check (= 2 (ms-count $xs (Num 1))))
            t.Root.Run(Schedule.Repeat(10, Schedule.Run(t.Rules)));
            Assert.True(t.Root.Check(t.Count.Of(t.Xs.Of(), t.Num.Of(1L)).Is(2L)));

            // (union $zz $aa) (run 10) (check (= 2 (ms-count $xs (Num 1)))): the rows are there, so the fill stops.
            t.Root.Union(zz, aa);
            t.Root.Run(Schedule.Repeat(10, Schedule.Run(t.Rules)));
            Assert.True(t.Root.Check(t.Count.Of(t.Xs.Of(), t.Num.Of(1L)).Is(2L)));

            // (union (Num 1) (Num 3)) (check (= 3 (ms-count $xs (Num 1)))): the two rows meet, and the merge adds them.
            t.Root.Union(t.Root.Make(t.Num.Of(1L)), t.Root.Make(t.Num.Of(3L)));
            Assert.True(t.Root.Check(t.Count.Of(t.Xs.Of(), t.Num.Of(1L)).Is(3L)));

            // (unstable-multiset-clear-index $xs (unstable-fn "ms-count")) (fail (ms-count $xs (Num 1)))
            t.Root.Assume(Fact.Exists(Primitives.UnstableMultiSetClearIndex<Node>().Of(t.Xs.Of(), t.CountFn)));
            Assert.ThrowsAny<Exception>(() => t.Root.Assume(Fact.Exists(t.Count.Of(t.Xs.Of(), t.Num.Of(1L)))));
            Assert.False(t.Root.Check(Fact.Exists(t.Count.Of(t.Xs.Of(), t.Num.Of(1L)))));
        }

        /// <summary>
        /// naive_unstable_fn_via_global.rs, <c>global_naive_widens_rule_context_same_as_local_naive</c>: egglog's global
        /// <c>--naive</c> runs the fill as a naive rule; here a rule is naive by its own <see cref="RuleBuilder.Naive"/>.
        /// </summary>
        [Fact]
        public void ANaiveRuleFillsTheIndex()
        {
            // (let $xs (multiset-of (Num 1) (Num 2))) (let $zz (product $xs)) (rule ... fill ...) (run 1)
            // (check (= 1 (ms-count $xs (Num 1))))
            var t = new Theory();
            t.Root.Assume(t.Xs.Of().Is(Primitives.MultiSetOf<Node>().Of(t.Num.Of(1L), t.Num.Of(2L))));
            t.Root.Make(t.Product.Of(t.Root.Get(t.Xs.Of())));
            t.DefineFill();
            t.Root.Run(t.Rules);
            Assert.True(t.Root.Check(t.Count.Of(t.Xs.Of(), t.Num.Of(1L)).Is(1L)));
        }


        [Fact]
        public void TheFillIsFullAndTheClearIsAWrite()
        {
            // egglog registers the fill as a FullPrim and the clear as a WritePrim: a semi-naive head, which writes and
            // does not read, takes the clear and refuses the fill.
            var t = new Theory();
            var refused = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("fill", r =>
            {
                var outer = r.Var<Node>("outer");
                var inner = r.Var<MultiSet<Node>>("inner");
                r.When(outer.Is(t.Product.Of(inner)));
                r.Then(Fact.Exists(Primitives.UnstableMultiSetFillIndex<Node>().Of(inner, t.CountFn)));
            }));
            Assert.Contains("unstable-multiset-fill-index", refused.Message, StringComparison.Ordinal);
            t.Rules.DefineRule("clear", r =>
            {
                var outer = r.Var<Node>("outer");
                var inner = r.Var<MultiSet<Node>>("inner");
                r.When(outer.Is(t.Product.Of(inner)));
                r.Then(Fact.Exists(Primitives.UnstableMultiSetClearIndex<Node>().Of(inner, t.CountFn)));
            });

            // A host write fills and clears, and a product made with a read of xs in the term is one class.
            t.Root.Assume(t.Xs.Of().Is(Primitives.MultiSetOf<Node>().Of(t.Num.Of(1L), t.Num.Of(1L), t.Num.Of(2L))));
            t.Root.Make(t.Product.Of(t.Xs.Of()));
            t.Root.Assume(Fact.Exists(Primitives.UnstableMultiSetFillIndex<Node>().Of(t.Xs.Of(), t.CountFn)));
            Assert.Equal(2L, t.Root.Get(t.Count.Of(t.Xs.Of(), t.Num.Of(1L))));
            Assert.Equal(1L, t.Root.Get(t.Count.Of(t.Xs.Of(), t.Num.Of(2L))));
            t.Root.Run(t.Rules);
            Assert.Equal(0, t.Root.CountRows(t.Count));
        }

        [Fact]
        public void APrimitiveHasNoIndex()
        {
            // egglog panics: "Primitive functions cannot be used with unstable-multiset-fill-index, since they cannot be set".
            var t = new Theory();
            var p = Primitive.Create<long>("p", [typeof(MultiSet<Node>), typeof(Node)], static _ => 0L);
            t.Root.Assume(t.Xs.Of().Is(Primitives.MultiSetOf<Node>().Of(t.Num.Of(1L))));
            var fill = Assert.ThrowsAny<Exception>(() => t.Root.Assume(Fact.Exists(Primitives.UnstableMultiSetFillIndex<Node>().Of(
                t.Xs.Of(), Primitives.UnstableFn<MultiSet<Node>, Node, long>(p).Of()))));
            Assert.Contains("cannot be set", fill.ToString(), StringComparison.Ordinal);
            var clear = Assert.ThrowsAny<Exception>(() => t.Root.Assume(Fact.Exists(Primitives.UnstableMultiSetClearIndex<Node>().Of(
                t.Xs.Of(), Primitives.UnstableFn<MultiSet<Node>, Node, long>(p).Of()))));
            Assert.Contains("cannot be deleted", clear.ToString(), StringComparison.Ordinal);
            Assert.False(t.Root.IsFaulted);
        }
    }

}
