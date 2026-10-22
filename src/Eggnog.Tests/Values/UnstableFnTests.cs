using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Function values, egglog's <c>UnstableFn</c> sort: registration, host values, targets' signatures, rendering,
    /// serialization and extraction, classes in the partially applied arguments, and scopes.
    /// </summary>
    public sealed class UnstableFnTests
    {

        [Fact]
        public void RegistrationNamesTheFunctionSort()
        {
            var graph = new EGraph();
            var type = graph.Values.RegisterFn<long, long>();
            Assert.Equal("UnstableFn<(i64), i64>", type.Name);
            Assert.Same(type, graph.Values.RegisterFn<long, long>());
            Assert.Equal("UnstableFn<(i64, String), bool>", graph.Values.RegisterFn<long, string, bool>().Name);
            var register = Assert.Throws<ArgumentException>(() => graph.Values.Register<UnstableFn<bool, bool>>("F"));
            Assert.Contains("ValueStore.RegisterFn", register.Message, StringComparison.Ordinal);
            var unknown = Assert.Throws<ArgumentException>(() => graph.Values.TypeOf<UnstableFn<bool, bool>>());
            Assert.Contains("ValueStore.RegisterFn", unknown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HostValuesRoundTripAndApply()
        {
            var graph = new EGraph();
            graph.Values.RegisterFn<long, long>();
            var f = graph.DefineFunction<long, UnstableFn<long, long>>("f");
            var app = Primitives.UnstableApp<long, long>();
            var plusTwo = UnstableFn.Of<long, long>(Primitives.Add, 2L);
            graph.Root.Assume(f.Of(1L).Is(plusTwo), f.Of(2L).Is(Primitives.UnstableFn<long, long>(Primitives.Add).Of(2L)));

            var read = graph.Root.Get(f.Of(1L));
            Assert.Equal(plusTwo, read);
            Assert.Same(Primitives.Add, read.Target);
            Assert.Equal("+", read.Name);
            Assert.Equal(new object[] { 2L }, read.Arguments);
            Assert.Equal("(unstable-fn \"+\" 2)", read.ToString());
            Assert.True(graph.Root.Check(f.Of(1L).Is(f.Of(2L))));
            Assert.False(graph.Root.Check(f.Of(1L).Is(UnstableFn.Of<long, long>(Primitives.Add, 3L))));
            Assert.True(graph.Root.Check(app.Of(f.Of(1L), 3L).Is(5L)));
            Assert.True(graph.Root.Check(app.Of(plusTwo, 3L).Is(5L)));

            var none = Assert.Throws<ArgumentException>(() => graph.Root.Assume(f.Of(3L).Is(default(UnstableFn<long, long>))));
            Assert.Contains("default", none.Message, StringComparison.Ordinal);

            // egglog has no literal for a function value, so a rule makes one with unstable-fn.
            var literal = Assert.Throws<RuleDefinitionException>(() => graph.DefineRuleset("rules").DefineRule("constant", r =>
            {
                var k = r.Var<long>("k");
                r.When(f.Of(k).Is(plusTwo));
                r.Then(f.Of(Primitives.Add.Of(k, 10L)).Is(plusTwo));
            }));
            Assert.Contains("Primitives.UnstableFn", literal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ATargetsSignatureEndsWithTheSorts()
        {
            // fail-typecheck/unstable-fn-wrong-args-type.egg and unstable-fn-wrong-return-type.egg:
            // (datatype Math (Zero) (Inc Math)) (sort Fn (UnstableFn (i64) Math)) (unstable-fn "Inc"), and (UnstableFn (Math) i64).
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var inc = graph.DefineConstructor<Node, Node>("Inc");
            Assert.Throws<ArgumentException>(() => Primitives.UnstableFn<long, Node>(inc));
            Assert.Throws<ArgumentException>(() => UnstableFn.Of<long, Node>(inc));
            Assert.Throws<ArgumentException>(() => Primitives.UnstableFn<Node, long>(inc));

            // fail-typecheck/unstable-fn-primitive-missing-partial-arg.egg: (unstable-app (unstable-fn "+") 2) of
            // (UnstableFn (i64) i64), which "+" is only partially applied to one argument.
            Assert.Throws<ArgumentException>(() => Primitives.UnstableFn<long, long>(Primitives.Add).Of());
            Assert.Throws<ArgumentException>(() => UnstableFn.Of<long, long>(Primitives.Add));
            Assert.Throws<ArgumentException>(() => UnstableFn.Of<long, long>(Primitives.Add, "2"));

            // fail-typecheck/unstable-fn-wrong-args.egg: (sort Fn (UnstableFn (Math) Math)) (unstable-app (unstable-fn "Inc") 10)
            Assert.Throws<ArgumentException>(() => Primitives.UnstableApp<Node, Node>().Of(Primitives.UnstableFn<Node, Node>(inc).Of(), 10L));

            // A function value of another graph's table is not this graph's.
            var other = new EGraph();
            other.DefineSort<Node>("Math");
            other.Values.RegisterFn<Node, Node>();
            var held = other.DefineFunction<UnstableFn<Node, Node>>("held");
            var foreign = Assert.Throws<ArgumentException>(() => other.Root.Assume(held.Of().Is(UnstableFn.Of<Node, Node>(inc))));
            Assert.Contains("another graph", foreign.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ARelationIsNoTarget()
        {
            // egglog desugars (relation R (i64)) to a constructor of the reserved sort @RSort, which no function sort names.
            var graph = new EGraph();
            var rel = graph.DefineRelation<long>("R");
            var primitive = Assert.Throws<ArgumentException>(() => Primitives.UnstableFn<long, bool>(rel));
            Assert.Contains("'R' is a relation", primitive.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => UnstableFn.Of<long, bool>(rel));
        }

        [Fact]
        public void AFunctionValueRendersSerializesAndExtractsAsEgglogs()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var add = graph.DefineConstructor<Node, Node, Node>("Add");
            graph.Values.RegisterFn<long, long>();
            graph.Values.RegisterFn<Node, Node>();
            var leaf = graph.DefineConstructor<UnstableFn<long, long>, Node>("Leaf");
            var hold = graph.DefineConstructor<UnstableFn<Node, Node>, Node>("Hold");
            var output = graph.DefineFunction<long, long>("out");
            var x = graph.Root.Make(leaf.Of(Primitives.UnstableFn<long, long>(Primitives.Add).Of(2L)));
            var y = graph.Root.Make(hold.Of(Primitives.UnstableFn<Node, Node>(add).Of(num.Of(1L))));
            var rules = graph.DefineRuleset("rules");
            var rule = rules.DefineRule("apply", r =>
            {
                var f = r.Var<UnstableFn<long, long>>("f");
                var n = r.Var<Node>("n");
                r.When(n.Is(leaf.Of(f)));
                r.Then(output.Of(0L).Is(Primitives.UnstableApp<long, long>().Of(f, 1L)));
            });

            Assert.Equal("(rule ((= n (Leaf f))) ((set (out 0) (unstable-app f 1))) :ruleset rules)", rule.ToString());

            // A container costs its elements', as egglog's: repro-738-fn-sort.egg extracts (unstable-fn "fn" (x)) at 1.
            Assert.Equal("(Leaf (unstable-fn \"+\" 2))", graph.Root.Extract(x).ToString());
            Assert.Equal<ulong>(2, graph.Root.Extract(x).Cost);
            Assert.Equal("(Hold (unstable-fn \"Add\" (Num 1)))", graph.Root.Extract(y).ToString());
            Assert.Equal<ulong>(3, graph.Root.Extract(y).Cost);

            // A value serializes as a node of its function's name, its partially applied arguments its children.
            var serialized = graph.Root.Serialize();
            var nodes = serialized.Nodes.Where(n => n.Op is "+" or "Add").Select(n => $"{n.Op} [{string.Join(", ", n.Children)}]");
            Assert.Equal(new[] { "+ [primitive-i64-2]", "Add [function-0-Num]" }, nodes);
        }

        [Fact]
        public void FunctionValuesBecomeEqualWhenTheirClassesMerge()
        {
            // (sort MathFn (UnstableFn (Math) Math)) (function f (i64) MathFn :no-merge)
            // (set (f 1) (unstable-fn "Add" (Num 1))) (set (f 2) (unstable-fn "Add" (Num 2))) (union (Num 1) (Num 2))
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var add = graph.DefineConstructor<Node, Node, Node>("Add");
            graph.Values.RegisterFn<Node, Node>();
            var f = graph.DefineFunction<long, UnstableFn<Node, Node>>("f");
            var addTo = Primitives.UnstableFn<Node, Node>(add);
            var root = graph.Root;
            root.Assume(f.Of(1L).Is(addTo.Of(num.Of(1L))), f.Of(2L).Is(addTo.Of(num.Of(2L))));
            Assert.False(root.Check(f.Of(1L).Is(f.Of(2L))));

            root.Union(root.Make(num.Of(1L)), root.Make(num.Of(2L)));
            Assert.True(root.Check(f.Of(1L).Is(f.Of(2L))));
            var read = root.Get(f.Of(1L));
            Assert.Equal(root.Get(f.Of(2L)), read);
            Assert.True(root.AreEqual((Node)read.Arguments[0], num.Of(1L)));

            // (let $applied (unstable-app (f 1) (Num 3))) (check (= $applied (Add (Num 2) (Num 3))))
            var applied = graph.DefineFunction<Node>("applied");
            root.Assume(applied.Of().Is(Primitives.UnstableApp<Node, Node>().Of(f.Of(1L), num.Of(3L))));
            Assert.True(root.AreEqual(root.Get(applied.Of()), add.Of(num.Of(2L), num.Of(3L))));
        }

        [Fact]
        public void AChildMakesFunctionValuesOfValuesButNoneOfNewClasses()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var add = graph.DefineConstructor<Node, Node, Node>("Add");
            graph.Values.RegisterFn<long, long>();
            graph.Values.RegisterFn<Node, Node>();
            var f = graph.DefineFunction<long, UnstableFn<long, long>>("f");
            var g = graph.DefineFunction<long, UnstableFn<Node, Node>>("g");
            var addTo = Primitives.UnstableFn<Node, Node>(add);
            var root = graph.Root;
            root.Assume(g.Of(1L).Is(addTo.Of(num.Of(1L))));
            root.Freeze();
            using var child = root.CreateChild();

            // A value of values, the child makes.
            child.Assume(f.Of(1L).Is(Primitives.UnstableFn<long, long>(Primitives.Add).Of(5L)));
            Assert.True(child.Check(Primitives.UnstableApp<long, long>().Of(f.Of(1L), 1L).Is(6L)));

            // One of classes the root holds, it uses; a new one, it does not make.
            child.Assume(g.Of(2L).Is(addTo.Of(num.Of(1L))));
            Assert.True(child.Check(g.Of(2L).Is(g.Of(1L))));
            var made = Assert.Throws<RuleEvaluationException>(() => child.Assume(g.Of(3L).Is(addTo.Of(num.Of(2L)))));
            Assert.Contains("only the root interns", made.Message, StringComparison.Ordinal);
            Assert.False(child.IsFaulted);

            // A function sort registers while the root is frozen, its values never holding classes.
            graph.Values.RegisterFn<string, long>();
        }

    }

}
