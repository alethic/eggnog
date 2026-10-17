using System;
using System.Linq;

using Eggnog.Benchmarks.Comparison;

using Xunit;

namespace Eggnog.Tests.Benchmarks
{

    /// <summary>
    /// <see cref="CaseComparer"/>: the campaign's arithmetic (ratio of medians per round, spread between same-build
    /// runs) and its outcome rules (a 2% gate on cases of 100 µs or more; a round whose runs are more than 1% apart is
    /// dropped; two agreeing rounds on one side of the gate decide; four rounds at most).
    /// </summary>
    public sealed class CaseComparerTests
    {

        const double Ms = 1_000_000;

        static readonly ComparisonRules Rules = ComparisonRules.Default;

        /// <summary>
        /// A round with two runs of each build, each build's runs at its value.
        /// </summary>
        static RoundSamples Round(int round, double baseline, double candidate) => new(round, [baseline, baseline], [candidate, candidate]);

        /// <summary>
        /// A round whose baseline runs are 2% apart, so its same-build runs disagree, at a candidate-to-baseline ratio
        /// of about <paramref name="ratio"/>.
        /// </summary>
        static RoundSamples Noisy(int round, double ratio) => new(round, [10 * Ms, 10.2 * Ms], [10.1 * ratio * Ms, 10.1 * ratio * Ms]);

        static CaseOutcome Outcome(params RoundSamples[] rounds) => CaseComparer.Compare("X.Run", rounds, Rules).Outcome;

        [Fact]
        public void TheDefaultRulesAreTheCampaigns()
        {
            Assert.Equal(1.02, Rules.Gate);
            Assert.Equal(0.01, Rules.Spread);
            Assert.Equal(100_000.0, Rules.MinimumNanoseconds);
            Assert.Equal(2, Rules.MinimumRounds);
            Assert.Equal(4, Rules.MaximumRounds);
        }

        [Theory]
        [InlineData(new double[] { 3 }, 3)]
        [InlineData(new double[] { 1, 9, 5 }, 5)]
        [InlineData(new double[] { 4, 1 }, 2.5)]
        [InlineData(new double[] { 7, 1, 3, 100 }, 5)]
        public void TheMedianIsTheMiddleOrTheMeanOfTheTwoMiddle(double[] values, double expected)
        {
            Assert.Equal(expected, CaseComparer.Median(values));
        }

        [Fact]
        public void TheMedianOfNothingOrOfANaNIsNaN()
        {
            Assert.True(double.IsNaN(CaseComparer.Median([])));
            Assert.True(double.IsNaN(CaseComparer.Median([1, double.NaN])));
            Assert.True(double.IsNaN(CaseComparer.Median([1, double.NaN, 5])));
        }

        [Fact]
        public void TheSpreadIsTheLargestOverTheSmallestMinusOne()
        {
            Assert.Equal(0.25, CaseComparer.Spread([4, 5, 4.5]), 12);
            Assert.Equal(0.0, CaseComparer.Spread([2, 2]));
            Assert.True(double.IsNaN(CaseComparer.Spread([2])));
            Assert.True(double.IsNaN(CaseComparer.Spread([0, 2])));
        }

        [Fact]
        public void EachRoundIsTheRatioOfTheBuildsMediansWithTheLargerSpread()
        {
            // Baseline runs 100 and 102 ms (median 101, spread 2%); candidate runs 110, 111 and 112 ms (median 111,
            // spread 1.8%).
            var comparison = CaseComparer.Compare("X.Run", [new RoundSamples(1, [100 * Ms, 102 * Ms], [112 * Ms, 110 * Ms, 111 * Ms])], Rules);
            var round = Assert.Single(comparison.Rounds);
            Assert.Equal(1, round.Round);
            Assert.Equal(101 * Ms, round.Baseline, 6);
            Assert.Equal(111 * Ms, round.Candidate, 6);
            Assert.Equal(111.0 / 101.0, round.Ratio, 12);
            Assert.Equal(0.02, round.Spread, 12);
        }

        [Fact]
        public void TheOverallFiguresAreMediansOfEveryRunInEveryRound()
        {
            var comparison = CaseComparer.Compare("X.Run",
                [new RoundSamples(1, [100 * Ms, 101 * Ms], [90 * Ms, 91 * Ms]), new RoundSamples(2, [103 * Ms, 104 * Ms], [92 * Ms, 93 * Ms])],
                Rules);
            Assert.Equal(102 * Ms, comparison.Baseline, 6);
            Assert.Equal(91.5 * Ms, comparison.Candidate, 6);
        }

        [Fact]
        public void RoundsAreReportedInRoundOrder()
        {
            var comparison = CaseComparer.Compare("X.Run", [Round(3, Ms, Ms), Round(1, Ms, Ms), Round(2, Ms, Ms)], Rules);
            Assert.Equal([1, 2, 3], comparison.Rounds.Select(r => r.Round));
        }

        [Fact]
        public void PassesWhenEveryRoundIsWithinTheGateAndRunsAgree()
        {
            Assert.Equal(CaseOutcome.Pass, Outcome(Round(1, 10 * Ms, 10.1 * Ms), Round(2, 10 * Ms, 9.5 * Ms)));
        }

        [Fact]
        public void ARatioOfExactlyTheGatePasses()
        {
            Assert.Equal(CaseOutcome.Pass, Outcome(Round(1, 10 * Ms, 10.2 * Ms), Round(2, 1 * Ms, 1.02 * Ms)));
        }

        [Fact]
        public void FailsWhenEveryRoundExceedsTheGateAndRunsAgree()
        {
            Assert.Equal(CaseOutcome.Failure, Outcome(Round(1, 10 * Ms, 10.21 * Ms), Round(2, 10 * Ms, 12 * Ms)));
        }

        [Fact]
        public void RoundsThatDisagreeAreInconclusive()
        {
            Assert.Equal(CaseOutcome.Inconclusive, Outcome(Round(1, 10 * Ms, 10.1 * Ms), Round(2, 10 * Ms, 10.5 * Ms)));
            Assert.Equal(CaseOutcome.Inconclusive, Outcome(Round(1, 10 * Ms, 10.5 * Ms), Round(2, 10 * Ms, 10.1 * Ms)));
        }

        [Fact]
        public void ANoisyRoundIsDroppedSoOneCleanRoundIsInconclusive()
        {
            // Every ratio is 1.0 or 1.5, but one build's runs in round 2 differ by 2%: round 2 is dropped, and round 1
            // alone decides nothing.
            var noisy = new RoundSamples(2, [10 * Ms, 10.2 * Ms], [10.1 * Ms, 10.1 * Ms]);
            Assert.Equal(CaseOutcome.Inconclusive, Outcome(Round(1, 10 * Ms, 10 * Ms), noisy));

            var noisyFailure = new RoundSamples(2, [10 * Ms, 10 * Ms], [15 * Ms, 15.3 * Ms]);
            Assert.Equal(CaseOutcome.Inconclusive, Outcome(Round(1, 10 * Ms, 15 * Ms), noisyFailure));
        }

        [Fact]
        public void RunsExactlyOnePercentApartAgree()
        {
            var edge = new RoundSamples(2, [10 * Ms, 10.1 * Ms], [10 * Ms, 10.1 * Ms]);
            Assert.Equal(CaseOutcome.Pass, Outcome(Round(1, 10 * Ms, 10 * Ms), edge));
        }

        [Fact]
        public void ARoundAgreesWhenItsSpreadIsAtMostTheRule()
        {
            Assert.True(CaseComparer.Agrees(new RoundResult(1, Ms, Ms, 1, 0.01), Rules));
            Assert.False(CaseComparer.Agrees(new RoundResult(1, Ms, Ms, 1, 0.0101), Rules));
            Assert.False(CaseComparer.Agrees(new RoundResult(1, Ms, Ms, 1, double.NaN), Rules));
        }

        [Fact]
        public void TwoCleanRoundsSettleACaseAfterANoisyOne()
        {
            // Round 1's runs disagree, so it is dropped whatever its ratio; rounds 2 and 3 agree and decide.
            Assert.Equal(CaseOutcome.Inconclusive, Outcome(Noisy(1, 1.5), Round(2, 10 * Ms, 10 * Ms)));
            Assert.Equal(CaseOutcome.Pass, Outcome(Noisy(1, 1.5), Round(2, 10 * Ms, 10 * Ms), Round(3, 10 * Ms, 10.1 * Ms)));
            Assert.Equal(CaseOutcome.Failure, Outcome(Noisy(1, 1.0), Round(2, 10 * Ms, 11 * Ms), Round(3, 10 * Ms, 10.5 * Ms)));
        }

        [Fact]
        public void TwoCleanRoundsSettleACaseAtTheFourthRound()
        {
            var rounds = new[] { Noisy(1, 1.0), Noisy(2, 1.3), Round(3, 10 * Ms, 11 * Ms), Round(4, 10 * Ms, 11 * Ms) };
            Assert.Equal(CaseOutcome.Failure, Outcome(rounds));
        }

        [Fact]
        public void ACleanRoundOnTheOtherSideOfTheGateKeepsTheCaseInconclusive()
        {
            // Every round's runs agree, so none is dropped; round 1 fails the gate and the later ones pass it.
            var rounds = new[] { Round(1, 10 * Ms, 10.3 * Ms), Round(2, 10 * Ms, 10 * Ms), Round(3, 10 * Ms, 10 * Ms) };
            Assert.Equal(CaseOutcome.Inconclusive, Outcome(rounds));
            Assert.Equal(CaseOutcome.Ungated, Outcome([.. rounds, Round(4, 10 * Ms, 10 * Ms)]));
        }

        [Fact]
        public void FourRoundsWithoutTwoCleanOnesAreUngated()
        {
            var rounds = new[] { Noisy(1, 1.0), Noisy(2, 1.0), Noisy(3, 1.0) };
            Assert.Equal(CaseOutcome.Inconclusive, Outcome(rounds));
            Assert.Equal(CaseOutcome.Ungated, Outcome([.. rounds, Round(4, 10 * Ms, 10 * Ms)]));
        }

        [Fact]
        public void ADecidedCaseIsDecidedAtTheMostRoundsToo()
        {
            var four = new[] { Round(1, 10 * Ms, 11 * Ms), Round(2, 10 * Ms, 11 * Ms), Round(3, 10 * Ms, 11 * Ms), Round(4, 10 * Ms, 11 * Ms) };
            Assert.Equal(CaseOutcome.Failure, Outcome(four));
        }

        [Fact]
        public void ACaseShorterThanTheMinimumIsReportedNotGated()
        {
            var comparison = CaseComparer.Compare("X.Run", [Round(1, 50_000, 100_000), Round(2, 50_000, 100_000)], Rules);
            Assert.Equal(CaseOutcome.Reported, comparison.Outcome);
            Assert.All(comparison.Rounds, r => Assert.Equal(2.0, r.Ratio));
        }

        [Fact]
        public void TheMinimumIsJudgedOnTheBaselinesMedianOverEveryRun()
        {
            // Baseline runs of 99 and 101 µs: median 100 µs, so gated.
            var rounds = new[] { new RoundSamples(1, [99_000, 101_000], [99_000, 99_500]), new RoundSamples(2, [99_000, 101_000], [99_000, 99_500]) };
            Assert.NotEqual(CaseOutcome.Reported, Outcome(rounds));

            var shorter = new[] { new RoundSamples(1, [98_000, 99_900], [99_000, 99_500]), new RoundSamples(2, [98_000, 99_900], [99_000, 99_500]) };
            Assert.Equal(CaseOutcome.Reported, Outcome(shorter));
        }

        [Fact]
        public void OneRoundIsNotEnough()
        {
            Assert.Equal(CaseOutcome.NeedsRuns, Outcome(Round(1, 10 * Ms, 10 * Ms)));
            Assert.Equal(CaseOutcome.NeedsRuns, Outcome());
        }

        [Fact]
        public void ABuildWithOneRunInARoundLeavesTheSpreadUnknown()
        {
            var single = new RoundSamples(2, [10 * Ms], [10 * Ms, 10 * Ms]);
            var comparison = CaseComparer.Compare("X.Run", [Round(1, 10 * Ms, 10 * Ms), single], Rules);
            Assert.Equal(CaseOutcome.NeedsRuns, comparison.Outcome);
            Assert.True(double.IsNaN(comparison.Rounds[1].Spread));
            Assert.Equal(1.0, comparison.Rounds[1].Ratio);
        }

        [Fact]
        public void ARoundWithAnUnknownSpreadIsDroppedAndTheRestDecide()
        {
            var single = new RoundSamples(3, [10 * Ms], [10 * Ms, 10 * Ms]);
            Assert.Equal(CaseOutcome.Pass, Outcome(Round(1, 10 * Ms, 10 * Ms), Round(2, 10 * Ms, 10 * Ms), single));
            Assert.Equal(CaseOutcome.Pass, Outcome(Round(1, 10 * Ms, 10 * Ms), Round(2, 10 * Ms, 10 * Ms), single, Round(4, 10 * Ms, 10 * Ms)));
            Assert.Equal(CaseOutcome.Failure, Outcome(single with { Round = 1 }, Round(2, 10 * Ms, 11 * Ms), Round(3, 10 * Ms, 11 * Ms)));
        }

        [Fact]
        public void FourRoundsWithAnUnknownSpreadAreUngated()
        {
            var rounds = new[] { Round(1, 10 * Ms, 10 * Ms), new RoundSamples(2, [10 * Ms], [10 * Ms]), new RoundSamples(3, [10 * Ms], [10 * Ms]) };
            Assert.Equal(CaseOutcome.NeedsRuns, Outcome(rounds));
            Assert.Equal(CaseOutcome.Ungated, Outcome([.. rounds, new RoundSamples(4, [10 * Ms], [10 * Ms])]));
        }

        [Fact]
        public void ARoundWithoutRunsOfABuildNeedsRuns()
        {
            Assert.Equal(CaseOutcome.NeedsRuns, Outcome(Round(1, 10 * Ms, 10 * Ms), new RoundSamples(2, [10 * Ms, 10 * Ms], [])));
            Assert.Equal(CaseOutcome.NeedsRuns, Outcome(Round(1, 10 * Ms, 10 * Ms), new RoundSamples(2, [], [10 * Ms, 10 * Ms])));

            // Without a round that has both builds' medians, nothing says whether the case is short.
            Assert.Equal(CaseOutcome.NeedsRuns, Outcome(new RoundSamples(1, [50_000, 50_000], []), new RoundSamples(2, [], [50_000, 50_000])));
        }

        [Fact]
        public void AShortCaseIsReportedFromTheRoundsThatHaveBothBuilds()
        {
            var comparison = CaseComparer.Compare("X.Run", [Round(1, 50_000, 60_000), new RoundSamples(2, [50_000, 50_000], [])], Rules);
            Assert.Equal(CaseOutcome.Reported, comparison.Outcome);
            Assert.Equal(60_000.0, comparison.Candidate);
        }

        [Fact]
        public void ARunWithoutAMedianMakesNoOutcome()
        {
            var failed = new RoundSamples(2, [10 * Ms, 10 * Ms], [double.NaN, 10 * Ms]);
            Assert.Equal(CaseOutcome.NoMedian, Outcome(Round(1, 10 * Ms, 10 * Ms), failed));

            // A run without a median is named before a build with one run.
            Assert.Equal(CaseOutcome.NoMedian, Outcome(failed with { Round = 1 }, new RoundSamples(2, [10 * Ms], [10 * Ms])));
        }

        [Fact]
        public void ARoundWithoutAMedianIsDroppedAndTheRestDecide()
        {
            var failed = new RoundSamples(1, [double.NaN, 10 * Ms], [10 * Ms, 10 * Ms]);
            var comparison = CaseComparer.Compare("X.Run", [failed, Round(2, 10 * Ms, 10 * Ms), Round(3, 20 * Ms, 20 * Ms), Round(4, 20 * Ms, 20 * Ms)], Rules);
            Assert.Equal(CaseOutcome.Pass, comparison.Outcome);
            Assert.True(double.IsNaN(comparison.Rounds[0].Ratio));

            // The overall figures come from the rounds with medians: 10, 10, 20, 20, 20 and 20 ms.
            Assert.Equal(20 * Ms, comparison.Baseline, 6);
            Assert.Equal(20 * Ms, comparison.Candidate, 6);

            Assert.Equal(CaseOutcome.Ungated, Outcome(failed, failed with { Round = 2 }, failed with { Round = 3 }, Round(4, 10 * Ms, 10 * Ms)));
        }

        [Fact]
        public void ARoundGivenTwiceIsAnError()
        {
            Assert.Throws<ArgumentException>(() => CaseComparer.Compare("X.Run", [Round(1, Ms, Ms), Round(1, Ms, Ms)], Rules));
        }

        [Fact]
        public void OtherThresholdsApply()
        {
            var rules = Rules with { Gate = 1.10, Spread = 0.05, MinimumNanoseconds = 0, MaximumRounds = 2 };
            var rounds = new[] { new RoundSamples(1, [100, 104], [105, 106]), new RoundSamples(2, [100, 104], [108, 109]) };
            Assert.Equal(CaseOutcome.Pass, CaseComparer.Compare("X.Run", rounds, rules).Outcome);
            Assert.Equal(CaseOutcome.Ungated, CaseComparer.Compare("X.Run", rounds, rules with { Gate = 1.04 }).Outcome);
        }

        [Theory]
        [InlineData(0.0, 0.01, 0.0, 2, 4)]
        [InlineData(double.NaN, 0.01, 0.0, 2, 4)]
        [InlineData(1.02, -0.01, 0.0, 2, 4)]
        [InlineData(1.02, double.NaN, 0.0, 2, 4)]
        [InlineData(1.02, 0.01, -1.0, 2, 4)]
        [InlineData(1.02, 0.01, double.NaN, 2, 4)]
        [InlineData(1.02, 0.01, 0.0, 0, 4)]
        [InlineData(1.02, 0.01, 0.0, 3, 2)]
        public void UnusableRulesAreRejected(double gate, double spread, double minimum, int minimumRounds, int maximumRounds)
        {
            var rules = new ComparisonRules(gate, spread, minimum, minimumRounds, maximumRounds);
            Assert.Throws<ArgumentOutOfRangeException>(() => CaseComparer.Compare("X.Run", [], rules));
        }

        [Fact]
        public void ASpreadAndAMinimumOfZeroAreUsable()
        {
            new ComparisonRules(1.02, 0, 0, 2, 4).Validate();
            new ComparisonRules(1.02, 0.01, 0.0, 2, 2).Validate();
        }

    }

}
