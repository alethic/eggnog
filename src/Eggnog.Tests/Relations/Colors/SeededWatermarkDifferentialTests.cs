using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Seeded watermarks change nothing but the work: a run with a watermark inherited from an ancestor still applies
    /// every match of the context's view, in the context or in an ancestor whose effects it sees. Random programs of
    /// both families run on two engines in lockstep, the second creating every child with
    /// <see cref="ContextOptions.InheritWatermarks"/> off; the views are equal at every checkpoint, and the seeded
    /// engine matches at most as often as the unseeded one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Odd seeds run the history family (assert-ne and id-order filters, table reads in actions, deletes and clears),
    /// with deletes and clears limited to tables no rule writes. A child's delete of a fact a rule derived is the one
    /// place the two schedules differ by design: the unseeded child's first run re-derives the fact from the ancestor's
    /// unchanged rows, the seeded one, like egglog after a push, does not. The operational reference governs that case
    /// (<see cref="ContextRuleDifferentialTests"/>).
    /// </para>
    /// <para>
    /// <c>EGGNOG_CONTEXT_SEEDS</c>, <c>EGGNOG_CONTEXT_SOAK</c> and <c>EGGNOG_CONTEXT_RULE_SEED</c> as in
    /// <see cref="ColorDifferential"/>.
    /// </para>
    /// </remarks>
    public class SeededWatermarkDifferentialTests
    {

        /// <summary>
        /// The seeds to run.
        /// </summary>
        public static TheoryData<int> Seeds() => ColorDifferential.Seeds();

        /// <summary>
        /// What the programs run: the engine with and without inheritance.
        /// </summary>
        static readonly ColorRunOptions Options = new(Reference: false, Unseeded: true);

        /// <summary>
        /// Family A: seeded and unseeded views are equal cell for cell; seeded matches are at most unseeded.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void FamilyASeedingChangesOnlyTheWork(int seed)
        {
            Run(seed, minting: false);
        }

        /// <summary>
        /// Family B: seeded and unseeded views are equal by term names; seeded matches are at most unseeded.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void FamilyBSeedingChangesOnlyTheWork(int seed)
        {
            Run(seed, minting: true);
        }

        /// <summary>
        /// Guards against a vacuous property: seeding saves matches over the programs, so inheritance is in play.
        /// </summary>
        [Fact]
        public void SeedingSavesMatches()
        {
            var stats = new ColorRunStats();
            for (var seed = 0; seed < 40; seed++)
            {
                stats.Add(Run(seed, false));
                stats.Add(Run(seed, true));
            }

            TestContext.Current.SendDiagnosticMessage(stats.ToString());
            Assert.True(stats.SeededMatches < stats.UnseededMatches, stats.ToString());
            Assert.True(stats.ChildIterations >= 200, stats.ToString());
        }

        /// <summary>
        /// <c>(rule ((h x)) ((set (g x) (f x))))</c>, where the action reads <c>f</c>: a rule that would miss a fact if
        /// it inherited a watermark. The root holds <c>h(a)</c> and <c>f(a) = 5</c>, runs the rule (<c>g(a) = 5</c>)
        /// and is frozen; a child raises <c>f(a)</c> in the lattice (to 2, under the minimum) and runs the rule. Its
        /// action reads a table a descendant can change, so the rule does not inherit the root's watermark, and the
        /// child derives <c>g(a) = 2</c>, as an unseeded child does; with the root's watermark, the match of
        /// <c>h(a)</c> would never fire again. Also through a primitive that reads <c>f</c>
        /// (<see cref="ExternalFunction.ReadsTables"/>).
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ARuleThatReadsATableInItsActionsDoesNotInherit(bool throughAPrimitive)
        {
            var g = new ContextGraph();
            var gFunction = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(g.Min, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "g",
            });
            var read = new TableAction(g.EGraph, g.Low);
            var readF = g.EGraph.RegisterExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) => read.TryLookup(state, args, out result), readsTables: true));

            // h = mark, f = low.
            var rb = g.EGraph.NewRule("g-of-f", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Mark, [x, rb.NewVar(g.UnitType)], false);
            var f = throughAPrimitive ? rb.CallExternalFunc(readF, [x], g.I64, () => "no f") : rb.Lookup(g.Low, [x], () => "no f");
            rb.Set(gFunction, [x, f]);
            var rule = rb.Build();

            var a = g.Root.FreshId();
            g.Root.AddValues([(g.Mark, [a, g.Unit]), (g.Low, [a, g.Int(5)])]);
            g.Root.RunRules(rule);
            Assert.True(g.Root.TryLookup(gFunction, [a], out var rootValue));
            Assert.Equal(g.Int(5), rootValue);
            g.Root.Freeze();
            Assert.False(g.EGraph.GetRuleInfo(rule).AllowInheritance);

            using var seeded = g.Root.CreateChild();
            using var unseeded = g.Root.CreateChild(new ContextOptions { InheritWatermarks = false });
            foreach (var child in new[] { seeded, unseeded })
            {
                child.AddValues([(g.Low, [a, g.Int(2)])]);
                var report = child.RunRules(rule);
                Assert.Equal(1, report.RuleSetReport.NumMatches("g-of-f"));
                Assert.True(child.TryLookup(gFunction, [a], out var value));
                Assert.Equal(g.Int(2), value);
                child.CheckInvariants();
            }

            Assert.Equal(ContextGraph.Rows(unseeded, gFunction), ContextGraph.Rows(seeded, gFunction));

            // The root's row is unchanged.
            Assert.True(g.Root.TryLookup(gFunction, [a], out rootValue));
            Assert.Equal(g.Int(5), rootValue);
        }

        /// <summary>
        /// A soak over longer programs of both families, when <c>EGGNOG_CONTEXT_SOAK</c> is set.
        /// </summary>
        [Fact]
        public void Soak()
        {
            ColorDifferential.Soak(new ColorFamily(false, false, true), Options, alternateHistory: true);
            ColorDifferential.Soak(new ColorFamily(true, false, true), Options, alternateHistory: true);
        }

        /// <summary>
        /// Replays the program <c>EGGNOG_CONTEXT_RULE_SEED</c> names.
        /// </summary>
        [Fact]
        public void Replay()
        {
            ColorDifferential.Replay(Options, deletesOnlyUnwritten: true);
        }

        /// <summary>
        /// Runs one program and checks the match totals.
        /// </summary>
        static ColorRunStats Run(int seed, bool minting)
        {
            var program = ColorProgram.Generate(seed, new ColorFamily(minting, seed % 2 == 1, DeletesOnlyUnwritten: true), ColorDifferential.Steps);
            var stats = ColorDifferential.RunOrShrink(program, Options);
            Assert.True(stats.SeededMatches <= stats.UnseededMatches, $"{program.Family}, seed {seed}: {stats.SeededMatches} seeded matches, {stats.UnseededMatches} unseeded.");
            return stats;
        }

    }

}
