using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <i>Context = copy</i> for rules, for the monotone family: after saturation, a context's view equals that of a
    /// fresh root that replays the host writes of the context's chain and saturates the same rules, and random
    /// conjunctive queries give the same answers in both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For monotone programs every fact an ancestor derived, under fewer equalities or before later writes, is in the
    /// chain's fixpoint, so the saturated view is that fixpoint whatever the schedule. Non-monotone programs are
    /// governed by the operational reference instead (<see cref="ContextRuleDifferentialTests"/>).
    /// </para>
    /// <para>
    /// Family A replays with the same explicit ids and compares cell for cell; family B re-adds the terms and compares
    /// by term names. Frozen contexts are saturated through a fresh child. Each query runs in a child of the saturated
    /// context and of the copy. <c>EGGNOG_CONTEXT_SEEDS</c> and <c>EGGNOG_CONTEXT_SOAK</c> as in
    /// <see cref="ColorDifferential"/>.
    /// </para>
    /// </remarks>
    public class ContextRuleCopyTests
    {

        /// <summary>
        /// The seeds to run.
        /// </summary>
        public static TheoryData<int> Seeds() => ColorDifferential.Seeds();

        /// <summary>
        /// What the copy programs run: the engine, compared with fresh roots after the program.
        /// </summary>
        static readonly ColorRunOptions Options = new(Reference: false, Copy: true);

        /// <summary>
        /// Family A: saturated contexts equal fresh-root replays, cell for cell.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void FamilyAContextsEqualFreshRoots(int seed)
        {
            ColorDifferential.RunOrShrink(ColorProgram.Generate(seed, new ColorFamily(false, false), ColorDifferential.Steps), Options);
        }

        /// <summary>
        /// Family B: saturated contexts equal fresh-root replays, by term names.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void FamilyBContextsEqualFreshRoots(int seed)
        {
            ColorDifferential.RunOrShrink(ColorProgram.Generate(seed, new ColorFamily(true, false), ColorDifferential.Steps), Options);
        }

        /// <summary>
        /// Guards against vacuous programs: copies of children are compared, and queries answer.
        /// </summary>
        [Fact]
        public void TheCopiesAreExercised()
        {
            var stats = new ColorRunStats();
            for (var seed = 0; seed < 20; seed++)
            {
                stats.Add(ColorDifferential.RunOrShrink(ColorProgram.Generate(seed, new ColorFamily(false, false), ColorDifferential.Steps), Options));
                stats.Add(ColorDifferential.RunOrShrink(ColorProgram.Generate(seed, new ColorFamily(true, false), ColorDifferential.Steps), Options));
            }

            TestContext.Current.SendDiagnosticMessage(stats.ToString());
            Assert.True(stats.Copies >= 80, stats.ToString());
            Assert.True(stats.Queries >= 240, stats.ToString());
            Assert.True(stats.ChildIterations >= 100, stats.ToString());
        }

        /// <summary>
        /// Rule <c>R(x, 7) → S(x)</c>; the root holds <c>R(1, 3)</c> and <c>union(7, 3)</c>, runs the rule and is
        /// frozen. The root runs the constant 7 as its canonical 3, so it derives <c>S(1)</c>; a child that asserts
        /// nothing derives nothing more and has its parent's view, which a fresh root replaying the chain also has.
        /// </summary>
        [Fact]
        public void ConstantLosesRootUnionBeforeChildRuns()
        {
            var g = new ContextGraph();
            var one = g.Root.FreshId();
            var three = g.Root.FreshId();
            var seven = g.Root.FreshId();
            Assert.True(three.Rep < seven.Rep);

            // R(x, 7) => S(x), with R = edge and S = mark.
            var rb = g.EGraph.NewRule("r-x-7", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Edge, [x, RuleEntry.Const(seven, ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Set(g.Mark, [x, RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();

            g.Assert(g.Root, g.Edge, one, three);
            g.Root.StageUnion(seven, three);
            g.Root.FlushUpdates();
            g.Root.RunRules(rule);
            Assert.Equal([$"{one.Rep}"], ContextGraph.Keys(g.Root, g.Mark));
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var report = child.RunRules(rule);
            Assert.False(report.Changed);
            Assert.Equal(0, report.RuleSetReport.NumMatches("r-x-7"));
            foreach (var function in new[] { g.Edge, g.Path, g.Mark, g.Low, g.Num, g.Add })
                Assert.Equal(ContextGraph.Rows(g.Root, function), ContextGraph.Rows(child, function));

            child.CheckInvariants();

            // A fresh root replaying the chain: the same facts.
            var copy = new ContextGraph();
            var ids = Enumerable.Range(0, 3).Select(_ => copy.Root.FreshId()).ToArray();
            Assert.Equal([one, three, seven], ids);
            var copyRule = copy.EGraph.NewRule("r-x-7", true);
            var cx = copyRule.NewVar(ColumnTy.Id);
            copyRule.QueryTable(copy.Edge, [cx, RuleEntry.Const(seven, ColumnTy.Id), copyRule.NewVar(copy.UnitType)], false);
            copyRule.Set(copy.Mark, [cx, RuleEntry.Const(copy.Unit, copy.UnitType)]);
            var copied = copyRule.Build();
            copy.Assert(copy.Root, copy.Edge, one, three);
            copy.Root.StageUnion(seven, three);
            copy.Root.FlushUpdates();
            copy.Root.Saturate([copied], 10);
            Assert.Equal(ContextGraph.Keys(copy.Root, copy.Mark), ContextGraph.Keys(child, g.Mark));
            Assert.Equal(ContextGraph.Keys(copy.Root, copy.Edge), ContextGraph.Keys(child, g.Edge));
        }

        /// <summary>
        /// A soak over longer monotone programs of both families, when <c>EGGNOG_CONTEXT_SOAK</c> is set.
        /// </summary>
        [Fact]
        public void Soak()
        {
            ColorDifferential.Soak(new ColorFamily(false, false), Options, alternateHistory: false);
            ColorDifferential.Soak(new ColorFamily(true, false), Options, alternateHistory: false);
        }

        /// <summary>
        /// A program of the monotone family that the copy oracle accepts. A body that binds the output of the min
        /// lattice <c>low</c> is not monotone: once a host write lowers <c>low</c> after a saturation, facts derived
        /// from the old value persist (at a plain root too), while the fresh root replays both writes before saturating
        /// and never sees the old value. For example, <c>union(#2, #1); low(#1, 3); saturate; low(#2, 0)</c> in a
        /// grandchild, with <c>low(v0, v1), num(v1, v2), num(#1, v3) =&gt; set path(add(v2, v0), v2)</c> and a union
        /// rule over edges, makes the context union <c>num(0)</c> with <c>num(3)</c>, which the fresh root never does.
        /// The monotone family's bodies therefore never read a lattice output, so the copy oracle reports no false
        /// failure for this seed.
        /// </summary>
        [Fact]
        public void TheCopyOracleHoldsForTheMonotoneFamilyAtSeed7000246()
        {
            var program = ColorProgram.Generate(7000246, new ColorFamily(true, false), ColorDifferential.Steps * 4);
            ColorDifferential.RunOrShrink(program, new ColorRunOptions(Reference: false, Copy: true));
        }

    }

}
