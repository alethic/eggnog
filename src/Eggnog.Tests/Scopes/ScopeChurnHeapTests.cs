using System;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// The retained heap of a long-lived graph serving query scopes (<see cref="ScopeChurn"/>): a disposed query
    /// retains nothing but the values it newly interned. Measured as the smallest of three overlapping slopes after a
    /// warm-up (<see cref="Slope"/>), so one-off growth (caches, tiered compilation, pools) is left out; the exact
    /// counts of <see cref="ScopeChurnTests"/> are the deterministic guard. The probes read the whole process's heap,
    /// so they run in the non-parallel collection of the allocation probes.
    /// </summary>
    [Collection(Relations.Tests.AllocationProbeCollection.Name)]
    public sealed class ScopeChurnHeapTests
    {

        /// <summary>
        /// The derivations run before the first measuring point.
        /// </summary>
        const int WarmUp = 200;

        /// <summary>
        /// The derivations each slope spans, and those before the first reading after the warm-up.
        /// </summary>
        const int Measured = 5_000;

        /// <summary>
        /// The bound on the retained heap per query that reuses its literals.
        /// </summary>
        const double SameLiteralBound = 16;

        /// <summary>
        /// The bound on the retained heap per value a query newly interns.
        /// </summary>
        const double PerValueBound = 128;

        /// <summary>
        /// The environment variable that turns on <see cref="AHundredThousandQueriesRetainNothingBeyondTheirNewValues"/>.
        /// </summary>
        const string SoakVariable = "EGGNOG_CHURN_SOAK";

        /// <summary>
        /// Queries that reuse their literals retain less than 16 bytes each, a plain query's and a split query's alike.
        /// </summary>
        [Fact]
        public void QueriesThatReuseTheirLiteralsRetainNothing()
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            var (perQuery, interned) = Slope(churn, Measured, i =>
            {
                if (i % 10 == 0)
                    churn.Derive(c.Not.Of(c.StrEq.Of("kind", "zz")), churn.Split);
                else
                    churn.Derive(c.StrEq.Of("kind", "a"));
            });

            Assert.Equal(0, interned);
            Assert.True(perQuery < SameLiteralBound, $"{perQuery.ToString("F1", CultureInfo.InvariantCulture)} B retained per query");
        }

        /// <summary>
        /// Queries with a new literal retain at most 128 bytes per value they newly intern (the string, and the
        /// singleton set <c>inset</c> computes from it).
        /// </summary>
        [Fact]
        public void QueriesWithANewLiteralRetainOnlyTheirNewValues()
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            var (perQuery, interned) = Slope(churn, Measured, i => churn.Derive(c.StrEq.Of("kind", "fresh" + i.ToString(CultureInfo.InvariantCulture))));

            Assert.Equal(2L * Measured, interned);
            var perValue = perQuery * Measured / interned;
            Assert.True(perValue <= PerValueBound, $"{perValue.ToString("F1", CultureInfo.InvariantCulture)} B retained per new value");
        }

        /// <summary>
        /// Later passes of one hundred sibling frozen scopes with one hundred local rules each, over the same constants,
        /// retain no more than earlier ones. Two warm-up passes fill the caches and pools the first use of every path
        /// fills; then the heap is read after each of <see cref="SiblingPasses"/> passes. The heap moves from pass to
        /// pass with nothing retained, up and down, so the comparison takes the smallest heap of the last
        /// <see cref="SiblingWindow"/> readings against the smallest of the first as many: a leak of anything a pass
        /// creates grows every reading and so the smallest too, by the leak times the passes between the windows,
        /// while the noise moves the two minimums much less than one reading. The passes intern nothing new.
        /// </summary>
        [Fact]
        public void LaterPassesOfHundredSiblingScopesRetainNoMoreThanEarlierOnes()
        {
            var churn = new ScopeChurn();
            churn.RunSiblings(100, 100);
            churn.RunSiblings(100, 100);
            var interned = churn.Graph.Values.InternedCount;
            var readings = new long[SiblingPasses];
            for (var pass = 0; pass < SiblingPasses; pass++)
            {
                churn.RunSiblings(100, 100);
                readings[pass] = RetainedBytes();
            }

            Assert.Equal(0, churn.Graph.Values.InternedCount - interned);
            var early = readings.AsSpan(0, SiblingWindow).ToArray().Min();
            var late = readings.AsSpan(SiblingPasses - SiblingWindow).ToArray().Min();
            Assert.True(late - early <= SiblingSlack,
                $"{late - early} B retained between the windows; heap after each pass: {string.Join(", ", readings.Select(r => r.ToString(CultureInfo.InvariantCulture)))}");
        }

        /// <summary>
        /// The opt-in soak of a long-lived graph: about one hundred thousand queries, of which every tenth splits and
        /// every hundredth has a new literal, retain nothing beyond their new values over the last two thirds, measured
        /// as <see cref="Slope"/> measures. Set <c>EGGNOG_CHURN_SOAK</c> to any value to run it.
        /// </summary>
        [Fact]
        public void AHundredThousandQueriesRetainNothingBeyondTheirNewValues()
        {
            Assert.SkipUnless(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SoakVariable)) == false,
                $"Set {SoakVariable} to soak a graph with 100,000 query scopes.");

            var churn = new ScopeChurn();
            var c = churn.Theory;
            const int measured = 33_000;
            var (perQuery, interned) = Slope(churn, measured, i =>
            {
                if (i % 100 == 0)
                    churn.Derive(c.StrEq.Of("kind", "soak" + i.ToString(CultureInfo.InvariantCulture)));
                else if (i % 10 == 0)
                    churn.Derive(c.Not.Of(c.StrEq.Of("kind", "zz")), churn.Split);
                else
                    churn.Derive(c.StrEq.Of("kind", "a"));
            });

            Assert.Equal(2L * (measured / 100), interned);
            Assert.True(perQuery * measured <= SameLiteralBound * measured + PerValueBound * interned, $"{perQuery.ToString("F1", CultureInfo.InvariantCulture)} B retained per query");
            Assert.Empty(churn.Schema.Children);
        }

        /// <summary>
        /// The passes of the sibling comparison whose heap is read, after the warm-up.
        /// </summary>
        const int SiblingPasses = 6;

        /// <summary>
        /// The readings at each end of the sibling comparison whose smallest is compared.
        /// </summary>
        const int SiblingWindow = 3;

        /// <summary>
        /// The slack of the sibling comparison: the heap after a pass moves by up to about 500 KB from the pass before,
        /// up and down, with nothing retained, and the smallest of three readings by much less. A leak of every pass's
        /// scopes, rules or variants grows the smallest reading by far more than this between the windows; the slack
        /// is about 17 bytes per local rule of the three passes between them.
        /// </summary>
        const long SiblingSlack = 512 * 1024;

        /// <summary>
        /// The readings of a slope after its first, each <see cref="Measured"/> / 2 derivations (or half the span a
        /// test asks for) after the one before.
        /// </summary>
        const int SlopeReadings = 4;

        /// <summary>
        /// Runs <see cref="WarmUp"/> derivations and <paramref name="measured"/> more, reads the heap, then reads it again
        /// after each of <see cref="SlopeReadings"/> runs of <paramref name="measured"/> / 2 derivations. Returns the
        /// smallest retained heap per derivation over the three overlapping spans of <paramref name="measured"/>
        /// derivations between readings two apart, and the values interned over the first span. A leak grows every span
        /// alike; a one-off step of the process's heap (a cache or pool that grows once) falls in at most two of them.
        /// </summary>
        static (double PerQuery, long Interned) Slope(ScopeChurn churn, int measured, Action<int> derive)
        {
            var i = 0;
            for (; i < WarmUp + measured; i++)
                derive(i);

            var step = measured / 2;
            var readings = new long[SlopeReadings + 1];
            readings[0] = RetainedBytes();
            var start = churn.Graph.Values.InternedCount;
            var interned = 0L;
            for (var r = 1; r <= SlopeReadings; r++)
            {
                for (var end = i + step; i < end; i++)
                    derive(i);

                readings[r] = RetainedBytes();
                if (r == 2)
                    interned = churn.Graph.Values.InternedCount - start;
            }

            var smallest = long.MaxValue;
            for (var r = 2; r <= SlopeReadings; r++)
                smallest = Math.Min(smallest, readings[r] - readings[r - 2]);

            return (smallest / (double)(2 * step), interned);
        }

        /// <summary>
        /// Gets the heap's size after a full, blocking collection that runs pending finalizers.
        /// </summary>
        static long RetainedBytes()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return GC.GetTotalMemory(true);
        }

    }

}
