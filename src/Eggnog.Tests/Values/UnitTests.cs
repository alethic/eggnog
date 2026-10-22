using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// egglog's <c>Unit</c> sort, of <see cref="ValueTuple"/>: functions of it, its rendering, extraction and
    /// serialization, and the fallible forms of the filter primitives, its primitives.
    /// </summary>
    public sealed class UnitTests
    {

        [Fact]
        public void TheGraphRegistersUnit()
        {
            var graph = new EGraph();
            var unit = graph.Values.TypeOf<ValueTuple>();
            Assert.Equal("Unit", unit.Name);
            Assert.Equal("()", unit.Format(default));
            // (sort Unit) is "Sort Unit already declared." in egglog: the name is the sort's.
            Assert.Contains("Unit", Assert.Throws<ArgumentException>(() => graph.Values.Register<Guid>("Unit")).Message, StringComparison.Ordinal);
            Assert.Contains("Unit", Assert.Throws<ArgumentException>(() => graph.DefineSort<Node>("Unit")).Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AFunctionOfUnitHoldsRows()
        {
            // (datatype Math (Num i64)) (function is-even (Math) Unit :no-merge) (set (is-even (Num 2)) ())
            // (check (= (is-even (Num 2)) ())) (check (is-even (Num 2))) (fail (check (is-even (Num 3))))
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var isEven = graph.DefineFunction<Node, ValueTuple>("is-even");
            var root = graph.Root;
            root.Assume(isEven.Of(num.Of(2L)).Is(default(ValueTuple)));
            root.Make(num.Of(3L));
            Assert.True(root.Check(isEven.Of(num.Of(2L)).Is(default(ValueTuple))));
            Assert.True(root.Check(Fact.Exists(isEven.Of(num.Of(2L)))));
            Assert.False(root.Check(Fact.Exists(isEven.Of(num.Of(3L)))));
            Assert.Equal(default, root.Get(isEven.Of(num.Of(2L))));

            // A rule matches its rows: (rule ((is-even x)) ((set (even x) true))).
            var even = graph.DefineFunction<Node, bool>("even");
            var rules = graph.DefineRuleset("rules");
            var rule = rules.DefineRule("even", r =>
            {
                var n = r.Var<Node>("n");
                r.When(isEven.Of(n).Is(default(ValueTuple)));
                r.Then(even.Of(n).Is(true));
            });
            Assert.Equal("(rule ((= (is-even n) ())) ((set (even n) true)) :ruleset rules)", rule.ToString());
            root.Run(rules);
            Assert.True(root.Get(even.Of(num.Of(2L))));
        }

        [Fact]
        public void UnitSerializesAndExtractsAsEgglogs()
        {
            // egglog at 5e655cae, --to-json of (datatype Math (Num i64)) (function is-even (Math) Unit :no-merge)
            // (set (is-even (Num 2)) ()) (function u () Unit :no-merge) (set (u) ()): the unit value is the node
            // primitive-Unit-0, op "()", of class Unit-0, where the rows function-0-is-even and function-0-u are too.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var isEven = graph.DefineFunction<Node, ValueTuple>("is-even");
            var u = graph.DefineFunction<ValueTuple>("u");
            var root = graph.Root;
            root.Assume(isEven.Of(num.Of(2L)).Is(default(ValueTuple)), u.Of().Is(default(ValueTuple)));

            var nodes = root.Serialize().Nodes.Select(n => $"{n.Id} {n.Op} [{string.Join(", ", n.Children)}] {n.Class}");
            Assert.Equal(
                new[]
                {
                    "primitive-i64-2 2 [] i64-2",
                    "function-0-Num Num [primitive-i64-2] Math-0",
                    "primitive-Unit-0 () [] Unit-0",
                    "function-0-is-even is-even [function-0-Num] Unit-0",
                    "function-0-u u [] Unit-0",
                },
                nodes);

            // (constructor Tag (Unit) Math): a term of a unit extracts with egglog's literal.
            var tag = graph.DefineConstructor<ValueTuple, Node>("Tag");
            var tagged = root.Make(tag.Of(default(ValueTuple)));
            Assert.Equal("(Tag ())", root.Extract(tagged).ToString());
        }

        [Fact]
        public void AFilterPrimitiveHasEgglogsFallibleForm()
        {
            // (check (< 1 2)) holds, and (< 2 1) has no result: the Unit-valued primitive egglog names "<".
            var fallible = Primitives.Fallible(Primitives.Less);
            Assert.Equal("<", fallible.Name);
            Assert.Same(fallible, Primitives.Fallible(Primitives.Less));
            Assert.Equal(typeof(ValueTuple), fallible.ResultType);
            Assert.Equal("multiset-contains-swapped", Primitives.Fallible(Primitives.MultiSetContainsSwapped<long>()).Name);
            Assert.Equal("!=", Primitives.Fallible(Primitives.NotEqual<string>()).Name);
            Assert.Throws<ArgumentException>(() => Primitives.Fallible(Primitives.Add));

            // In a rule, through a function value: (rule ((= f (lt)) (keys k) (= () (unstable-app f k))) ((set (hit k) 1))): the
            // keys above 3.
            var graph = new EGraph();
            graph.Values.RegisterFn<long, ValueTuple>();
            var lt = graph.DefineFunction<UnstableFn<long, ValueTuple>>("lt");
            var keys = graph.DefineRelation<long>("keys");
            var hit = graph.DefineFunction<long, long>("hit");
            graph.Root.Assume(lt.Of().Is(Primitives.UnstableFn<long, ValueTuple>(fallible).Of(3L)));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("lt", r =>
            {
                var (f, k) = r.Vars<UnstableFn<long, ValueTuple>, long>("f", "k");
                r.When(f.Is(lt.Of()), keys.Of(k), Primitives.UnstableApp<long, ValueTuple>().Of(f, k).Is(default(ValueTuple)));
                r.Then(hit.Of(k).Is(1L));
            });
            graph.Root.Assume(keys.Of(2L), keys.Of(5L));
            graph.Root.Run(rules);
            Assert.False(graph.Root.TryGet(hit.Of(2L), out _));
            Assert.Equal(1L, graph.Root.Get(hit.Of(5L)));
        }

        [Fact]
        public void GuardAndValueEqAreEgglogsOtherFalliblePrimitives()
        {
            // egglog at 5e655cae: (check (= (unstable-app (unstable-fn "guard") true) ()))
            // (fail (check (= (unstable-app (unstable-fn "guard") false) ()))) (check (= (unstable-app (unstable-fn "value-eq" 1) 1) ()))
            var graph = new EGraph();
            Assert.Equal("guard", BooleanPrimitives.Guard.Name);
            Assert.Equal("value-eq", Primitives.ValueEq<long>().Name);
            Assert.Same(Primitives.ValueEq<long>(), Primitives.ValueEq<long>());
            graph.Values.RegisterFn<bool, ValueTuple>();
            graph.Values.RegisterFn<long, ValueTuple>();
            var holds = Primitives.UnstableApp<bool, ValueTuple>();
            var guard = Primitives.UnstableFn<bool, ValueTuple>(BooleanPrimitives.Guard);
            Assert.True(graph.Root.Check(holds.Of(guard.Of(), true).Is(default(ValueTuple))));
            Assert.False(graph.Root.Check(holds.Of(guard.Of(), false).Is(default(ValueTuple))));
            var same = Primitives.UnstableFn<long, ValueTuple>(Primitives.ValueEq<long>());
            Assert.True(graph.Root.Check(Primitives.UnstableApp<long, ValueTuple>().Of(same.Of(1L), 1L).Is(default(ValueTuple))));
            Assert.False(graph.Root.Check(Primitives.UnstableApp<long, ValueTuple>().Of(same.Of(1L), 2L).Is(default(ValueTuple))));

            // Over classes, as a filter: (unstable-multiset-filter (unstable-fn "value-eq" (Num 1)) (multiset-of (Num 1) (Num 2))).
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterMultiSet<Node>();
            graph.Values.RegisterFn<Node, ValueTuple>();
            var of = Primitives.MultiSetOf<Node>();
            var kept = graph.DefineFunction<MultiSet<Node>>("kept");
            graph.Root.Assume(kept.Of().Is(Primitives.UnstableMultiSetFilter<Node>().Of(
                Primitives.UnstableFn<Node, ValueTuple>(Primitives.ValueEq<Node>()).Of(num.Of(1L)), of.Of(num.Of(1L), num.Of(2L)))));
            Assert.True(graph.Root.Check(kept.Of().Is(of.Of(num.Of(1L)))));
        }

    }

}
