using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The lift of tables with id outputs. No id local to a case reaches its parent, so a relation-kind table with an
    /// id output is refused. A function-kind table with an id output lifts without a conflict when its parent row sits
    /// under a key the cases merge or its stored output is merged with another id; it lifts a value every case holds
    /// over the stored one when a lattice primitive merges it; and a case's failed assert-eq write never reaches the
    /// parent.
    /// </summary>
    public class LiftIdOutputTests
    {

        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        static FunctionId Pick(ContextGraph g, MergeFn merge) => g.EGraph.AddTable(new FunctionConfig
        {
            Schema = [ColumnTy.Id, ColumnTy.Id],
            Default = DefaultVal.Fail,
            Merge = merge,
            Name = "pick",
        });

        /// <summary>
        /// An assert-eq id-output function whose parent row sits under a key the cases merge with another parent id:
        /// the lift writes the new key's row next to the meet's unions of the keys and of the outputs, and the rebuild
        /// that folds the two rows together must not see a conflict. Into the root, and into a child of a frozen root.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnAssertEqIdFunctionUnderAMergedKeyLiftsWithoutAConflict(bool childParent)
        {
            var (g, ids) = Setup(5);
            var (m, z, k, k0, e) = (ids[0], ids[1], ids[2], ids[3], ids[4]);
            var pick = Pick(g, MergeFn.AssertEq);
            g.Root.StageRow(pick, [k0, e]);
            g.Root.FlushUpdates();
            EGraphContext? scope = null;
            if (childParent)
            {
                g.Root.Freeze();
                scope = g.Root.CreateChild();
            }

            var parent = scope ?? g.Root;
            try
            {
                using var set = new CaseSet(parent);
                var one = set.AddCase();
                one.StageUnion(k, k0);
                one.StageUnion(e, m);
                one.StageUnion(e, z);
                var two = set.AddCase();
                two.StageUnion(k, k0);
                two.StageUnion(e, z);

                LiftReport? report = null;
                var thrown = Record.Exception(() => report = set.Lift(new LiftSpec(new LiftTable(pick, LiftTableKind.Function)), LiftOptions.Default));
                Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
                Assert.False(parent.IsFaulted);
                Assert.Equal(parent.Find(k), parent.Find(k0));
                Assert.Equal(parent.Find(e), parent.Find(z));
                Assert.NotEqual(parent.Find(e), parent.Find(m));
                Assert.Equal(parent.Find(e), parent.LookupId(pick, [k]));
                parent.CheckInvariants();
            }
            finally
            {
                scope?.Dispose();
            }
        }

        /// <summary>
        /// Completeness for an id-output function merged by a declared lattice primitive (here the larger id): the root
        /// holds <c>pick(k) = e</c>, and every case writes <c>pick(k) = p</c>, which the merge keeps, so <c>pick(k) =
        /// p</c> holds in every live case while <c>e</c> and <c>p</c> stay distinct there. The lift writes it, as it
        /// writes a base-output function's joined value over a stored one. Skipping every key the parent holds, which
        /// keeps an assert-eq merge from conflicting, is right only when each case's output is the class of the
        /// parent's, as under union and assert-eq merges.
        /// </summary>
        [Fact]
        public void AnIdFunctionWithALatticeMergeLiftsAValueEveryCaseHoldsOverTheStoredOne()
        {
            var (g, ids) = Setup(3);
            var (k, e, p) = (ids[0], ids[1], ids[2]);
            var larger = Register(g.EGraph, (_, args) => Math.Max(args[0], args[1]));
            var pick = Pick(g, MergeFn.Primitive(larger, MergeFn.Old, MergeFn.New).AsLattice());
            g.Root.StageRow(pick, [k, e]);
            g.Root.FlushUpdates();

            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                @case.StageRow(pick, [k, p]);
                @case.EnsureClean();
                Assert.Equal(p, @case.LookupId(pick, [k]));
                Assert.NotEqual(@case.Find(e), @case.Find(p));
            }

            set.Lift(new LiftSpec(new LiftTable(pick, LiftTableKind.Function)), LiftOptions.Default);
            Assert.True(p == g.Root.LookupId(pick, [k]), $"pick(k) = p holds in every live case, but the root still holds pick(k) = {g.Root.LookupId(pick, [k])?.Rep} (e = {e.Rep}, p = {p.Rep})");
        }

        /// <summary>
        /// The id-output function path writes over a key the parent holds only when every live case moved off the
        /// stored class, which an <see cref="MergeFn.AssertEq"/> merge allows only through a failed merge. A case whose
        /// assert-eq write conflicted with the parent's row does not lead the lift to write that conflict into the
        /// parent: the lift is refused or leaves the parent's row as it was, and the parent is never faulted.
        /// </summary>
        [Fact]
        public void AFailedAssertEqWriteInACaseNeverReachesTheParent()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var (k, e, x) = (g.Root.FreshId(), g.Root.FreshId(), g.Root.FreshId());
            var pick = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "pick",
            });
            g.Root.StageRow(pick, [k, e]);
            g.Root.FlushUpdates();

            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                @case.StageRow(pick, [k, x]);
                _ = Record.Exception(() => @case.EnsureClean());
            }

            _ = Record.Exception(() => set.Lift(new LiftSpec(new LiftTable(pick, LiftTableKind.Function)), LiftOptions.Default));
            Assert.False(g.Root.IsFaulted);
            Assert.Equal(g.Root.Find(e), g.Root.LookupId(pick, [k]));
            Assert.NotEqual(g.Root.Find(e), g.Root.Find(x));
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// No id local to a case reaches its parent. A function with an id output listed as a relation would write the
        /// seed's canonical output, here a fresh id of the seed case; the kind is refused for it before anything is
        /// written.
        /// </summary>
        [Fact]
        public void ARelationKindWithAnIdOutputLeaksNoLocalId()
        {
            var (g, ids) = Setup(1);
            var a = ids[0];
            var pick = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.UnionId,
                Name = "pick",
            });

            using var set = new CaseSet(g.Root);
            var cases = new EGraphContext[2];
            for (var i = 0; i < 2; i++)
            {
                var @case = cases[i] = set.AddCase();
                @case.StageRow(pick, [a, @case.FreshId()]);
            }

            var spec = new LiftSpec(new LiftTable(pick, LiftTableKind.Relation));
            var refused = Record.Exception(() => set.Lift(spec, LiftOptions.Default));
            if (refused is not null)
            {
                Assert.True(refused is ArgumentException, $"expected a refusal of the kind before anything is written, got {refused.GetType().Name}: {refused.Message}; root faulted: {g.Root.IsFaulted}");
                return;
            }

            if (g.Root.LookupId(pick, [a]) is { } output)
                foreach (var @case in cases)
                    Assert.False(@case.IsLocalId(output), $"the root's pick(a) = {output.Rep} is an id local to a case");
        }

        /// <summary>
        /// Soundness and the root's semantics: a function with an id output whose merge asserts equality
        /// (<see cref="MergeFn.AssertEq"/>, a lattice merge) holds <c>pick(a) = e</c> in the root, and every case
        /// merges <c>e</c> with <c>x</c>. The lift's meet writes the union; the id-output function path does not also
        /// write <c>pick(a) = x</c> against the stored <c>pick(a) = e</c>, which the merge would refuse as a conflict.
        /// </summary>
        [Fact]
        public void AnIdOutputFunctionLiftDoesNotConflictWithItsStoredRow()
        {
            var (g, ids) = Setup(3);
            var (a, x, e) = (ids[0], ids[1], ids[2]);
            var pick = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "pick",
            });

            g.Root.StageRow(pick, [a, e]);
            g.Root.FlushUpdates();

            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
                set.AddCase().StageUnion(e, x);

            var report = set.Lift(new LiftSpec(new LiftTable(pick, LiftTableKind.Function)), LiftOptions.Default);
            Assert.Equal(1, report.Unions);
            Assert.Equal(g.Root.Find(x), g.Root.Find(e));
            Assert.Equal(g.Root.Find(e), g.Root.LookupId(pick, [a]));
        }

        /// <summary>
        /// The same as <see cref="AnIdOutputFunctionLiftDoesNotConflictWithItsStoredRow"/>, lifted into a writable
        /// child of a frozen root.
        /// </summary>
        [Fact]
        public void AnIdOutputFunctionLiftIntoAChildDoesNotConflictWithItsStoredRow()
        {
            var (g, ids) = Setup(3);
            var (a, x, e) = (ids[0], ids[1], ids[2]);
            var pick = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "pick",
            });

            g.Root.StageRow(pick, [a, e]);
            g.Root.FlushUpdates();
            g.Root.Freeze();
            using var scope = g.Root.CreateChild();

            using var set = new CaseSet(scope);
            for (var i = 0; i < 2; i++)
                set.AddCase().StageUnion(e, x);

            LiftReport? report = null;
            var thrown = Record.Exception(() => report = set.Lift(new LiftSpec(new LiftTable(pick, LiftTableKind.Function)), LiftOptions.Default));
            Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}; scope faulted: {scope.IsFaulted}");
            Assert.Equal(1, report!.Unions);
            Assert.False(scope.IsFaulted);
            Assert.Equal(scope.Find(x), scope.Find(e));
            Assert.Equal(scope.Find(e), scope.LookupId(pick, [a]));
        }

        /// <summary>
        /// <see cref="LiftTableKind.Relation"/> writes the output every live case holds. Listed for a constructor,
        /// whose output in each case is a term minted there, it would write a case-local id into the parent, while
        /// local ids appear in no ancestor row; the kind is refused for an output that is not a base value.
        /// </summary>
        [Fact]
        public void ARelationKindNeverWritesACaseLocalIdIntoTheParent()
        {
            var (g, _) = Setup(0);
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
                set.AddCase().AddTerm(g.Num, [g.Int(7)]);

            try
            {
                set.Lift(new LiftSpec(new LiftTable(g.Num, LiftTableKind.Relation)), LiftOptions.Default);
            }
            catch (ArgumentException)
            {
                return;
            }

            var outputs = new List<Value>();
            g.Root.ForEach(g.Num, row => outputs.Add(row.Values[1]));
            foreach (var @case in set.Cases)
            {
                foreach (var output in outputs)
                    Assert.False(@case.IsLocalId(output), $"the parent's num(7) names id {output.Rep}, local to a case");

                @case.CheckInvariants();
            }
        }

        /// <summary>
        /// A function with an id output and egglog's default merge for custom functions
        /// (<see cref="MergeFn.AssertEq"/>, a lattice merge here, so cases may write it). The parent holds <c>f(k) =
        /// e</c>; every case unions <c>e</c> with the smaller <c>m</c>, so every case's <c>f(k)</c> is <c>m</c>. The
        /// fact <c>f(k) = e</c> already holds in the parent, and <c>m = e</c> is the meet's equality: the lift writes
        /// that union, and does not fail. Writing <c>f(k) = m</c> next to the union, in the same flush, would make the
        /// assert-eq merge compare <c>e</c> with <c>m</c> before the union is applied.
        /// </summary>
        [Fact]
        public void AnAssertEqIdFunctionTheParentAlreadyHoldsLiftsOnlyTheEquality()
        {
            var (g, ids) = Setup(3);
            var (k, m, e) = (ids[0], ids[1], ids[2]);
            var f = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "f",
            });

            g.Root.StageRow(f, [k, e]);
            g.Root.FlushUpdates();
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
                set.AddCase().StageUnion(m, e);

            var report = set.Lift(new LiftSpec(new LiftTable(f, LiftTableKind.Function)), LiftOptions.Default);
            Assert.Equal(1, report.Unions);
            Assert.Equal(g.Root.Find(m), g.Root.Find(e));
            Assert.Equal(g.Root.Find(m), g.Root.LookupId(f, [k]));
            Assert.False(g.Root.IsFaulted);
        }

    }

}
