using System;
using System.Collections.Generic;
using System.Globalization;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// The heap measurements of <see cref="ScopeChurnHeapTests"/>, each run in a process of its own
    /// (<see cref="IsolatedRun"/>): each test runs its graphs, reads the heap after full collections after a warm-up,
    /// after a settling interval and after each of <see cref="Intervals"/> measured intervals, and writes the readings
    /// and the values interned over the measured intervals as its figures, for <see cref="ScopeChurnHeapTests"/> to
    /// bound. Each skips unless <see cref="IsolatedRun.Variable"/> names the file for its figures; a full run skips
    /// them all. The figures are <c>heap</c> and <c>interned</c> for a run with no control, and <c>queries</c>,
    /// <c>interned</c>, <c>control</c> and <c>controlInterned</c> for a run against a control.
    /// </summary>
    [Collection(Relations.Tests.AllocationProbeCollection.Name)]
    public sealed class ScopeChurnHeapProbe
    {

        /// <summary>
        /// The steps run before the first reading.
        /// </summary>
        const int WarmUp = 5_000;

        /// <summary>
        /// The steps of the settling interval and of each measured interval. The measured intervals take the graph
        /// from 7,000 served queries to 17,000.
        /// </summary>
        internal const int Interval = 2_000;

        /// <summary>
        /// The measured intervals of a run, after its settling interval: an odd number, so that the median is one of
        /// them.
        /// </summary>
        internal const int Intervals = 5;

        /// <summary>
        /// The steps of the soak's warm-up.
        /// </summary>
        const int SoakWarmUp = 19_800;

        /// <summary>
        /// The steps of the soak's settling interval and of each of its measured intervals.
        /// </summary>
        internal const int SoakInterval = 13_200;

        /// <summary>
        /// The reason a measurement skips outside a child.
        /// </summary>
        const string ChildOnly = $"A heap measurement of {nameof(ScopeChurnHeapTests)}, which runs it in a process of its own; "
            + $"set {IsolatedRun.Variable} to a file for its figures to run it here.";

        /// <summary>
        /// Measures queries that reuse their literals: every tenth splits on <c>kind</c>, the rest query
        /// <c>StrEq(kind, a)</c>.
        /// </summary>
        [Fact]
        public void QueriesThatReuseTheirLiterals()
        {
            Assert.SkipWhen(IsolatedRun.FiguresPath is null, ChildOnly);

            var churn = new ScopeChurn();
            var c = churn.Theory;
            Action<int> derive = i =>
            {
                if (i % 10 == 0)
                    churn.Derive(c.Not.Of(c.StrEq.Of("kind", "zz")), churn.Split);
                else
                    churn.Derive(c.StrEq.Of("kind", "a"));
            };
            var (heap, interned) = Measure(churn, WarmUp, Interval, derive, derive);
            IsolatedRun.Write(new Dictionary<string, long[]>
            {
                ["heap"] = heap,
                ["interned"] = [interned],
            });
        }

        /// <summary>
        /// Measures queries with a new literal each against a control: a second graph that runs the same queries
        /// through the warm-up, then interns each later step's values, the settling interval's included, through
        /// <see cref="ScopeChurn.InternLiteral"/>, so that its pools hold the same values as the query run's at every
        /// reading.
        /// </summary>
        [Fact]
        public void QueriesWithANewLiteral()
        {
            Assert.SkipWhen(IsolatedRun.FiguresPath is null, ChildOnly);

            var queries = new ScopeChurn();
            var (queried, interned) = Measure(queries, WarmUp, Interval, FreshDerivation(queries), FreshDerivation(queries));
            var control = new ScopeChurn();
            var (controlled, controlInterned) = Measure(control, WarmUp, Interval, FreshDerivation(control), i => control.InternLiteral(Fresh(i)));
            IsolatedRun.Write(new Dictionary<string, long[]>
            {
                ["queries"] = queried,
                ["interned"] = [interned],
                ["control"] = controlled,
                ["controlInterned"] = [controlInterned],
            });
        }

        /// <summary>
        /// Measures the soak: 99,000 queries, of which every hundredth has a new literal, every tenth of the others
        /// splits, and the rest reuse their literal, in a warm-up of 19,800, a settling interval of 13,200 and five
        /// measured intervals of 13,200, against a control as <see cref="QueriesWithANewLiteral"/> measures. A
        /// disposed query leaves no child of the schema behind.
        /// </summary>
        [Fact]
        public void TheSoak()
        {
            Assert.SkipWhen(IsolatedRun.FiguresPath is null, ChildOnly);

            var queries = new ScopeChurn();
            var (queried, interned) = Measure(queries, SoakWarmUp, SoakInterval, SoakDerivation(queries), SoakDerivation(queries));
            Assert.Empty(queries.Schema.Children);

            var control = new ScopeChurn();
            var (controlled, controlInterned) = Measure(control, SoakWarmUp, SoakInterval, SoakDerivation(control), i =>
            {
                if (i % 100 == 0)
                    control.InternLiteral(Soak(i));
            });
            IsolatedRun.Write(new Dictionary<string, long[]>
            {
                ["queries"] = queried,
                ["interned"] = [interned],
                ["control"] = controlled,
                ["controlInterned"] = [controlInterned],
            });
        }

        /// <summary>
        /// Gets the literal of the step numbered <paramref name="i"/> of a run with a new literal per query.
        /// </summary>
        /// <param name="i">The step's number.</param>
        /// <returns>The literal.</returns>
        static string Fresh(int i) => "fresh" + i.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Gets the literal of the soak's step numbered <paramref name="i"/>, a multiple of 100.
        /// </summary>
        /// <param name="i">The step's number.</param>
        /// <returns>The literal.</returns>
        static string Soak(int i) => "soak" + i.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Gets the step of a run with a new literal per query: a query of <c>StrEq(kind, literal)</c>, the literal
        /// being <see cref="Fresh"/> of the step's number.
        /// </summary>
        /// <param name="churn">The graph.</param>
        /// <returns>The step, by number.</returns>
        static Action<int> FreshDerivation(ScopeChurn churn)
        {
            var c = churn.Theory;
            return i => churn.Derive(c.StrEq.Of("kind", Fresh(i)));
        }

        /// <summary>
        /// Gets the step of the soak: every hundredth query has a new literal (<see cref="Soak"/> of the step's
        /// number), every tenth of the others splits, and the rest reuse their literal.
        /// </summary>
        /// <param name="churn">The graph.</param>
        /// <returns>The step, by number.</returns>
        static Action<int> SoakDerivation(ScopeChurn churn)
        {
            var c = churn.Theory;
            return i =>
            {
                if (i % 100 == 0)
                    churn.Derive(c.StrEq.Of("kind", Soak(i)));
                else if (i % 10 == 0)
                    churn.Derive(c.Not.Of(c.StrEq.Of("kind", "zz")), churn.Split);
                else
                    churn.Derive(c.StrEq.Of("kind", "a"));
            };
        }

        /// <summary>
        /// Runs <paramref name="warmUp"/> steps of <paramref name="warm"/> and reads the heap, then runs the settling
        /// interval and each of the <see cref="Intervals"/> measured intervals, <paramref name="interval"/> steps of
        /// <paramref name="step"/> each, and reads the heap after each. The steps are numbered from 0 across all of
        /// them. Returns the readings and the values interned over the measured intervals.
        /// </summary>
        /// <param name="churn">The graph.</param>
        /// <param name="warmUp">The steps before the first reading.</param>
        /// <param name="interval">The steps between two readings.</param>
        /// <param name="warm">A step before the first reading, by number.</param>
        /// <param name="step">A step after it, by number.</param>
        /// <returns>
        /// The <see cref="Intervals"/> + 2 readings: after the warm-up, after the settling interval and after each
        /// measured interval; and the values interned after the settling interval.
        /// </returns>
        static (long[] Heap, long Interned) Measure(ScopeChurn churn, int warmUp, int interval, Action<int> warm, Action<int> step)
        {
            var i = 0;
            for (; i < warmUp; i++)
                warm(i);

            var readings = new long[Intervals + 2];
            readings[0] = ScopeChurnHeapTests.RetainedBytes();
            var start = 0L;
            for (var r = 1; r < readings.Length; r++)
            {
                for (var end = i + interval; i < end; i++)
                    step(i);

                readings[r] = ScopeChurnHeapTests.RetainedBytes();
                if (r == 1)
                    start = churn.Graph.Values.InternedCount;
            }

            return (readings, churn.Graph.Values.InternedCount - start);
        }

    }

}
