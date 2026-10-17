using System.Globalization;
using System.Linq;

using Eggnog.Tests.Predicates;
using Eggnog.Tests.Predicates.Scoped;

using Xunit;

namespace Eggnog.Tests.Cases
{

    /// <summary>
    /// What catch-up costs the cases of a domain split under a writable scope, counted against the rest of their work,
    /// on <see cref="PredicateTheory"/>: a schema whose discriminator <c>kind</c> holds some kinds that store
    /// <c>ref</c> as a lowercase UUID and one that does not, and a query that names the UUID kinds and compares
    /// <c>ref</c>, which lowers only through a split of one case per kind. The schema's split runs under the writable
    /// schema scope while it compiles, and the query's under the writable query scope while it saturates; each live
    /// case catches up with what its parent wrote since its last run (the lifts and the parent's own iterations between
    /// split steps) before it runs again. The query opens 3 cases in one test and 8 in the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For each run, <see cref="SplitCountObserver"/> charges to every split step the rows its cases examined in
    /// catch-ups, in their rule runs' scans, and in the dynamic indexes those runs built; the tests print them, with
    /// catch-up's share of the three, and check the cases opened and the lowered result. The counts decide whether
    /// catch-up under a busy parent needs an index of its own (a share above 25% at 8 cases); they need no idle
    /// machine.
    /// </para>
    /// <para>
    /// The steps' counts must add up to the observer's total, and the query's totals must equal the figures recorded
    /// for them: the counts are deterministic, so a change to how the engine catches up, scans or indexes, or to how
    /// the observer charges them, shows as a failure here, and the new figures replace the recorded ones once they are
    /// understood. The query's cases must examine at least one row in catch-ups; the schema's may examine none.
    /// </para>
    /// <para>
    /// The executor's counters need <c>EGGNOG_COUNTERS</c>, which the suite turns on for itself; the tests are skipped
    /// when it is off.
    /// </para>
    /// </remarks>
    public sealed class SplitCatchUpCountTests
    {

        /// <summary>
        /// The UUID the query compares <c>ref</c> with.
        /// </summary>
        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="output">The test output.</param>
        public SplitCatchUpCountTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void AQueryOfThreeKindsCountsThreeCases()
        {
            Assert.SkipUnless(Relations.ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            KindsQuery(3, (33, 201, 393));
        }

        [Fact]
        public void AQueryOfEightKindsCountsEightCases()
        {
            Assert.SkipUnless(Relations.ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            KindsQuery(8, (128, 1_000, 984));
        }

        /// <summary>
        /// Compiles a schema of <paramref name="kinds"/> kinds that declare <c>ref</c> a lowercase UUID (<c>k0</c>,
        /// <c>k1</c> and on) and the kind <c>other</c>, which does not, then asks the query that names those kinds and
        /// compares <c>ref</c>; counts both runs.
        /// </summary>
        /// <param name="kinds">The kinds the query names, and the cases its split opens.</param>
        /// <param name="expected">The rows the query's cases examined in catch-ups, scans and indexes, in all.</param>
        void KindsQuery(int kinds, (long CatchUp, long Scanned, long Indexed) expected)
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();

            var named = Enumerable.Range(0, kinds).Select(i => $"k{i}").ToArray();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            schema.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.From(named.Append("other"))), c.Discriminator.Of("kind"));
            foreach (var kind in named)
                c.DeclareBranch(schema, "kind", kind, c.Form.Of("ref", "uuid-lower"));

            var (compiled, _) = Count($"{kinds} kinds, schema", schema, c.Facts);
            Assert.True(compiled.Saturated);
            Assert.Equal(kinds + 1, compiled.CasesOpened);
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
            schema.Freeze();

            using var query = schema.CreateChild(c.Query);
            var any = named.Skip(1).Aggregate<string, Term<Pred>>(c.StrEq.Of("kind", named[0]), (or, kind) => c.Or.Of(or, c.StrEq.Of("kind", kind)));
            var q = query.Make(c.And.Of(any, c.UuidEq.Of("ref", Ref)));
            query.Assume(c.WhereClause.Of(q));
            var (report, total) = Count($"{kinds} kinds, query", query, c.Saturation);
            Assert.True(report.Saturated);
            Assert.True(total.CatchUp > 0, "The query's cases caught up with no row.");
            Assert.Equal(expected, total);
            Assert.Equal(kinds, report.CasesOpened);
            Assert.True(query.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.True(query.AreEqual(q, c.And.Of(any, c.StrEq.Of("ref", Ref))));
        }

        /// <summary>
        /// Runs <paramref name="schedule"/> in <paramref name="scope"/> with a <see cref="SplitCountObserver"/>, prints
        /// its split steps' counts and their sums, and checks that the steps' counts add up to the observer's total.
        /// </summary>
        /// <param name="name">The run's name, for the output.</param>
        /// <param name="scope">The writable scope the run splits.</param>
        /// <param name="schedule">The schedule.</param>
        /// <returns>The run's report, and the rows its cases examined in catch-ups, scans and indexes, in all.</returns>
        (RunReport Report, (long CatchUp, long Scanned, long Indexed) Total) Count(string name, Scope scope, Schedule schedule)
        {
            var observer = new SplitCountObserver(scope);
            var report = scope.Run(schedule, new RunOptions { Observer = observer.Observe });
            var steps = observer.Steps;
            var total = new SplitStepCounts(-1, steps.Count == 0 ? 0 : steps.Max(s => s.Cases), steps.Sum(s => s.CatchUp), steps.Sum(s => s.Scanned), steps.Sum(s => s.Indexed));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: {report.CasesOpened} cases opened, {steps.Count} split steps with cases; all steps: catch-up {total.CatchUp}, scanned {total.Scanned}, indexed {total.Indexed}, catch-up share {total.CatchUpShare:P1}"));
            foreach (var step in steps)
                output.WriteLine("  " + step);

            Assert.All(steps, s => Assert.True(s.CatchUp >= 0 && s.Scanned >= 0 && s.Indexed >= 0));
            Assert.Equal(observer.Total(), (total.CatchUp, total.Scanned, total.Indexed));
            if (report.CasesOpened > 0)
                Assert.True(total.Scanned + total.Indexed > 0, "The cases' rule runs examined no row.");

            return (report, (total.CatchUp, total.Scanned, total.Indexed));
        }

    }

}
