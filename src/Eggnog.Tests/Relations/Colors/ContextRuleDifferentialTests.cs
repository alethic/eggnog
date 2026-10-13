using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The rule differential: random programs of both families run on the engine's contexts and on the operational
    /// <see cref="ReferenceRuleContext"/> in lockstep (<see cref="ColorRunner"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Programs write, union, run rules once or to saturation (through the API and step by step), and create, freeze
    /// and dispose contexts up to three below the root, ancestors changing after their children exist. Family A uses
    /// explicit ids and no minting, compared cell for cell; family B adds terms and minting rules, compared by term
    /// names. Odd seeds run the history family: assert-ne filters, an id-order filter (family A), actions that read a
    /// table, and deletes and clears wherever asked, applied where the engine's retraction rule allows them (no live
    /// descendants, and the root or a child of a frozen parent) and refused elsewhere.
    /// </para>
    /// <para>
    /// After every write, run and rebuild the harness checks <see cref="EGraphContext.CheckInvariants"/> and the
    /// lookup-path comparison (<see cref="LookupPaths"/>), compares the view, and checks <c>RestampedCopies == 0</c>.
    /// <c>EGGNOG_CONTEXT_SEEDS</c>, <c>EGGNOG_CONTEXT_SOAK</c>, <c>EGGNOG_CONTEXT_SEED_BASE</c> and
    /// <c>EGGNOG_CONTEXT_RULE_SEED</c> as in <see cref="ColorDifferential"/>.
    /// </para>
    /// </remarks>
    public class ContextRuleDifferentialTests
    {

        /// <summary>
        /// The seeds to run.
        /// </summary>
        public static TheoryData<int> Seeds() => ColorDifferential.Seeds();

        /// <summary>
        /// Family A (explicit ids, no minting) agrees with the reference, cell for cell.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void FamilyAAgreesWithTheReference(int seed)
        {
            ColorDifferential.RunOrShrink(ColorProgram.Generate(seed, new ColorFamily(false, seed % 2 == 1), ColorDifferential.Steps), new ColorRunOptions());
        }

        /// <summary>
        /// Family B (constructor minting) agrees with the reference by term names.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void FamilyBAgreesWithTheReference(int seed)
        {
            ColorDifferential.RunOrShrink(ColorProgram.Generate(seed, new ColorFamily(true, seed % 2 == 1), ColorDifferential.Steps), new ColorRunOptions());
        }

        /// <summary>
        /// Guards against vacuous programs: across 40 seeds of each family, children run rules that change their views,
        /// inherit watermarks, retract under frozen parents, see refusals, freeze and dispose.
        /// </summary>
        [Fact]
        public void TheProgramsExerciseTheContexts()
        {
            var stats = new ColorRunStats();
            for (var seed = 0; seed < 40; seed++)
            {
                stats.Add(ColorDifferential.RunOrShrink(ColorProgram.Generate(seed, new ColorFamily(false, seed % 2 == 1), ColorDifferential.Steps), new ColorRunOptions()));
                stats.Add(ColorDifferential.RunOrShrink(ColorProgram.Generate(seed, new ColorFamily(true, seed % 2 == 1), ColorDifferential.Steps), new ColorRunOptions()));
            }

            TestContext.Current.SendDiagnosticMessage(stats.ToString());
            Assert.True(stats.ChildIterations >= 200, stats.ToString());
            Assert.True(stats.ChangingIterations >= 200, stats.ToString());
            Assert.True(stats.InheritedRuns >= 50, stats.ToString());
            Assert.True(stats.ReferenceMatches >= 500, stats.ToString());
            Assert.True(stats.ChildRetractions >= 10, stats.ToString());
            Assert.True(stats.RootRetractions >= 5, stats.ToString());
            Assert.True(stats.Refusals >= 10, stats.ToString());
            Assert.True(stats.Freezes >= 20, stats.ToString());
            Assert.True(stats.Disposals >= 10, stats.ToString());
        }

        /// <summary>
        /// A soak over longer programs of both families, when <c>EGGNOG_CONTEXT_SOAK</c> is set.
        /// </summary>
        [Fact]
        public void Soak()
        {
            ColorDifferential.Soak(new ColorFamily(false, false), new ColorRunOptions(), alternateHistory: true);
            ColorDifferential.Soak(new ColorFamily(true, false), new ColorRunOptions(), alternateHistory: true);
        }

        /// <summary>
        /// Replays the program <c>EGGNOG_CONTEXT_RULE_SEED</c> names.
        /// </summary>
        [Fact]
        public void Replay()
        {
            ColorDifferential.Replay(new ColorRunOptions());
        }

    }

}
