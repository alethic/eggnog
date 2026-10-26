using System;
using System.Collections.Generic;
using System.Globalization;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Merges that are not lattices, egglog's <c>:merge new</c>, <c>:merge old</c> and <c>:merge (f old new)</c>
    /// (<see cref="FunctionMerge"/>): every write folds into the value before it, in order, from host writes, rules and
    /// unions; a merge that throws; and the refusals below the root, where only a lattice merge may write.
    /// </summary>
    public sealed class FunctionMergeTests
    {

        static readonly FunctionMerge<long> Sum = FunctionMerge.Of<long>("sum", (a, b) => checked(a + b));

        [Fact]
        public void NewKeepsTheLastValue()
        {
            var graph = new EGraph();
            var f = graph.DefineFunction<string, long>("f", FunctionMerge.New<long>());
            graph.Root.Assume(f.Of("k").Is(1L));
            graph.Root.Assume(f.Of("k").Is(3L), f.Of("k").Is(2L));

            Assert.Equal(2L, graph.Root.Get(f.Of("k")));
            Assert.Equal("new", ((FunctionHandle)f).Merge!.Name);
        }

        [Fact]
        public void OldKeepsTheFirstValue()
        {
            var graph = new EGraph();
            var f = graph.DefineFunction<string, long>("f", FunctionMerge.Old<long>());
            graph.Root.Assume(f.Of("k").Is(1L));
            var version = graph.Root.Version;
            graph.Root.Assume(f.Of("k").Is(3L), f.Of("k").Is(2L));

            Assert.Equal(1L, graph.Root.Get(f.Of("k")));
            Assert.Equal(version, graph.Root.Version);
        }

        /// <summary>
        /// The merge need be neither idempotent nor commutative: a write of the value already there folds again, and the
        /// writes fold in the order they come, as egglog's <c>set</c> commands do.
        /// </summary>
        [Fact]
        public void HostWritesFoldInOrderEvenWhenTheValueIsTheSame()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var word = graph.DefineFunction<string, string>("word", FunctionMerge.Of<string>("append", (a, b) => a + b));
            graph.Root.Assume(sum.Of("k").Is(2L), sum.Of("k").Is(2L), word.Of("k").Is("a"), word.Of("k").Is("b"));
            graph.Root.Assume(sum.Of("k").Is(2L), word.Of("k").Is("c"));

            Assert.Equal(6L, graph.Root.Get(sum.Of("k")));
            Assert.Equal("abc", graph.Root.Get(word.Of("k")));
        }

        /// <summary>
        /// A read in the batch sees the batch's earlier writes, merged: <c>(set (f k) (+ (f k) 1))</c> after
        /// <c>(set (f k) 2)</c> writes 3, which the sum merges into 5.
        /// </summary>
        [Fact]
        public void AReadInTheBatchSeesTheMergedValue()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            graph.Root.Assume(sum.Of("k").Is(2L), sum.Of("k").Is(Primitives.Add.Of(sum.Of("k"), 1L)));

            Assert.Equal(5L, graph.Root.Get(sum.Of("k")));
        }

        [Fact]
        public void RulesFoldEveryMatchIntoTheValue()
        {
            var t = new NodeTheory();
            var sum = t.Graph.DefineFunction<string, long>("sum", Sum);
            t.Rules.DefineRule("total", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(sum.Of("total").Is(x));
            });
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(6L, t.Root.Get(sum.Of("total")));
        }

        /// <summary>
        /// A union that makes two keys equal merges their values with the merge, as egglog's rebuild does, instead of
        /// the conflict a function without a merge gives.
        /// </summary>
        [Fact]
        public void AUnionMergesTheValuesOfTheKeysItMakesEqual()
        {
            var t = new NodeTheory();
            var sum = t.Graph.DefineFunction<Node, long>("sum", Sum);
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(sum.Of(a).Is(1L), sum.Of(b).Is(2L));

            t.Root.Union(a, b);

            Assert.Equal(3L, t.Root.Get(sum.Of(a)));
            Assert.Equal(1, t.Root.CountRows(sum));
        }

        /// <summary>
        /// A union's rebuild merges the row it keeps, the smaller class's, as the old value with the other row as the
        /// new, as egglog's rebuild does: <c>new</c> keeps the other row's value, and <c>old</c> the kept row's.
        /// </summary>
        [Fact]
        public void AUnionMergesTheKeptRowAsTheOldValue()
        {
            var t = new NodeTheory();
            var newest = t.Graph.DefineFunction<Node, long>("newest", FunctionMerge.New<long>());
            var oldest = t.Graph.DefineFunction<Node, long>("oldest", FunctionMerge.Old<long>());
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(newest.Of(a).Is(1L), newest.Of(b).Is(2L), oldest.Of(a).Is(1L), oldest.Of(b).Is(2L));

            t.Root.Union(b, a);

            Assert.Equal(2L, t.Root.Get(newest.Of(a)));
            Assert.Equal(1L, t.Root.Get(oldest.Of(a)));
        }

        /// <summary>
        /// A write of such a function of classes after the batch merged classes comes after the union's rebuild, as
        /// egglog's next command does: (set (newest (A)) 5) (set (newest (B)) 7) (union (A) (B)) (set (newest (A)) 1)
        /// leaves 1. A write before the merge keeps egglog's order.
        /// </summary>
        [Fact]
        public void ABatchWritesAFunctionOfClassesWithAMergeAfterAMerge()
        {
            var t = new NodeTheory();
            var newest = t.Graph.DefineFunction<Node, long>("newest", FunctionMerge.New<long>());
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(newest.Of(a).Is(5L), newest.Of(b).Is(7L));

            t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(newest.Of(a).Is(1L));
            });

            Assert.True(t.Root.AreEqual(a, t.B.Of()));
            Assert.Equal(1L, t.Root.Get(newest.Of(a)));

            // Written first, the value is merged by the union's rebuild after it, as egglog's next command would: the
            // kept row, 2, is old, and the other, 9, new.
            var c = t.Root.Make(t.Lit.Of(1L));
            var d = t.Root.Make(t.Lit.Of(2L));
            t.Root.Assume(newest.Of(d).Is(9L));
            t.Root.Write(w =>
            {
                w.Assume(newest.Of(c).Is(2L));
                w.Union(c, d);
            });
            Assert.Equal(9L, t.Root.Get(newest.Of(c)));
        }

        [Fact]
        public void AMergeThatThrowsWhileAHostWriteIsCheckedWritesNothingAndDoesNotFault()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            graph.Root.Assume(sum.Of("k").Is(long.MaxValue));
            var version = graph.Root.Version;

            var e = Assert.Throws<RuleEvaluationException>(() => graph.Root.Assume(sum.Of("k").Is(1L)));

            Assert.IsType<OverflowException>(e.InnerException);
            Assert.Contains("merge 'sum' of 'sum'", e.Message);
            Assert.False(graph.Root.IsFaulted);
            Assert.Equal(long.MaxValue, graph.Root.Get(sum.Of("k")));
            Assert.Equal(version, graph.Root.Version);
        }

        [Fact]
        public void AMergeThatThrowsDuringARulesMergeFailsTheRun()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("add one", r => r.Then(sum.Of("k").Is(1L)));
            graph.Root.Assume(sum.Of("k").Is(long.MaxValue));

            var e = Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules));

            Assert.IsType<OverflowException>(e.InnerException);
            Assert.Contains("merge 'sum'", e.Message);
            Assert.False(graph.Root.IsFaulted);
        }

        [Fact]
        public void AChildCannotWriteAFunctionWithAMerge()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            graph.Root.Assume(sum.Of("k").Is(1L));
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<InvalidOperationException>(() => child.Assume(sum.Of("k").Is(2L)));

            Assert.Contains("only the root may write it", e.Message);
            Assert.Equal(1L, child.Get(sum.Of("k")));
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// A child's run of a rule that writes such a function is refused before any work, whether or not the rule would
        /// match, and the child is not faulted.
        /// </summary>
        [Fact]
        public void AChildCannotRunARuleThatWritesAFunctionWithAMerge()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("add one", r => r.Then(sum.Of("k").Is(1L)));
            graph.Root.Assume(sum.Of("k").Is(1L));
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<InvalidOperationException>(() => child.Run(rules));

            Assert.Contains("Rule 'add one' writes 'sum', whose merge 'sum' is not a lattice", e.Message);
            Assert.False(child.IsFaulted);
            Assert.Equal(1L, child.Get(sum.Of("k")));
            graph.Root.Run(rules);
            Assert.Equal(2L, graph.Root.Get(sum.Of("k")));
        }

        [Fact]
        public void ARuleLocalToAChildCannotWriteAFunctionWithAMerge()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var rules = graph.DefineRuleset("rules");
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<RuleDefinitionException>(() => child.DefineRule(rules, "add one", r => r.Then(sum.Of("k").Is(1L))));

            Assert.Contains("a rule local to", e.Message);
            Assert.Empty(child.LocalRules);
            graph.Root.DefineRule(rules, "add one", r => r.Then(sum.Of("k").Is(1L)));
            graph.Root.Run(rules);
            Assert.Equal(1L, graph.Root.Get(sum.Of("k")));
        }

        /// <summary>
        /// A child's union in which a class that keys a row of such a function loses is refused before anything is
        /// staged, since the engine keeps the smaller id and the child's rebuild would rewrite the rows of the larger;
        /// directly, or through congruence. A union that such a class wins rewrites none of its rows, and is not refused.
        /// </summary>
        [Fact]
        public void AChildCannotMergeAwayAClassThatKeysAFunctionWithAMerge()
        {
            var t = new NodeTheory();
            var sum = t.Graph.DefineFunction<Node, long>("sum", Sum);
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            t.Root.Make(t.Wrap.Of(one));
            var wrapped = t.Root.Make(t.Wrap.Of(two));
            var later = t.Root.Make(t.Lit.Of(3L));
            t.Root.Assume(sum.Of(b).Is(2L), sum.Of(wrapped).Is(3L), sum.Of(one).Is(1L));
            var child = t.Root.CreateChild();

            var direct = Assert.Throws<InvalidOperationException>(() => child.Union(a, b));
            var congruent = Assert.Throws<InvalidOperationException>(() => child.Union(one, two));

            Assert.Contains("this merge of classes in", direct.Message);
            Assert.Contains("'sum' has the merge 'sum'", congruent.Message);
            Assert.False(child.IsFaulted);
            Assert.False(child.AreEqual(a, t.B.Of()));
            Assert.Equal(3, child.CountRows(sum));

            Assert.True(child.Union(one, later));
            Assert.Equal(1L, child.Get(sum.Of(later)));
            Assert.Equal(3, child.CountRows(sum));
        }

        /// <summary>
        /// A child's union in which a class that a container of classes holds loses, where a row of such a function is
        /// keyed by the container, by a container holding it, or holds it as its value, is refused before anything is
        /// staged too: the child's container pass would rebuild its copy of the container, or re-stamp the row naming
        /// one that holds it, and so rewrite the row. egglog's push accepts these unions (probe
        /// root-only-merge.egg, which egglog at 5e655cae runs clean), where the library writes such a function only at
        /// the root (decisions row 2). A union that the held class wins, of a class no container holds, rebuilds no
        /// container and is not refused; one whose losing class another container holds is refused when that
        /// container's rebuild merges the row's container away
        /// (<see cref="AChildCannotMergeAwayAClassAVectorHoldsThatItsRebuildMergesIntoARowsVector"/>), and not otherwise
        /// (<see cref="AChildsMergeOfAClassAContainerHoldsIsRefusedOnlyWhereTheContainerMergesARowsAway"/>).
        /// </summary>
        [Theory]
        [InlineData("sum")]
        [InlineData("nest")]
        [InlineData("last")]
        public void AChildCannotMergeAwayAClassAContainerInARowOfAFunctionWithAMergeHolds(string row)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var c = graph.DefineConstructor<Node>("C");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<Vec<Node>>();
            var sum = graph.DefineFunction<Vec<Node>, long>("sum", Sum);
            var nest = graph.DefineFunction<Vec<Vec<Node>>, long>("nest", Sum);
            var last = graph.DefineFunction<Vec<Node>>("last", FunctionMerge.New<Vec<Node>>());
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            var na = root.Make(a.Of());
            root.Assume(row switch
            {
                "sum" => sum.Of(of.Of(b.Of())).Is(2L),
                "nest" => nest.Of(Primitives.VecOf<Vec<Node>>().Of(of.Of(b.Of()))).Is(3L),
                _ => last.Of().Is(of.Of(b.Of())),
            });
            var nb = root.Make(b.Of());
            var nc = root.Make(c.Of());
            root.Freeze();

            using var child = root.CreateChild();
            var refused = Assert.Throws<InvalidOperationException>(() => child.Union(na, nb));
            Assert.Contains($"'{row}' has the merge", refused.Message, StringComparison.Ordinal);
            Assert.Contains("only the root may write it", refused.Message, StringComparison.Ordinal);
            Assert.False(child.IsFaulted);
            Assert.False(child.AreEqual(na, nb));
            Assert.Equal(1, child.CountRows(row switch { "sum" => sum, "nest" => nest, _ => (TableHandle)last }));

            Assert.True(child.Union(nb, nc));
            Assert.False(child.IsFaulted);
            child.Engine.Context.CheckInvariants();
        }

        /// <summary>
        /// A child's union in which the held class wins, but the class that loses is held by another vector, is refused
        /// before anything is staged when a row of such a function names a vector, or holds one, that the other's
        /// rebuild meets, and so is a vec-union of a host write whose union is such a one: the child's container pass
        /// rebuilds that vector to the content of the row's, and keeps its smaller id, so that the row would name the
        /// other or hold a vector rebuilt, which rewrites it. Probe root-only-container-merge.egg, which egglog at
        /// 5e655cae runs clean: (A), (B) and (Num 0), then (set (k) (vec-of (B))), (set (sum (vec-of (A))) 2),
        /// (set (last) (vec-of (A))), (set (nest (vec-of (vec-of (A)))) 3), (set (k5) (vec-of (Num 5))),
        /// (set (last2) (vec-of (Num 0))) and (C); in a push (union (A) (B)) rebuilds (k) to (vec-of (A)), which keeps
        /// the id of (k)'s vector, and rewrites the rows of sum, last and nest, in another (vec-union (k5) (last2))
        /// rewrites last2's, and in a third (union (B) (C)), whose losing class no vector holds, rewrites none; the pops
        /// restore them. The library writes such a function only at the root (decisions row 2).
        /// </summary>
        [Theory]
        [InlineData("union", true)]
        [InlineData("union", false)]
        [InlineData("vec-union", true)]
        [InlineData("vec-union", false)]
        public void AChildCannotMergeAwayAClassAVectorHoldsThatItsRebuildMergesIntoARowsVector(string merge, bool frozen)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var num = graph.DefineConstructor<long, Node>("Num");
            var c = graph.DefineConstructor<Node>("C");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<Vec<Node>>();
            var k = graph.DefineFunction<Vec<Node>>("k");
            var sum = graph.DefineFunction<Vec<Node>, long>("sum", Sum);
            var last = graph.DefineFunction<Vec<Node>>("last", FunctionMerge.New<Vec<Node>>());
            var nest = graph.DefineFunction<Vec<Vec<Node>>, long>("nest", Sum);
            var k5 = graph.DefineFunction<Vec<Node>>("k5");
            var last2 = graph.DefineFunction<Vec<Node>>("last2", FunctionMerge.New<Vec<Node>>());
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            var na = root.Make(a.Of());
            var nb = root.Make(b.Of());
            var zero = root.Make(num.Of(0L));
            root.Assume(k.Of().Is(of.Of(b.Of())), sum.Of(of.Of(a.Of())).Is(2L), last.Of().Is(of.Of(a.Of())));
            root.Assume(nest.Of(Primitives.VecOf<Vec<Node>>().Of(of.Of(a.Of()))).Is(3L));
            root.Assume(k5.Of().Is(of.Of(num.Of(5L))), last2.Of().Is(of.Of(num.Of(0L))));
            var five = root.Make(num.Of(5L));
            var nc = root.Make(c.Of());
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                var refused = Assert.Throws<InvalidOperationException>(() =>
                {
                    if (merge == "union")
                        child.Union(na, nb);
                    else
                        child.Assume(Fact.Exists(Primitives.VecUnion<Node>().Of(k5.Of(), last2.Of())));
                });
                Assert.Contains("only the root may write it", refused.Message, StringComparison.Ordinal);
                Assert.Contains("would rewrite one of its rows", refused.Message, StringComparison.Ordinal);
                Assert.False(child.IsFaulted);
                Assert.False(child.AreEqual(na, nb));
                Assert.False(child.AreEqual(zero, five));
                Assert.Equal(Vec.Of(nb), child.Get(k.Of()));
                Assert.Equal(Vec.Of(five), child.Get(k5.Of()));
                Assert.Equal(2L, child.Get(sum.Of(of.Of(a.Of()))));
                Assert.Equal(new long[] { 1, 1, 1, 1 }, new[] { child.CountRows(sum), child.CountRows(last), child.CountRows(nest), child.CountRows(last2) });

                Assert.True(child.Union(nb, nc));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(na, nb));
            Assert.False(root.AreEqual(nb, nc));
            Assert.Equal(Vec.Of(nb), root.Get(k.Of()));
            Assert.Equal(2L, root.Get(sum.Of(of.Of(a.Of()))));
        }

        /// <summary>
        /// A child's union in which a container holding the class that loses rebuilds to the content of a container that
        /// a row of such a function names, or holds, is refused exactly when the row's container is the one of the two
        /// that loses its id: the other has the smaller id, or, a pass later, a vector holding the other has a smaller id
        /// than the row's. Where the row's container has the smaller id it keeps the id and its content, the other merges
        /// into it, the row stands, and the union goes through, for vectors, sets, maps and vectors of vectors alike.
        /// Probe root-only-smaller-id.egg, which egglog at 5e655cae runs clean: each variant makes (A) and (B), then the
        /// row's container and the other's in either order, the inner vectors first in the last; in a push
        /// (union (A) (B)) rebuilds the other to the row's content and leaves the row's value as it was, and the pop
        /// restores them. egglog's push accepts every one, where the library writes such a function only at the root
        /// (decisions row 2) and refuses the merges that would rewrite its row.
        /// </summary>
        [Theory]
        [InlineData("vec", "row", false)]
        [InlineData("vec", "other", true)]
        [InlineData("set", "row", false)]
        [InlineData("set", "other", true)]
        [InlineData("map", "row", false)]
        [InlineData("map", "other", true)]
        [InlineData("nest", "row", false)]
        [InlineData("nest", "other", true)]
        [InlineData("nest", "inner", true)]
        public void AChildsMergeOfAClassAContainerHoldsIsRefusedOnlyWhereTheContainerMergesARowsAway(string kind, string first, bool refused)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<Vec<Node>>();
            graph.Values.RegisterSet<Node>();
            graph.Values.RegisterMap<Node, long>();
            var of = Primitives.VecOf<Node>();
            var nested = Primitives.VecOf<Vec<Node>>();
            var set = Primitives.SetOf<Node>();
            var empty = Primitives.MapEmpty<Node, long>().Of();
            var insert = Primitives.MapInsert<Node, long>();
            var root = graph.Root;
            var na = root.Make(a.Of());
            var nb = root.Make(b.Of());
            Fact other;
            Fact row;
            Fact rebuilt;
            TableHandle sum;
            switch (kind)
            {
                case "vec":
                {
                    var k = graph.DefineFunction<Vec<Node>>("k");
                    var f = graph.DefineFunction<Vec<Node>, long>("sum", Sum);
                    (other, row, rebuilt, sum) = (k.Of().Is(of.Of(b.Of())), f.Of(of.Of(a.Of())).Is(2L), k.Of().Is(of.Of(a.Of())), f);
                    break;
                }
                case "set":
                {
                    var k = graph.DefineFunction<Set<Node>>("k");
                    var f = graph.DefineFunction<Set<Node>, long>("sum", Sum);
                    (other, row, rebuilt, sum) = (k.Of().Is(set.Of(a.Of(), b.Of())), f.Of(set.Of(a.Of())).Is(2L), k.Of().Is(set.Of(a.Of())), f);
                    break;
                }
                case "map":
                {
                    var k = graph.DefineFunction<Map<Node, long>>("k");
                    var f = graph.DefineFunction<Map<Node, long>, long>("sum", Sum);
                    (other, row, rebuilt, sum) = (k.Of().Is(insert.Of(empty, b.Of(), 1L)), f.Of(insert.Of(empty, a.Of(), 1L)).Is(2L), k.Of().Is(insert.Of(empty, a.Of(), 1L)), f);
                    break;
                }
                default:
                {
                    var k = graph.DefineFunction<Vec<Vec<Node>>>("k");
                    var f = graph.DefineFunction<Vec<Vec<Node>>, long>("sum", Sum);
                    (other, row, rebuilt, sum) = (k.Of().Is(nested.Of(of.Of(b.Of()))), f.Of(nested.Of(of.Of(a.Of()))).Is(3L), k.Of().Is(nested.Of(of.Of(a.Of()))), f);
                    break;
                }
            }

            // The inner vectors first, the row's before the other's, then the other's outer vector before the row's.
            if (first == "inner")
                root.Assume(Fact.Exists(of.Of(a.Of())), Fact.Exists(of.Of(b.Of())));

            if (first == "row")
                root.Assume(row, other);
            else
                root.Assume(other, row);

            root.Freeze();
            using (var child = root.CreateChild())
            {
                if (refused)
                {
                    var e = Assert.Throws<InvalidOperationException>(() => child.Union(na, nb));
                    Assert.Contains("'sum' has the merge 'sum'", e.Message, StringComparison.Ordinal);
                    Assert.Contains("only the root may write it", e.Message, StringComparison.Ordinal);
                    Assert.False(child.IsFaulted);
                    Assert.False(child.AreEqual(na, nb));
                    Assert.True(child.Check(other));
                }
                else
                {
                    Assert.True(child.Union(na, nb));
                    Assert.False(child.IsFaulted);
                    Assert.True(child.Check(rebuilt));
                }

                Assert.True(child.Check(row));
                Assert.Equal(1, child.CountRows(sum));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(na, nb));
            Assert.True(root.Check(other));
            Assert.True(root.Check(row));
        }

        /// <summary>
        /// A child's union in which a class that a vector holds loses goes through where a row of such a function is
        /// keyed by a vector of another sort, though the vectors of every sort share one environment: no vector of the
        /// row's sort holds the classes, so the child's rebuild leaves the row as it is. Probe root-only-other-sort.egg,
        /// which egglog at 5e655cae runs clean: (A) and (B) of one sort, (X) of another, (set (k) (vec-of (B))) and
        /// (set (sum (vec-of (X))) 2); in a push (union (A) (B)) rebuilds (k) to (vec-of (A)) and leaves
        /// (sum (vec-of (X))) at 2, and the pop restores (k).
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildMergesClassesAVectorHoldsWhereARowIsKeyedByAVectorOfAnotherSort(bool frozen)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            graph.DefineSort<NodeList>("Expr");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var x = graph.DefineConstructor<NodeList>("X");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<NodeList>();
            var k = graph.DefineFunction<Vec<Node>>("k");
            var sum = graph.DefineFunction<Vec<NodeList>, long>("sum", Sum);
            var of = Primitives.VecOf<Node>();
            var keyed = sum.Of(Primitives.VecOf<NodeList>().Of(x.Of()));
            var root = graph.Root;
            var na = root.Make(a.Of());
            var nb = root.Make(b.Of());
            root.Assume(k.Of().Is(of.Of(b.Of())), keyed.Is(2L));
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                Assert.True(child.Union(na, nb));
                Assert.False(child.IsFaulted);
                Assert.True(child.Check(k.Of().Is(of.Of(a.Of()))));
                Assert.Equal(2L, child.Get(keyed));
                Assert.Equal(1, child.CountRows(sum));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(na, nb));
            Assert.True(root.Check(k.Of().Is(of.Of(b.Of()))));
        }

        /// <summary>
        /// A child's union in which a class that containers hold loses goes through where their rebuilt contents are none
        /// that a row of such a function names: the row's container keeps its content and its id, and the row stands.
        /// Probe root-only-never-meet.egg, which egglog at 5e655cae runs clean: (A) and (B), then
        /// (set (k) (vec-of (B) (B))), (set (k2) (set-of (B) (C))) and (set (sum (vec-of (A))) 2); in a push
        /// (union (A) (B)) rebuilds (k) to (vec-of (A) (A)) and (k2) to (set-of (A) (C)) and leaves the row at 2, and the
        /// pop restores them.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildMergesAClassContainersHoldWhoseRebuildsMeetNoRowOfAFunctionWithAMerge(bool frozen)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var c = graph.DefineConstructor<Node>("C");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterSet<Node>();
            var k = graph.DefineFunction<Vec<Node>>("k");
            var k2 = graph.DefineFunction<Set<Node>>("k2");
            var sum = graph.DefineFunction<Vec<Node>, long>("sum", Sum);
            var of = Primitives.VecOf<Node>();
            var set = Primitives.SetOf<Node>();
            var root = graph.Root;
            var na = root.Make(a.Of());
            var nb = root.Make(b.Of());
            root.Assume(k.Of().Is(of.Of(b.Of(), b.Of())), k2.Of().Is(set.Of(b.Of(), c.Of())), sum.Of(of.Of(a.Of())).Is(2L));
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                Assert.True(child.Union(na, nb));
                Assert.False(child.IsFaulted);
                Assert.True(child.Check(k.Of().Is(of.Of(a.Of(), a.Of())), k2.Of().Is(set.Of(a.Of(), c.Of()))));
                Assert.Equal(2L, child.Get(sum.Of(of.Of(a.Of()))));
                Assert.Equal(1, child.CountRows(sum));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(na, nb));
            Assert.True(root.Check(k.Of().Is(of.Of(b.Of(), b.Of())), k2.Of().Is(set.Of(b.Of(), c.Of()))));
        }

        /// <summary>
        /// A pair of a value and a class and a pair of a class and a value are never one container, though they share an
        /// environment and may hold the same cells: the engine tells them apart by the part it rebuilds. So a child's
        /// union whose losing class the first holds goes through where a row of such a function is keyed by a pair of
        /// the other layout whose cells the first one's rebuild would give, were it read as one of the row's: here
        /// (pair 0 (Num 5)), 0 being the cell of (Num 0), and (pair (Num 0) 5), 5 being that of (Num 5), interned in
        /// that order, and (union (Num 5) (Num 4)). Probe root-only-pair-layout.egg, which egglog at 5e655cae runs clean:
        /// in a push the union rebuilds (hp 0) to (pair 0 (Num 4)) and leaves (rp (pair (Num 0) 5)) at 1, and the pop
        /// restores (hp 0); egglog's cells need not be the library's, and it accepts the union whatever they are.
        /// </summary>
        [Fact]
        public void AChildMergesAClassAPairHoldsThoughItsCellsAreThoseOfARowsPairOfTheOtherLayout()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var valueFirst = graph.Values.RegisterPair<long, Node>();
            var classFirst = graph.Values.RegisterPair<Node, long>();
            var hp = graph.DefineFunction<long, Pair<long, Node>>("hp");
            var rp = graph.DefineFunction<Pair<Node, long>, long>("rp", Sum);
            var root = graph.Root;
            var nodes = new Node[6];
            for (var i = 0; i < nodes.Length; i++)
                nodes[i] = root.Make(num.Of((long)i));

            var zero = (long)ClassChecks.Require(root, nodes[0].Id, "zero");
            var five = (long)ClassChecks.Require(root, nodes[5].Id, "five");
            root.Assume(hp.Of(0L).Is(Primitives.PairOf<long, Node>().Of(zero, num.Of(5L))));
            var keyed = rp.Of(Primitives.PairOf<Node, long>().Of(num.Of(0L), five));
            root.Assume(keyed.Is(1L));

            // The two pairs hold the same cells, and the one of a value first has the smaller id.
            Assert.True(valueFirst.TryEncodeExisting(root.Get(hp.Of(0L)), root.ContainerView, out var held));
            Assert.True(classFirst.TryEncodeExisting(new Pair<Node, long>(nodes[0], five), root.ContainerView, out var named));
            Assert.True(held < named);
            Assert.Equal(((IContainerColumnType)valueFirst).Content(held, root.ContainerView), ((IContainerColumnType)classFirst).Content(named, root.ContainerView));
            root.Freeze();

            using (var child = root.CreateChild())
            {
                Assert.True(child.Union(nodes[5], nodes[4]));
                Assert.False(child.IsFaulted);
                Assert.True(child.Check(hp.Of(0L).Is(Primitives.PairOf<long, Node>().Of(zero, num.Of(4L)))));
                Assert.Equal(1L, child.Get(keyed));
                Assert.Equal(1, child.CountRows(rp));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(nodes[5], nodes[4]));
        }

        /// <summary>
        /// Pairs of one layout are one container whatever their elements' types, and so are maps: a pair of a class and an
        /// <c>i32</c> holds the cells of one of a class and an <c>i64</c>, and a map of a class to a <c>bool</c> those of
        /// one of a class to an <c>i64</c>. So a child's union in which a holder of the class that loses rebuilds to the
        /// content of a pair, or a map, that a row of such a function names is refused where the holder has the smaller
        /// id, though another type of the layout made the row's container, the key of a constructor's term or of a plain
        /// function's row: the child's container pass merges the row's container into the holder and rewrites the row.
        /// Probe root-only-same-layout.egg, which egglog at 5e655cae runs clean: two pair sorts of one layout,
        /// (set (hp 0) (pair (Num 1) 1)), (KP (pair (Num 0) 1)) of the other sort and (set (rp (pair (Num 0) 1)) 1); in a
        /// push (union (Num 0) (Num 1)) rebuilds (hp 0) to the row's pair and the row is 1 under it; the same with a plain
        /// function's row in place of KP's term, and with two map sorts; the pops restore them. The library writes such a
        /// function only at the root (decisions row 2).
        /// </summary>
        [Theory]
        [InlineData("constructor", true)]
        [InlineData("constructor", false)]
        [InlineData("function", true)]
        [InlineData("function", false)]
        [InlineData("map", true)]
        [InlineData("map", false)]
        public void AChildCannotMergeAwayARowsContainerThatAnotherTypeOfItsLayoutMade(string alias, bool frozen)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var pairs = graph.Values.RegisterPair<Node, long>();
            var narrowPairs = graph.Values.RegisterPair<Node, int>();
            var maps = graph.Values.RegisterMap<Node, long>();
            var flagMaps = graph.Values.RegisterMap<Node, bool>();

            // The rows of such functions before the other types' rows in the check.
            var rp = graph.DefineFunction<Pair<Node, long>, long>("rp", Sum);
            var rm = graph.DefineFunction<Map<Node, long>, long>("rm", Sum);
            var kpi = graph.DefineFunction<Pair<Node, int>, long>("kpi");
            var kmb = graph.DefineFunction<Map<Node, bool>, long>("kmb");
            var kp = graph.DefineConstructor<Pair<Node, int>, Node>("KP");
            var hp = graph.DefineFunction<long, Pair<Node, long>>("hp");
            var hm = graph.DefineFunction<long, Map<Node, long>>("hm");
            var pair = Primitives.PairOf<Node, long>();
            var narrowPair = Primitives.PairOf<Node, int>();
            var empty = Primitives.MapEmpty<Node, long>().Of();
            var insert = Primitives.MapInsert<Node, long>();
            var root = graph.Root;
            var a = root.Make(num.Of(0L));
            var b = root.Make(num.Of(1L));
            TableHandle row;
            Fact keyed;
            uint held, named, made;
            if (alias == "map")
            {
                root.Assume(hm.Of(0L).Is(insert.Of(empty, num.Of(1L), 1L)));
                root.Assume(kmb.Of(Primitives.MapInsert<Node, bool>().Of(Primitives.MapEmpty<Node, bool>().Of(), num.Of(0L), true)).Is(0L));
                (row, keyed) = (rm, rm.Of(insert.Of(empty, num.Of(0L), 1L)).Is(1L));
                root.Assume(keyed);
                Assert.True(maps.TryEncodeExisting(Map.Of((b, 1L)), root.ContainerView, out held));
                Assert.True(maps.TryEncodeExisting(Map.Of((a, 1L)), root.ContainerView, out named));
                Assert.True(flagMaps.TryEncodeExisting(Map.Of((a, true)), root.ContainerView, out made));
            }
            else
            {
                root.Assume(hp.Of(0L).Is(pair.Of(num.Of(1L), 1L)));
                if (alias == "constructor")
                    root.Make(kp.Of(narrowPair.Of(num.Of(0L), 1)));
                else
                    root.Assume(kpi.Of(narrowPair.Of(num.Of(0L), 1)).Is(0L));

                (row, keyed) = (rp, rp.Of(pair.Of(num.Of(0L), 1L)).Is(1L));
                root.Assume(keyed);
                Assert.True(pairs.TryEncodeExisting(new Pair<Node, long>(b, 1L), root.ContainerView, out held));
                Assert.True(pairs.TryEncodeExisting(new Pair<Node, long>(a, 1L), root.ContainerView, out named));
                Assert.True(narrowPairs.TryEncodeExisting(new Pair<Node, int>(a, 1), root.ContainerView, out made));
            }

            // The other type made the row's container, and the holder's id is the smaller.
            Assert.Equal(made, named);
            Assert.True(held < named);
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                var refused = Assert.Throws<InvalidOperationException>(() => child.Union(a, b));
                Assert.Contains($"'{row.Name}' has the merge 'sum'", refused.Message, StringComparison.Ordinal);
                Assert.Contains("only the root may write it", refused.Message, StringComparison.Ordinal);
                Assert.False(child.IsFaulted);
                Assert.False(child.AreEqual(a, b));
                Assert.True(child.Check(keyed));
                Assert.Equal(1, child.CountRows(row));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(a, b));
            Assert.True(root.Check(keyed));
        }

        /// <summary>
        /// A child's union of two classes that maps hold, each map the key of a row of a constructor, makes the maps one,
        /// so the constructor's rows meet in the rebuild's next pass and their classes become one, the larger losing.
        /// Where a row of such a function names the class that loses, as its key or inside the vector it is keyed by, the
        /// union is refused before anything is staged, as a merge the batch's closure derives is: only the replay of the
        /// passes finds this one, since a map whose keys collapse rebuilds by their order. Where the row names the class
        /// that wins, the union goes through and the row stands. Probe root-only-map-congruence.egg, which egglog at
        /// 5e655cae runs clean: (Num 1), (Num 2), $k1 = (K (map-insert (map-empty) (Num 1) 0)), $k2 the same of (Num 2),
        /// (set (f (vec-of $k2)) 5) and (set (f2 $k2) 7), both merging by +; in a push (union (Num 1) (Num 2)) makes $k1
        /// and $k2 one, K keeps one row and both rows are rewritten; after the pop K has two and the rows are as they
        /// were. The library writes such a function only at the root (decisions row 2).
        /// </summary>
        [Theory]
        [InlineData("vec", true)]
        [InlineData("vec", false)]
        [InlineData("class", true)]
        [InlineData("class", false)]
        [InlineData("winner", true)]
        [InlineData("winner", false)]
        public void AChildCannotMakeRowsOfAConstructorKeyedByMapsMeetWhereARowOfAFunctionWithAMergeNamesTheClassThatLoses(string row, bool frozen)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterMap<Node, long>();
            graph.Values.RegisterVec<Node>();
            var k = graph.DefineConstructor<Map<Node, long>, Node>("K");
            var f = graph.DefineFunction<Vec<Node>, long>("f", Sum);
            var f2 = graph.DefineFunction<Node, long>("f2", Sum);
            var empty = Primitives.MapEmpty<Node, long>().Of();
            var insert = Primitives.MapInsert<Node, long>();
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            var k1 = root.Make(k.Of(insert.Of(empty, num.Of(1L), 0L)));
            var k2 = root.Make(k.Of(insert.Of(empty, num.Of(2L), 0L)));
            root.Assume(row switch
            {
                "vec" => f.Of(Primitives.VecOf<Node>().Of(k.Of(insert.Of(empty, num.Of(2L), 0L)))).Is(5L),
                "class" => f2.Of(k.Of(insert.Of(empty, num.Of(2L), 0L))).Is(7L),
                _ => f2.Of(k.Of(insert.Of(empty, num.Of(1L), 0L))).Is(7L),
            });
            var named = row == "vec" ? (TableHandle)f : f2;
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                if (row == "winner")
                {
                    Assert.True(child.Union(one, two));
                    Assert.False(child.IsFaulted);
                    Assert.True(child.AreEqual(k1, k2));
                    Assert.Equal(1, child.CountRows(k));
                    Assert.Equal(7L, child.Get(f2.Of(k.Of(insert.Of(empty, num.Of(2L), 0L)))));
                }
                else
                {
                    var refused = Assert.Throws<InvalidOperationException>(() => child.Union(one, two));
                    Assert.Contains($"'{named.Name}' has the merge", refused.Message, StringComparison.Ordinal);
                    Assert.Contains("only the root may write it", refused.Message, StringComparison.Ordinal);
                    Assert.False(child.IsFaulted);
                    Assert.False(child.AreEqual(one, two));
                    Assert.False(child.AreEqual(k1, k2));
                    Assert.Equal(2, child.CountRows(k));
                }

                Assert.Equal(1, child.CountRows(named));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(k1, k2));
            Assert.Equal(2, root.CountRows(k));
            Assert.Equal(1, root.CountRows(named));
        }

        /// <summary>
        /// A merge only the replay of the passes finds makes a class lose that a vector no row of the check holds holds: a
        /// child's union of two classes that maps hold, each map the key of a row of a constructor, makes the rows meet a
        /// pass later and their classes one, and a pass after that a vector of the class that loses rebuilds to the vector
        /// a row of such a function names. Where that vector was made first, its id is the smaller, the row's vector loses
        /// its id and the row would be rewritten, so the union is refused before anything is staged; where the row's was,
        /// the other merges into it, the row stands, and the union goes through. No class that loses in the batch's
        /// closure is held by a vector, so only the replay's own holders find the other vector. Probe
        /// root-only-map-congruence-holder.egg, which egglog at 5e655cae runs clean: (Num 1), (Num 2),
        /// $k1 = (K (map-insert (map-empty) (Num 1) 0)), $k2 the same of (Num 2), (set (hv 0) (vec-of $k2)) and
        /// (set (rv (vec-of $k1)) 1), rv merging by +; in a push (union (Num 1) (Num 2)) makes $k1 and $k2 one and
        /// (hv 0) (vec-of $k1), and rv is 1 under it; the same with the row's vector made first; the pops restore them.
        /// The library writes such a function only at the root (decisions row 2).
        /// </summary>
        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void AChildsMergeOnlyTheReplayFindsIsRefusedWhereAVectorOfTheClassThatLosesMergesARowsVectorAway(bool holderFirst, bool frozen)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterMap<Node, long>();
            graph.Values.RegisterVec<Node>();
            var k = graph.DefineConstructor<Map<Node, long>, Node>("K");
            var hv = graph.DefineFunction<long, Vec<Node>>("hv");
            var rv = graph.DefineFunction<Vec<Node>, long>("rv", Sum);
            var empty = Primitives.MapEmpty<Node, long>().Of();
            var insert = Primitives.MapInsert<Node, long>();
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            var k1 = root.Make(k.Of(insert.Of(empty, num.Of(1L), 0L)));
            var k2 = root.Make(k.Of(insert.Of(empty, num.Of(2L), 0L)));
            var holder = hv.Of(0L).Is(of.Of(k.Of(insert.Of(empty, num.Of(2L), 0L))));
            var keyed = rv.Of(of.Of(k.Of(insert.Of(empty, num.Of(1L), 0L)))).Is(1L);
            if (holderFirst)
                root.Assume(holder, keyed);
            else
                root.Assume(keyed, holder);

            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                if (holderFirst)
                {
                    var refused = Assert.Throws<InvalidOperationException>(() => child.Union(one, two));
                    Assert.Contains("'rv' has the merge 'sum'", refused.Message, StringComparison.Ordinal);
                    Assert.Contains("only the root may write it", refused.Message, StringComparison.Ordinal);
                    Assert.False(child.IsFaulted);
                    Assert.False(child.AreEqual(one, two));
                    Assert.False(child.AreEqual(k1, k2));
                    Assert.True(child.Check(holder));
                }
                else
                {
                    Assert.True(child.Union(one, two));
                    Assert.False(child.IsFaulted);
                    Assert.True(child.AreEqual(k1, k2));
                    Assert.True(child.Check(hv.Of(0L).Is(of.Of(k.Of(insert.Of(empty, num.Of(1L), 0L))))));
                }

                Assert.True(child.Check(keyed));
                Assert.Equal(1, child.CountRows(rv));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(k1, k2));
            Assert.True(root.Check(holder));
            Assert.True(root.Check(keyed));
        }

        /// <summary>
        /// Over random programs (<see cref="RootOnlyMergeProgram"/>), a host merge below the root is refused exactly when
        /// the engine, given the same unions, would write a row of such a function in the scope's rebuild, which it
        /// refuses by faulting the scope: each merge of a child's two batches and a grandchild's, a union, a constructor
        /// write or a vec-union, whatever the containers, their ids and the rows' keys and values, is checked against the
        /// engine's own verdict, the merge's unions staged directly on the engine of a new scope that the merges before it
        /// the library kept brought to the same state, so that no check of the library's stands between them. egglog
        /// accepts every one of these merges in a push; the library refuses those decisions row 2 requires and no others.
        /// A writable root's write between the child's batches that the child's catch-up would have to rewrite a row of
        /// such a function for faults the child at its next call, which only the engine refuses (architecture 2.3); the
        /// program's later batches are then left out. 30 seeds, each with a frozen and a writable root, or more where
        /// <c>EGGNOG_SEEDS</c> asks for more. At most three disagreements are reported.
        /// </summary>
        [Fact]
        public void AHostMergeBelowTheRootIsRefusedExactlyWhenItsRebuildWouldWriteARowOfAFunctionWithAMerge()
        {
            var failures = new List<string>();
            var verdicts = new HashSet<(int Kind, int Batch, bool Refused)>();
            for (var seed = 0; seed < Seeds() && failures.Count < 3; seed++)
            {
                foreach (var frozen in new[] { true, false })
                {
                    var program = new RootOnlyMergeProgram(seed, frozen);
                    var kept = Verdicts(program, out var fault);
                    if (fault is not null)
                        failures.Add($"seed {seed}, {(frozen ? "frozen" : "writable")} root, {fault}; program: {program.Description}");

                    for (var batch = 0; batch < kept.Count; batch++)
                    {
                        var before = new List<int>();
                        for (var i = 0; i < kept[batch].Count; i++)
                        {
                            if (kept[batch][i] is not bool accepted)
                                continue;

                            var merge = program.Batches[batch][i];
                            var faults = EngineFaults(program.WritesBetween ? new RootOnlyMergeProgram(seed, frozen) : program, kept, batch, [.. before, i]);
                            verdicts.Add((merge.Kind, batch, accepted == false));
                            if (faults == accepted)
                                failures.Add($"seed {seed}, {(frozen ? "frozen" : "writable")} root, batch {batch}, merge {i} {merge}: the library {(accepted ? "accepted" : "refused")} it, and the engine {(faults ? "faults" : "does not fault")} the scope; program: {program.Description}");

                            if (accepted)
                                before.Add(i);
                        }
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));

            // Every kind of merge, and every batch, has merges both accepted and refused.
            foreach (var refused in new[] { false, true })
            {
                for (var kind = 0; kind < 3; kind++)
                    Assert.Contains(verdicts, v => v.Kind == kind && v.Refused == refused);

                for (var batch = 0; batch < 3; batch++)
                    Assert.Contains(verdicts, v => v.Batch == batch && v.Refused == refused);
            }
        }

        /// <summary>
        /// Runs the program's batches through the library, each merge a call of its own in one batch: whether the library
        /// accepted each (<c>true</c>), refused it as one that would rewrite a row of such a function (<c>false</c>), or
        /// failed it otherwise, a vec-union of two lengths having no result (<c>null</c>). A batch whose flush the engine
        /// refused, faulting the scope, is a disagreement, described in <paramref name="fault"/>, and ends the run.
        /// </summary>
        static List<List<bool?>> Verdicts(RootOnlyMergeProgram program, out string? fault)
        {
            fault = null;
            var verdicts = new List<List<bool?>>();
            var scopes = new List<Scope>();
            try
            {
                for (var batch = 0; batch < program.Batches.Count; batch++)
                {
                    var scope = program.ScopeOf(scopes, batch);
                    var kept = new List<bool?>();
                    try
                    {
                        scope.Write(w =>
                        {
                            foreach (var merge in program.Batches[batch])
                            {
                                try
                                {
                                    program.Write(w, merge);
                                    kept.Add(true);
                                }
                                catch (InvalidOperationException e) when (e.Message.Contains("only the root may write it", StringComparison.Ordinal))
                                {
                                    kept.Add(false);
                                }
                                catch (RuleEvaluationException e) when (merge.Kind == 2 && e.Message.Contains("has no result", StringComparison.Ordinal))
                                {
                                    kept.Add(null);
                                }
                            }
                        });
                    }
                    catch (InvalidOperationException e) when (batch == 1 && program.WritesBetween && kept.Count == 0 && e.Message.Contains("not a lattice merge", StringComparison.Ordinal))
                    {
                        // The child's catch-up with the root's writes, before the batch planned anything.
                        Assert.True(scope.IsFaulted);
                        break;
                    }
                    catch (InvalidOperationException e) when (kept.Count > 0 && e.Message.Contains("not a lattice merge", StringComparison.Ordinal))
                    {
                        fault = $"batch {batch}: the library accepted merges ({string.Join(", ", kept)}) whose flush the engine refused: {e.Message}";
                        break;
                    }

                    Assert.False(scope.IsFaulted);
                    verdicts.Add(kept);
                }
            }
            finally
            {
                for (var i = scopes.Count - 1; i >= 0; i--)
                    scopes[i].Dispose();
            }

            return verdicts;
        }

        /// <summary>
        /// Gets whether the engine faults the scope of batch <paramref name="batch"/> when its rebuild meets the unions of
        /// <paramref name="merges"/>, staged on the scope's engine, after the batches before it, made through the library
        /// with the merges it kept: in new scopes under the program's root, which the scopes before them left as it was,
        /// unless the root writes between the batches, when the program is a new build.
        /// </summary>
        static bool EngineFaults(RootOnlyMergeProgram program, List<List<bool?>> verdicts, int batch, List<int> merges)
        {
            var scopes = new List<Scope>();
            try
            {
                for (var before = 0; before < batch; before++)
                {
                    var merged = program.Batches[before];
                    var kept = verdicts[before];
                    program.ScopeOf(scopes, before).Write(w =>
                    {
                        for (var i = 0; i < merged.Length; i++)
                            if (kept[i] == true)
                                program.Write(w, merged[i]);
                    });
                }

                var scope = program.ScopeOf(scopes, batch);

                var unions = new List<(uint, uint)>();
                foreach (var i in merges)
                    unions.AddRange(program.Unions(scope, program.Batches[batch][i]));

                scope.EnterWrite();
                try
                {
                    foreach (var (x, y) in unions)
                        scope.Engine.StageUnion(x, y);

                    scope.Engine.Flush();
                    return false;
                }
                catch (InvalidOperationException e) when (e.Message.Contains("not a lattice merge", StringComparison.Ordinal))
                {
                    Assert.True(scope.Engine.IsFaulted);
                    return true;
                }
                finally
                {
                    scope.Exit(true);
                }
            }
            finally
            {
                for (var i = scopes.Count - 1; i >= 0; i--)
                    scopes[i].Dispose();
            }
        }

        /// <summary>
        /// The number of seeds: 30, or <c>EGGNOG_SEEDS</c> where it is more, since the test's coverage of accepted and
        /// refused merges needs that many programs.
        /// </summary>
        static int Seeds() =>
            int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_SEEDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeds) && seeds > 30
                ? seeds
                : 30;

        /// <summary>
        /// A root write keyed by a class that a live child has merged away needs the child's catch-up to rewrite the row,
        /// which the engine refuses: the child is faulted at its next call, and the root is not.
        /// </summary>
        [Fact]
        public void ARootWriteKeyedByAClassAChildMergedAwayFaultsTheChild()
        {
            var t = new NodeTheory();
            var sum = t.Graph.DefineFunction<Node, long>("sum", Sum);
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var child = t.Root.CreateChild();
            child.Union(a, b);

            t.Root.Assume(sum.Of(b).Is(1L));
            var e = Assert.Throws<InvalidOperationException>(() => child.CountRows(sum));

            Assert.Contains("Table sum has a merge that is not a lattice merge", e.Message);
            Assert.True(child.IsFaulted);
            Assert.False(t.Root.IsFaulted);
            Assert.Equal(1L, t.Root.Get(sum.Of(b)));
        }

        [Fact]
        public void ASplitCannotAssumeAFunctionWithAMerge()
        {
            var graph = new EGraph();
            var choice = graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var sum = graph.DefineFunction<string, long>("sum", Sum);

            var e = Assert.Throws<RuleDefinitionException>(() => graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                var v = s.Cases("v", d);
                s.Assume(sum.Of(k).Is(v));
            }));

            Assert.Contains("a case is a child", e.Message);
        }

        /// <summary>
        /// A root run whose split runs a rule that writes such a function in its cases is refused before any work; the
        /// same rule may run at the root outside the split.
        /// </summary>
        [Fact]
        public void ARootRunCannotRunARuleThatWritesAFunctionWithAMergeInASplitsCases()
        {
            var graph = new EGraph();
            var choice = graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var split = graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)));
            });
            var adds = graph.DefineRuleset("adds");
            adds.DefineRule("add one", r => r.Then(sum.Of("k").Is(1L)));
            var none = graph.DefineRuleset("none");
            graph.Root.Assume(choice.Of("k").Is(Set.Of(1L, 2L)));

            var e = Assert.Throws<InvalidOperationException>(() => graph.Root.Run(Schedule.Split(split, Schedule.Run(adds))));

            Assert.Contains("the cases of a split are children", e.Message);
            Assert.False(graph.Root.IsFaulted);
            Assert.False(graph.Root.TryGet(sum.Of("k"), out _));
            graph.Root.Run(Schedule.Sequence(Schedule.Run(adds), Schedule.Split(split, Schedule.Run(none))));
            Assert.Equal(1L, graph.Root.Get(sum.Of("k")));
        }

        /// <summary>
        /// A rule's union below the root that would rewrite a row of such a function depends on the data, so the run
        /// cannot refuse it before any work: the engine refuses the rewrite, and the run fails with its exception. Here a
        /// split's case does it, and the root is neither faulted nor changed.
        /// </summary>
        [Fact]
        public void AUnionInACaseThatWouldRewriteARowOfAFunctionWithAMergeFailsTheRun()
        {
            var t = new NodeTheory();
            var choice = t.Graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var sum = t.Graph.DefineFunction<Node, long>("sum", Sum);
            var split = t.Graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)));
            });
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(sum.Of(a).Is(1L), sum.Of(b).Is(2L), choice.Of("k").Is(Set.Of(1L, 2L)));
            t.Rules.DefineRule("join", r =>
            {
                r.When(choice.Of("k").Is(Set.Of(1L)));
                r.Then(t.A.Of().Is(t.B.Of()));
            });

            var e = Assert.Throws<InvalidOperationException>(() => t.Root.Run(Schedule.Split(split, Schedule.Run(t.Rules))));

            Assert.Contains("Table sum has a merge that is not a lattice merge", e.Message);
            Assert.False(t.Root.IsFaulted);
            Assert.Equal(2, t.Root.CountRows(sum));
            Assert.Empty(t.Root.Children);
        }

        [Fact]
        public void ARuleCannotReadAFunctionWithAMergeInAnotherScopeButAHostReadCan()
        {
            var graph = new EGraph();
            var outer = graph.DefineScopeTag("outer");
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var copy = graph.DefineFunction<string, long>("copy", ValueLattice.Max<long>());
            var rules = graph.DefineRuleset("rules");

            var e = Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("copy", r =>
            {
                var (k, v) = r.Vars<string, long>("k", "v");
                r.When(v.Is(sum.In(outer).Of(k)));
                r.Then(copy.Of(k).Is(v));
            }));

            Assert.Contains("'sum' has the merge 'sum', which is not a lattice", e.Message);

            // A host read reads the scope itself, with no mirror.
            graph.Root.Assume(sum.Of("k").Is(4L));
            Assert.Equal(4L, graph.Root.CreateChild().Get(sum.In(graph.RootTag).Of("k")));
        }

        [Fact]
        public void TheMergeMustNotBeADefaultValue()
        {
            var graph = new EGraph();

            Assert.Throws<ArgumentException>(() => graph.DefineFunction<string, long>("f", default(FunctionMerge<long>)));
            Assert.Throws<ArgumentNullException>(() => FunctionMerge.Of<long>("m", null!));
        }

    }

}
