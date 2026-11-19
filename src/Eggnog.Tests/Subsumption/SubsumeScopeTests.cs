using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Subsumption
{

    /// <summary>
    /// Subsumption in scopes. A subsumption is a sticky change of the row's subsumption cell, written in the scope that
    /// makes it: a child's shadows its ancestors' row, so the child and its descendants stop matching it while the
    /// ancestors and the siblings still do, and an ancestor's reaches its descendants when they catch up.
    /// </summary>
    public sealed class SubsumeScopeTests
    {

        [Fact]
        public void AChildsSubsumptionLeavesItsParentAndSiblingsMatchingTheRow()
        {
            var t = new MathTheory();
            t.DefineSee();
            t.Root.Make(t.Num.Of(1L));
            t.Root.Make(t.Num.Of(2L));
            var child = t.Root.CreateChild();
            var sibling = t.Root.CreateChild();

            child.Subsume(t.Num.Of(1L));
            child.Run(Schedule.Saturate(t.Rules));
            sibling.Run(Schedule.Saturate(t.Rules));

            Assert.False(child.Check(t.Seen.Of(1L)));
            Assert.True(child.Check(t.Seen.Of(2L)));
            Assert.True(child.Check(Fact.Exists(t.Num.Of(1L))));
            Assert.True(sibling.Check(t.Seen.Of(1L), t.Seen.Of(2L)));
            Assert.Equal(new[] { true, false }, child.Rows(t.Num).Select(row => row.Subsumed));
            Assert.Equal(new[] { false, false }, sibling.Rows(t.Num).Select(row => row.Subsumed));

            // The parent still matches the row, and a grandchild inherits the child's subsumption.
            var grandchild = child.CreateChild();
            grandchild.Make(t.Num.Of(3L));
            grandchild.Run(Schedule.Saturate(t.Rules));
            Assert.True(grandchild.Check(t.Seen.Of(3L)));
            Assert.False(grandchild.Check(t.Seen.Of(1L)));
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(t.Seen.Of(1L), t.Seen.Of(2L)));
            Assert.All(t.Root.Rows(t.Num), row => Assert.False(row.Subsumed));
        }

        [Fact]
        public void AChildsRuleSubsumesOnlyInTheChild()
        {
            var t = new MathTheory();
            t.DefineSee();
            var prune = t.Graph.DefineRuleset("prune");
            prune.DefineRule("times three", r =>
            {
                var x = r.Var<Node>("x");
                r.Rewrite(t.Mul.Of(t.Num.Of(3L), x), t.Add.Of(x, t.Add.Of(x, x)), subsume: true);
            });
            var product = t.Root.Make(t.Mul.Of(t.Num.Of(3L), t.Var.Of("y")));
            var child = t.Root.CreateChild();
            var sibling = t.Root.CreateChild();

            child.Run(Schedule.Saturate(prune));

            Assert.Equal("(Add (Var \"y\") (Add (Var \"y\") (Var \"y\")))", child.Extract(product).ToString());
            Assert.Equal("(Mul (Num 3) (Var \"y\"))", sibling.Extract(product).ToString());
            Assert.Equal("(Mul (Num 3) (Var \"y\"))", t.Root.Extract(product).ToString());
            Assert.True(Assert.Single(child.Rows(t.Mul)).Subsumed);
            Assert.False(Assert.Single(t.Root.Rows(t.Mul)).Subsumed);

            // The sibling runs the same rule on its own: the product is subsumed there too, independently.
            sibling.Run(Schedule.Saturate(prune));
            Assert.True(Assert.Single(sibling.Rows(t.Mul)).Subsumed);
            Assert.False(Assert.Single(t.Root.Rows(t.Mul)).Subsumed);
        }

        [Fact]
        public void AParentsSubsumptionReachesItsChildOnCatchUp()
        {
            var t = new MathTheory();
            t.DefineSee();
            t.Root.Make(t.Num.Of(1L));
            t.Root.Make(t.Num.Of(2L));
            var child = t.Root.CreateChild();
            child.Make(t.Num.Of(3L));

            t.Root.Subsume(t.Num.Of(1L));
            t.Root.Subsume(t.Num.Of(3L));
            child.Run(Schedule.Saturate(t.Rules));

            // The parent's subsumption of its own row reaches the child. The parent subsumes (Num 3) too, which it did not
            // hold, so it makes the term subsumed; the child's catch-up folds that row into its own row of the term, and
            // subsumption is sticky, so the child sees it subsumed as well.
            Assert.False(child.Check(t.Seen.Of(1L)));
            Assert.False(child.Check(t.Seen.Of(3L)));
            Assert.True(child.Check(t.Seen.Of(2L)));
            Assert.Equal(new[] { true, false, true }, child.Rows(t.Num).Select(row => row.Subsumed));
            Assert.Equal(new[] { true, false, true }, t.Root.Rows(t.Num).Select(row => row.Subsumed));
        }

        [Fact]
        public void ASubsumptionDoesNotRetractWhatWasDerivedBeforeIt()
        {
            // As in egglog, subsuming a row stops later matches; what earlier matches derived stays, in the scope and in
            // the children that inherit it.
            var t = new MathTheory();
            t.DefineSee();
            t.Root.Make(t.Num.Of(1L));
            t.Root.Run(Schedule.Saturate(t.Rules));
            var child = t.Root.CreateChild();

            child.Subsume(t.Num.Of(1L));
            child.Make(t.Num.Of(2L));
            child.Run(Schedule.Saturate(t.Rules));

            Assert.True(child.Check(t.Seen.Of(1L), t.Seen.Of(2L)));
            Assert.True(t.Root.Check(t.Seen.Of(1L)));
        }

        [Fact]
        public void AFrozenScopeIsNotSubsumedAndItsChildrenSubsumeIndependently()
        {
            var t = new MathTheory();
            t.DefineSee();
            t.Root.Make(t.Num.Of(1L));
            t.Root.Make(t.Num.Of(2L));
            t.Root.Freeze();

            Assert.Throws<InvalidOperationException>(() => t.Root.Subsume(t.Num.Of(1L)));
            Assert.Throws<InvalidOperationException>(() => t.Root.Write(w => w.Subsume(t.Num.Of(1L))));

            var first = t.Root.CreateChild();
            var second = t.Root.CreateChild();
            first.Subsume(t.Num.Of(1L));
            second.Subsume(t.Num.Of(2L));
            first.Run(Schedule.Saturate(t.Rules));
            second.Run(Schedule.Saturate(t.Rules));

            Assert.True(first.Check(t.Seen.Of(2L)));
            Assert.False(first.Check(t.Seen.Of(1L)));
            Assert.True(second.Check(t.Seen.Of(1L)));
            Assert.False(second.Check(t.Seen.Of(2L)));
            Assert.All(t.Root.Rows(t.Num), row => Assert.False(row.Subsumed));

            // A frozen child keeps its subsumption for its own children.
            first.Freeze();
            var grandchild = first.CreateChild();
            grandchild.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(new[] { true, false }, grandchild.Rows(t.Num).Select(row => row.Subsumed));
            Assert.False(grandchild.Check(t.Seen.Of(1L)));
        }

    }

}
