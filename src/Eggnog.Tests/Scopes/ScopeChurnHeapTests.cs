using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// The retained heap of a long-lived graph serving query scopes (<see cref="ScopeChurn"/>): a disposed query
    /// retains nothing but the values it newly interned. The query tests and the soak take their measurement in a
    /// process of its own that runs nothing else (<see cref="IsolatedRun"/>, running a test of
    /// <see cref="ScopeChurnHeapProbe"/>), and bound the figures it reports: in a full run the process's heap holds
    /// whatever the earlier tests left, and its growth over a 2,000-query interval moves by hundreds of kilobytes from
    /// one interval to the next with nothing retained, more than the bounds allow over the measured intervals. A
    /// measurement reads the heap after full collections after a warm-up, after a settling interval, and after each of
    /// <see cref="ScopeChurnHeapProbe.Intervals"/> measured intervals of as many steps. The settling interval is left
    /// out of the bounds, as a guard against growth that the warm-up starts and does not finish. The tests bound two
    /// statistics of the measured intervals' growth per step (<see cref="AssertRetained"/>). The median, below
    /// <see cref="MedianBound"/>, is the bound for a leak of objects each query creates, which grows every interval;
    /// it leaves out a one-off change of the heap, up or down (a cache or pool filled or trimmed), in any two
    /// intervals. The mean, below the lower <see cref="MeanBound"/>, is the bound for a leak of entries in a table that
    /// grows by doubling (a set or dictionary of ids or stamps kept per query), whose cost lands in the one or two
    /// intervals that resize the table, where the median leaves it out. How much of the table's cost lands in the
    /// measured intervals depends on where its resizes fall, and the bound is set for the least of it. The mean counts
    /// a one-off change in full: a one-off growth of the bound times the measured steps (80 KB over 10,000 steps) fails
    /// it on its own. Queries that intern new values are compared with a control run that runs the same warm-up, then
    /// interns each later step's values in the same pools and order with no query scopes, interval by interval: the
    /// intern pools' tables and arrays grow by doubling and never shrink, at the same counts in both runs, so their
    /// growth cancels out of each interval's difference. The exact counts of <see cref="ScopeChurnTests"/> are the
    /// deterministic guard. The sibling comparison reads this process's heap, with a slack far above its movement; the
    /// class runs in the non-parallel collection of the allocation probes.
    /// </summary>
    [Collection(Relations.Tests.AllocationProbeCollection.Name)]
    public sealed class ScopeChurnHeapTests
    {

        /// <summary>
        /// The bound on the median of the heap a query retains beyond the values it newly interns, per step: of the
        /// measured intervals' growth for queries that reuse their literals, and of the intervals' differences from the
        /// control for queries that intern new values.
        /// </summary>
        const double MedianBound = 16;

        /// <summary>
        /// The bound on the mean of the measured intervals' growth per step, of the same figures as
        /// <see cref="MedianBound"/>. The mean is the bound for a leak of entries in a table that grows by a factor
        /// between 2 and 2.4 when it fills (2 to 3 in the soak), as List, HashSet and Dictionary do, and so grows the
        /// heap only in the intervals that resize it. Kept for every query, such a table holds 7,000 entries at the
        /// start of the query tests' measured intervals, so its capacity there is less than 2.4 times 7,000, below the
        /// 17,000 entries it holds at their end: it resizes at least once in them, and as a resize at least doubles it,
        /// adds at least half its final capacity, 8,500 slots over the 10,000 measured queries, 0.85 a query, wherever
        /// its resizes fall (0.75 in the soak, from 33,000 queries to 99,000, with a capacity at the start of less than
        /// 3 times 33,000). The bound fails such a table whose slots take more than 9.4 bytes (10.7 in the soak), as a
        /// hash set or dictionary with a key per query does, whose slots take at least 16 bytes. With nothing retained
        /// the mean stays within 2 bytes of 0.
        /// </summary>
        const double MeanBound = 8;

        /// <summary>
        /// How long the measurement of a query test may run in its process.
        /// </summary>
        static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(10);

        /// <summary>
        /// How long the soak's measurement may run in its process.
        /// </summary>
        static readonly TimeSpan SoakTimeout = TimeSpan.FromMinutes(60);

        /// <summary>
        /// The environment variable that turns on <see cref="AHundredThousandQueriesRetainNothingBeyondTheirNewValues"/>.
        /// </summary>
        const string SoakVariable = "EGGNOG_CHURN_SOAK";

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="output">The test's output, which gets the measuring process's command line and the growth of
        /// every interval.</param>
        public ScopeChurnHeapTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Queries that reuse their literals retain less than 16 bytes each, a plain query's and a split query's alike:
        /// the median growth of the measured intervals per step is less than <see cref="MedianBound"/>, and the mean
        /// less than <see cref="MeanBound"/> (<see cref="ScopeChurnHeapProbe.QueriesThatReuseTheirLiterals"/>).
        /// </summary>
        [Fact]
        public void QueriesThatReuseTheirLiteralsRetainNothing()
        {
            var figures = IsolatedRun.Run(typeof(ScopeChurnHeapProbe), nameof(ScopeChurnHeapProbe.QueriesThatReuseTheirLiterals),
                QueryTimeout, output);
            var heap = Figure(figures, "heap", ScopeChurnHeapProbe.Intervals + 2);

            Assert.Equal(0, Figure(figures, "interned", 1)[0]);
            var growth = Growth(heap);
            const int interval = ScopeChurnHeapProbe.Interval;
            AssertRetained("retained per query", growth, interval, $"growth of the intervals {Format(growth, interval)}");
        }

        /// <summary>
        /// Queries with a new literal retain less than 16 bytes each beyond what interning their new values (the
        /// string, and the singleton set <c>inset</c> computes from it) retains with no query scopes: over the measured
        /// intervals, the median difference from the control per step is less than <see cref="MedianBound"/>, and the
        /// mean less than <see cref="MeanBound"/> (<see cref="ScopeChurnHeapProbe.QueriesWithANewLiteral"/>).
        /// </summary>
        [Fact]
        public void QueriesWithANewLiteralRetainOnlyTheirNewValues()
        {
            var figures = IsolatedRun.Run(typeof(ScopeChurnHeapProbe), nameof(ScopeChurnHeapProbe.QueriesWithANewLiteral),
                QueryTimeout, output);
            const int interval = ScopeChurnHeapProbe.Interval;
            AssertBeyondControl(figures, interval, 2L * ScopeChurnHeapProbe.Intervals * interval);
        }

        /// <summary>
        /// Later passes of one hundred sibling frozen scopes with one hundred local rules each, over the same constants,
        /// retain no more than earlier ones. Two warm-up passes fill the caches and pools the first use of every path
        /// fills; then the heap is read after each of <see cref="SiblingPasses"/> passes. The heap moves from pass to
        /// pass with nothing retained, up and down, so the comparison takes the median heap of the last
        /// <see cref="SiblingWindow"/> readings against the median of the first as many: a leak of anything a pass
        /// creates grows every reading and so the median too, by the leak times the passes between the windows, while
        /// one reading the noise moves far, as a collection that compacts more than the rest does, moves neither
        /// median. The passes intern nothing new.
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
            var early = Median(readings.AsSpan(0, SiblingWindow));
            var late = Median(readings.AsSpan(SiblingPasses - SiblingWindow));
            Assert.True(late - early <= SiblingSlack,
                $"{late - early} B retained between the windows; heap after each pass: {string.Join(", ", readings.Select(r => r.ToString(CultureInfo.InvariantCulture)))}");
        }

        /// <summary>
        /// The opt-in soak of a long-lived graph: 99,000 queries, of which every tenth splits and every hundredth has a
        /// new literal, retain nothing beyond their new values over the last two thirds (five measured intervals of
        /// 13,200 queries after a warm-up of 19,800 and a settling interval of 13,200), measured against a control as
        /// <see cref="QueriesWithANewLiteralRetainOnlyTheirNewValues"/> measures
        /// (<see cref="ScopeChurnHeapProbe.TheSoak"/>). Set <c>EGGNOG_CHURN_SOAK</c> to any value to run it.
        /// </summary>
        [Fact]
        public void AHundredThousandQueriesRetainNothingBeyondTheirNewValues()
        {
            Assert.SkipUnless(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SoakVariable)) == false,
                $"Set {SoakVariable} to soak a graph with 100,000 query scopes.");

            var figures = IsolatedRun.Run(typeof(ScopeChurnHeapProbe), nameof(ScopeChurnHeapProbe.TheSoak), SoakTimeout, output);
            const int interval = ScopeChurnHeapProbe.SoakInterval;
            AssertBeyondControl(figures, interval, 2L * (ScopeChurnHeapProbe.Intervals * interval / 100));
        }

        /// <summary>
        /// The passes of the sibling comparison whose heap is read, after the warm-up.
        /// </summary>
        const int SiblingPasses = 6;

        /// <summary>
        /// The readings at each end of the sibling comparison whose median is compared; odd, so that the median is one
        /// of them.
        /// </summary>
        const int SiblingWindow = 3;

        /// <summary>
        /// The slack of the sibling comparison: the heap after a pass moves by up to about 500 KB from the pass before,
        /// up and down, with nothing retained, and the median of three readings by much less. A leak of every pass's
        /// scopes, rules or variants grows the median by far more than this between the windows; the slack
        /// is about 17 bytes per local rule of the three passes between them.
        /// </summary>
        const long SiblingSlack = 512 * 1024;

        /// <summary>
        /// Gets the median of an odd number of readings.
        /// </summary>
        static long Median(ReadOnlySpan<long> readings)
        {
            var sorted = readings.ToArray();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }

        /// <summary>
        /// The longest run of collections a reading makes after its first three (<see cref="RetainedBytes"/>) while
        /// the heap still changes.
        /// </summary>
        const int MaxSettlingCollections = 8;

        /// <summary>
        /// Gets the heap's size after full, blocking, compacting collections that run pending finalizers
        /// (<see cref="Collect"/>): three, so that an object that re-registers itself for finalization once (a
        /// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/>'s container) is freed,
        /// then more until two readings in a row agree, at most <see cref="MaxSettlingCollections"/>.
        /// </summary>
        /// <returns>The size, in bytes.</returns>
        internal static long RetainedBytes()
        {
            for (var k = 0; k < 3; k++)
                Collect();

            var last = GC.GetTotalMemory(false);
            for (var k = 0; k < MaxSettlingCollections; k++)
            {
                Collect();
                var now = GC.GetTotalMemory(false);
                if (now == last)
                    break;

                last = now;
            }

            return last;
        }

        /// <summary>
        /// Runs a full, blocking collection that compacts the small and the large object heaps, then the finalizers it
        /// queued. <see cref="GC.GetTotalMemory(bool)"/> counts the free space a collection leaves between live objects,
        /// and a collection that only sweeps leaves it to grow: under the segment-based collector that .NET uses on
        /// macOS, collections that did not compact let it grow by 37 to 64 bytes a query, with the live bytes flat to a
        /// few kilobytes, until a later collection compacted it away. A compacted heap holds no such space, so the size
        /// is what the heap's objects take.
        /// </summary>
        static void Collect()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        /// <summary>
        /// Gets a figure of a measurement, which must have <paramref name="length"/> values.
        /// </summary>
        /// <param name="figures">The measurement's figures, by name.</param>
        /// <param name="name">The figure's name.</param>
        /// <param name="length">The values it must have.</param>
        /// <returns>The values.</returns>
        static long[] Figure(IReadOnlyDictionary<string, long[]> figures, string name, int length)
        {
            if (figures.TryGetValue(name, out var values) == false)
                Assert.Fail($"The measurement reported no figure '{name}'; it reported {string.Join(", ", figures.Keys)}.");

            Assert.Equal(length, values.Length);
            return values;
        }

        /// <summary>
        /// Gets the heap's growth over each interval of a measurement, the settling interval first: between consecutive
        /// readings.
        /// </summary>
        /// <param name="heap">The readings: after the warm-up, after the settling interval and after each measured
        /// interval.</param>
        /// <returns>The growth of each interval, in bytes.</returns>
        static long[] Growth(long[] heap)
        {
            var growth = new long[heap.Length - 1];
            for (var r = 1; r < heap.Length; r++)
                growth[r - 1] = heap[r] - heap[r - 1];

            return growth;
        }

        /// <summary>
        /// Gets the median of an odd number of values: the middle one in order.
        /// </summary>
        /// <param name="values">The values.</param>
        /// <returns>The median.</returns>
        static long Median(long[] values)
        {
            var sorted = values.Order().ToArray();
            return sorted[sorted.Length / 2];
        }

        /// <summary>
        /// Asserts that both runs of a measurement against a control interned <paramref name="interned"/> values over
        /// their measured intervals, and that what the query run retains per step beyond its control is within both
        /// bounds of <see cref="AssertRetained"/>, over the differences of the two runs' growth over the same
        /// intervals. The pools grow in the same intervals of both runs and cancel.
        /// </summary>
        /// <param name="figures">The measurement's figures: <c>queries</c>, <c>interned</c>, <c>control</c> and
        /// <c>controlInterned</c>.</param>
        /// <param name="interval">The steps of an interval.</param>
        /// <param name="interned">The values each run must intern over its measured intervals.</param>
        void AssertBeyondControl(IReadOnlyDictionary<string, long[]> figures, int interval, long interned)
        {
            var queried = Figure(figures, "queries", ScopeChurnHeapProbe.Intervals + 2);
            var controlled = Figure(figures, "control", ScopeChurnHeapProbe.Intervals + 2);
            Assert.Equal(interned, Figure(figures, "interned", 1)[0]);
            Assert.Equal(interned, Figure(figures, "controlInterned", 1)[0]);

            var queries = Growth(queried);
            var control = Growth(controlled);
            var beyond = queries.Zip(control, (q, k) => q - k).ToArray();
            AssertRetained("retained per query beyond the control", beyond, interval,
                $"growth of the queries {Format(queries, interval)}, of the control {Format(control, interval)}, beyond the control {Format(beyond, interval)}");
        }

        /// <summary>
        /// Asserts that the median of the measured intervals' growth per step is less than <see cref="MedianBound"/>
        /// and the mean less than <see cref="MeanBound"/>, and writes both and the growth of every interval to the
        /// output. The median is the bound for a leak of objects each step creates, which grows every interval, and
        /// leaves out a one-off change of the heap in any two intervals; the mean is the bound for a leak of entries in
        /// a table that grows by doubling, which grows only the intervals that resize the table. The settling interval
        /// is in neither.
        /// </summary>
        /// <param name="subject">What the statistics are, for the output.</param>
        /// <param name="growth">The growth of each interval (<see cref="Growth"/>), settling interval first.</param>
        /// <param name="interval">The steps of an interval.</param>
        /// <param name="intervals">The growth of the intervals as text, for the output.</param>
        void AssertRetained(string subject, long[] growth, int interval, string intervals)
        {
            var measured = growth[1..];
            var median = Median(measured) / (double)interval;
            var mean = measured.Sum() / (double)(measured.Length * interval);
            var text = string.Create(CultureInfo.InvariantCulture, $"{subject}: median {median:F1} B, mean {mean:F1} B; {intervals}");
            output.WriteLine(text);
            Assert.True(median < MedianBound, text);
            Assert.True(mean < MeanBound, text);
        }

        /// <summary>
        /// Formats the growth of each interval per step, the settling interval first.
        /// </summary>
        /// <param name="growth">The growth of each interval, in bytes.</param>
        /// <param name="interval">The steps of an interval.</param>
        /// <returns>The text.</returns>
        static string Format(long[] growth, int interval)
        {
            var perStep = growth.Select(g => (g / (double)interval).ToString("F1", CultureInfo.InvariantCulture)).ToArray();
            return "settling " + perStep[0] + "; " + string.Join(", ", perStep.Skip(1)) + " B per step";
        }

    }

}
