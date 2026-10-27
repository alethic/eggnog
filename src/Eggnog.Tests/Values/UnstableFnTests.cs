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

            // A function sort registers while the root is frozen too, into the environment that holds ids.
            graph.Values.RegisterFn<string, long>();
            Assert.True(graph.Engine.ContainerValues.GetEnvironment<Relations.FunctionContainer>().HoldsIds);
        }

        /// <summary>
        /// The first function sort registers while the root is frozen and a child of it lives, as egglog declares
        /// <c>(sort IntFn (UnstableFn (i64) i64))</c> inside a <c>(push)</c>: its environment holds ids, as egglog's
        /// function sorts' do, so the containers of classes the graph had none of before exist from then on, and the
        /// child's container pass, which walks every type that holds ids, walks it from its next pass. Function values
        /// of values still intern from any scope into the graph's one pool: the child and a sibling made later make one
        /// value of one cell. The expectation is egglog's at 5e655cae running probe late-fn.egg, which exits clean:
        /// (Num 1), then twice a push that declares the sort and (function h () IntFn :no-merge), sets (h) to
        /// (unstable-fn "+" 5) and checks (= (unstable-app (h) 1) 6). A function value of a class registered so is still
        /// one the child cannot make, as any container of classes (decisions row 9).
        /// </summary>
        [Fact]
        public void AFunctionSortRegisteredWhileTheRootIsFrozenHoldsIds()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var add = graph.DefineConstructor<Node, Node, Node>("Add");
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            root.Freeze();
            var containers = graph.Engine.ContainerValues;
            Assert.False(containers.HasEqContainers);
            Function<UnstableFn<long, long>> h;
            int functions;

            using (var child = root.CreateChild())
            {
                graph.Values.RegisterFn<long, long>();
                Assert.True(containers.GetEnvironment<Relations.FunctionContainer>().HoldsIds);
                Assert.True(containers.HasEqContainers);

                h = graph.DefineFunction<UnstableFn<long, long>>("h");
                child.Union(one, two);
                var before = graph.Values.InternedCount;
                child.Assume(h.Of().Is(Primitives.UnstableFn<long, long>(Primitives.Add).Of(5L)));
                Assert.True(child.Check(Primitives.UnstableApp<long, long>().Of(h.Of(), 1L).Is(6L)));
                Assert.True(child.AreEqual(one, two));
                child.Engine.Context.CheckInvariants();

                // The child's function value went into the graph's pool, not into the child's own containers.
                var interned = graph.Values.InternedCount;
                Assert.True(interned > before);
                functions = containers.GetEnvironment<Relations.FunctionContainer>().Count;

                using (var sibling = root.CreateChild())
                {
                    sibling.Assume(h.Of().Is(Primitives.UnstableFn<long, long>(Primitives.Add).Of(5L)));
                    Assert.True(sibling.Check(Primitives.UnstableApp<long, long>().Of(h.Of(), 1L).Is(6L)));
                    Assert.Equal(interned, graph.Values.InternedCount);
                    Assert.False(sibling.AreEqual(one, two));
                }

                graph.Values.RegisterFn<Node, Node>();
                var g = graph.DefineFunction<UnstableFn<Node, Node>>("g");
                var made = Assert.Throws<RuleEvaluationException>(() => child.Assume(g.Of().Is(Primitives.UnstableFn<Node, Node>(add).Of(num.Of(1L)))));
                Assert.Contains("only the root interns", made.Message, StringComparison.Ordinal);
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(one, two));
            Assert.Equal(0, root.CountRows(h));

            // The function value outlives the scopes that made it, in the graph's pool.
            Assert.Equal(functions, containers.GetEnvironment<Relations.FunctionContainer>().Count);
            containers.CheckInvariants();
        }

        [Fact]
        public void ANestedFunctionValueOfValuesIsAValue()
        {
            // (unstable-fn "first" (unstable-fn "+" 1)) holds no class, so a child makes it.
            var graph = new EGraph();
            graph.Values.RegisterFn<long, long>();
            var app = Primitives.UnstableApp<long, long>();
            var first = Primitive.Create<long>("first", [typeof(UnstableFn<long, long>), typeof(long)], args => (long)args[1]);
            var h = graph.DefineFunction<UnstableFn<long, long>>("h");
            graph.Root.Freeze();
            using var child = graph.Root.CreateChild();
            child.Assume(h.Of().Is(Primitives.UnstableFn<long, long>(first).Of(Primitives.UnstableFn<long, long>(Primitives.Add).Of(1L))));
            Assert.True(child.Check(app.Of(h.Of(), 7L).Is(7L)));
            Assert.Equal("(unstable-fn \"first\" (unstable-fn \"+\" 1))", child.Get(h.Of()).ToString());
        }

        [Fact]
        public void AChildMergesAClassAFunctionValueHoldsAndRebuildsTheValue()
        {
            // Probe fn-child-merge.egg, which egglog at 5e655cae runs clean: (Num 2), (Num 1), (set (score (Num 1) 1)
            // 5) and (set (fv) (unstable-fn "score" (Num 1))); in a push (union (Num 1) (Num 2)) displaces (Num 1),
            // which the value holds, so (fv) is (unstable-fn "score" (Num 2)) there, (unstable-app (fv) 1) still 5, and a
            // naive rule's (set (out 0) (unstable-app f 1)) writes 5; after the pop the classes are apart and (out 0) has
            // no value. The child's host merge rebuilds the child's copy of the value.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var score = graph.DefineFunction<Node, long, long>("score");
            graph.Values.RegisterFn<long, long>();
            var app = Primitives.UnstableApp<long, long>();
            var fv = graph.DefineFunction<UnstableFn<long, long>>("fv");
            var output = graph.DefineFunction<long, long>("out");
            var root = graph.Root;
            var two = root.Make(num.Of(2L));
            var one = root.Make(num.Of(1L));
            root.Assume(score.Of(num.Of(1L), 1L).Is(5L), fv.Of().Is(Primitives.UnstableFn<long, long>(score).Of(num.Of(1L))));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("apply", r =>
            {
                var f = r.Var<UnstableFn<long, long>>("f");
                r.Naive();
                r.When(f.Is(fv.Of()));
                r.Then(output.Of(0L).Is(app.Of(f, 1L)));
            });
            root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Union(one, two);
                Assert.False(child.IsFaulted);
                Assert.True(child.AreEqual(one, two));
                Assert.True(child.Check(fv.Of().Is(Primitives.UnstableFn<long, long>(score).Of(num.Of(2L)))));
                Assert.True(child.AreEqual((Node)child.Get(fv.Of()).Arguments[0], two));
                Assert.True(child.Check(score.Of(num.Of(2L), 1L).Is(5L)));
                Assert.True(child.Check(app.Of(fv.Of(), 1L).Is(5L)));
                child.Run(rules);
                Assert.True(child.Check(output.Of(0L).Is(5L)));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(one, two));
            Assert.True(root.Check(app.Of(fv.Of(), 1L).Is(5L)));
            Assert.False(root.TryGet(output.Of(0L), out _));
            Assert.False(root.Check(score.Of(num.Of(2L), 1L).Is(5L)));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AWritePrimitiveAppliesInAChild(bool frozen)
        {
            // Probe fn-write-primitive-child.egg, which egglog at 5e655cae runs clean: (set (fv) (unstable-fn
            // "vec-union" (vec-of (Num 1)))) and (set (out 0) (unstable-app (fv) (vec-of (Num 2)))) union (Num 1) and
            // (Num 2) at the root; (out 1) is (vec-of (Num 3)) and (out 3) (vec-of (Num 4)). In a push (set (out 2)
            // (unstable-app (fv) (out 1))) unions (Num 1) and (Num 3), which the vectors hold, so (out 2) is (vec-of
            // (Num 1)) and (out 1) is (out 0); and a rule's (set (out 4) (unstable-app f v)) over (out 3) unions (Num 4)
            // too. After the pop the classes and vectors are apart. Here the push is a child, the write its host write.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterFn<Vec<Node>, Vec<Node>>();
            var of = Primitives.VecOf<Node>();
            var app = Primitives.UnstableApp<Vec<Node>, Vec<Node>>();
            var fv = graph.DefineFunction<UnstableFn<Vec<Node>, Vec<Node>>>("fv");
            var output = graph.DefineFunction<long, Vec<Node>>("out");
            var go = graph.DefineRelation("go");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("apply", r =>
            {
                var f = r.Var<UnstableFn<Vec<Node>, Vec<Node>>>("f");
                var v = r.Var<Vec<Node>>("v");
                r.When(go.Of(), f.Is(fv.Of()), v.Is(output.Of(3L)));
                r.Then(output.Of(4L).Is(app.Of(f, v)));
            });
            var root = graph.Root;
            root.Assume(fv.Of().Is(Primitives.UnstableFn<Vec<Node>, Vec<Node>>(Primitives.VecUnion<Node>()).Of(of.Of(num.Of(1L)))));
            root.Assume(output.Of(0L).Is(app.Of(fv.Of(), of.Of(num.Of(2L)))));
            Assert.True(root.Check(num.Of(1L).Is(num.Of(2L))));

            root.Assume(output.Of(1L).Is(of.Of(num.Of(3L))), output.Of(3L).Is(of.Of(num.Of(4L))));
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Assume(output.Of(2L).Is(app.Of(fv.Of(), output.Of(1L))));
                Assert.False(child.IsFaulted);
                Assert.True(child.Check(num.Of(1L).Is(num.Of(3L))));
                Assert.True(child.Check(output.Of(2L).Is(of.Of(num.Of(1L)))));
                Assert.True(child.Check(output.Of(1L).Is(output.Of(0L))));

                child.Assume(go.Of());
                child.Run(rules);
                Assert.True(child.Check(num.Of(4L).Is(num.Of(1L))));
                Assert.True(child.Check(output.Of(4L).Is(output.Of(3L))));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.Check(num.Of(1L).Is(num.Of(3L))));
            Assert.False(root.Check(num.Of(1L).Is(num.Of(4L))));
            Assert.False(root.Check(output.Of(1L).Is(output.Of(0L))));
            Assert.False(root.TryGet(output.Of(2L), out _));
        }

        /// <summary>
        /// A child's union in which a function value holding the class that loses rebuilds to the value a row of a
        /// function with a merge that is not a lattice (<see cref="FunctionMerge"/>) is keyed by is refused exactly when
        /// the row's value is the one that loses its id, the other value interned first; where the row's value was
        /// interned first it keeps its id, the other merges into it, the row stands, and the union goes through. Probe
        /// root-only-fn-smaller-id.egg, which egglog at 5e655cae runs clean: (A) and (B), then
        /// (set (rf (unstable-fn "score" (A))) 1) and (set (hf 1) (unstable-fn "score" (B))) in either order; in a push
        /// (union (A) (B)) rebuilds (hf 1) to the row's value and leaves (rf (hf 1)) at 1, and the pop restores (hf 1).
        /// egglog's push accepts both, where the library writes such a function only at the root (decisions row 2).
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void AChildsMergeOfAClassAFunctionValueHoldsIsRefusedOnlyWhereItsRebuildMergesARowsValueAway(bool rowFirst, bool refused)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var score = graph.DefineFunction<Node, long, long>("score");
            graph.Values.RegisterFn<long, long>();
            var fn = Primitives.UnstableFn<long, long>(score);
            var hf = graph.DefineFunction<long, UnstableFn<long, long>>("hf");
            var rf = graph.DefineFunction<UnstableFn<long, long>, long>("rf", FunctionMerge.Of<long>("sum", (x, y) => checked(x + y)));
            var root = graph.Root;
            var na = root.Make(a.Of());
            var nb = root.Make(b.Of());
            var held = hf.Of(1L).Is(fn.Of(b.Of()));
            var row = rf.Of(fn.Of(a.Of())).Is(1L);
            if (rowFirst)
                root.Assume(row, held);
            else
                root.Assume(held, row);

            root.Freeze();
            using (var child = root.CreateChild())
            {
                if (refused)
                {
                    var e = Assert.Throws<InvalidOperationException>(() => child.Union(na, nb));
                    Assert.Contains("'rf' has the merge 'sum'", e.Message, StringComparison.Ordinal);
                    Assert.Contains("only the root may write it", e.Message, StringComparison.Ordinal);
                    Assert.False(child.IsFaulted);
                    Assert.False(child.AreEqual(na, nb));
                    Assert.True(child.Check(held));
                }
                else
                {
                    Assert.True(child.Union(na, nb));
                    Assert.False(child.IsFaulted);
                    Assert.True(child.Check(hf.Of(1L).Is(fn.Of(a.Of())), rf.Of(hf.Of(1L)).Is(1L)));
                }

                Assert.True(child.Check(row));
                Assert.Equal(1, child.CountRows(rf));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(na, nb));
            Assert.True(root.Check(held, row));
        }

        [Fact]
        public void FunctionValuesOrderAsTheirContents()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var add = graph.DefineConstructor<Node, Node, Node>("Add");
            graph.Values.RegisterFn<Node, Node>();
            var one = graph.Root.Make(num.Of(1L));
            var two = graph.Root.Make(num.Of(2L));
            var x = UnstableFn.Of<Node, Node>(add, one);
            var y = UnstableFn.Of<Node, Node>(add, two);
            Assert.Equal(0, x.CompareTo(UnstableFn.Of<Node, Node>(add, one)));
            Assert.Equal(-Math.Sign(x.CompareTo(y)), Math.Sign(y.CompareTo(x)));
            Assert.NotEqual(0, x.CompareTo(y));
            Assert.Equal(2, Set.Of(x, y, x).Count);

            // Two primitives of one name are two functions.
            graph.Values.RegisterFn<long, long>();
            var p = Primitive.Create<long>("p", [typeof(long)], args => (long)args[0]);
            var q = Primitive.Create<long>("p", [typeof(long)], args => (long)args[0]);
            var fp = UnstableFn.Of<long, long>(p);
            var fq = UnstableFn.Of<long, long>(q);
            Assert.NotEqual(fp, fq);
            Assert.NotEqual(0, fp.CompareTo(fq));
            Assert.Equal(-Math.Sign(fp.CompareTo(fq)), Math.Sign(fq.CompareTo(fp)));
        }

        [Fact]
        public void AVariadicPrimitiveIsATargetAtAnArity()
        {
            // (sort IntVecFn (UnstableFn (i64) (Vec i64))) (check (= (unstable-app (unstable-fn "vec-of" 1) 2) (vec-of 1 2)))
            var graph = new EGraph();
            graph.Values.RegisterVec<long>();
            graph.Values.RegisterFn<long, Vec<long>>();
            var vecOf = Primitives.VecOf<long>();
            Assert.Same(vecOf.WithArity(2), vecOf.WithArity(2));
            var fn = Primitives.UnstableFn<long, Vec<long>>(vecOf.WithArity(2));
            Assert.True(graph.Root.Check(Primitives.UnstableApp<long, Vec<long>>().Of(fn.Of(1L), 2L).Is(Vec.Of(1L, 2L))));
            Assert.Equal("(unstable-fn \"vec-of\" 1)", UnstableFn.Of<long, Vec<long>>(vecOf.WithArity(2), 1L).ToString());
            Assert.Throws<ArgumentException>(() => Primitives.UnstableFn<long, Vec<long>>(vecOf.WithArity(1)).Of(1L));

            // (sort BoolFn (UnstableFn (bool) bool)) (check (= (unstable-app (unstable-fn "or" false) true) true))
            graph.Values.RegisterFn<bool, bool>();
            var or = Primitives.UnstableFn<bool, bool>(BooleanPrimitives.Or.WithArity(2));
            Assert.True(graph.Root.Check(Primitives.UnstableApp<bool, bool>().Of(or.Of(false), true).Is(true)));
            Assert.Throws<ArgumentOutOfRangeException>(() => vecOf.WithArity(-1));
        }

    }

}
