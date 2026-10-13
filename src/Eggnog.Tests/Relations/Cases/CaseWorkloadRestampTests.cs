using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <c>RestampedCopies == 0</c> over random case workloads: lifts over writable roots and writable children of
    /// frozen roots with cases that union, assert, mint terms, lower lattice values, run rules and go vacuous, and
    /// nested split steps, never re-stamp a catch-up copy in any context they touch.
    /// </summary>
    public class CaseWorkloadRestampTests
    {

        /// <summary>
        /// A graph whose contradiction function is designated, with <paramref name="count"/> root ids.
        /// </summary>
        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        static LiftSpec Spec(ContextGraph g) => new(
            new LiftTable(g.Edge, LiftTableKind.Relation),
            new LiftTable(g.Path, LiftTableKind.Relation),
            new LiftTable(g.Mark, LiftTableKind.Relation),
            new LiftTable(g.Num, LiftTableKind.Constructor),
            new LiftTable(g.Add, LiftTableKind.Constructor),
            new LiftTable(g.Low, LiftTableKind.Function, (a, b) => Max(g, a, b)));

        static uint Max(ContextGraph g, uint a, uint b)
        {
            var values = g.EGraph.BaseValues;
            return values.Get<Int64Codec, long>(Math.Max(values.Unwrap<Int64Codec, long>(new Value(a)), values.Unwrap<Int64Codec, long>(new Value(b)))).Rep;
        }

        /// <summary>
        /// Random lifts over writable roots and writable children of frozen roots, with cases that union, assert, mint
        /// terms, lower lattice values, run rules and go vacuous, never re-stamp a catch-up copy, in the parent or in
        /// any case.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void LiftWorkloadsNeverRestampACopy(int batch)
        {
            for (var seed = batch * 25; seed < (batch + 1) * 25; seed++)
            {
                try
                {
                    RunRestampSeed(seed);
                }
                catch (Exception ex) when (ex is not SeedFailure)
                {
                    throw new SeedFailure(seed, ex);
                }
            }
        }

        /// <summary>
        /// Nested split steps (lift before deepening, phase B lifts) under a writable root and under a writable child
        /// of a frozen root never re-stamp a catch-up copy in any context they touch.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SplitWorkloadsNeverRestampACopy(bool frozenRoot)
        {
            var w = new SplitWorld();
            var g = w.G;
            EGraphContext scope = g.Root;
            if (frozenRoot)
            {
                g.Root.Freeze();
                scope = g.Root.CreateChild();
            }

            var seen = new List<EGraphContext> { scope };
            SplitDriver? driver = null;
            var report = new SplitReport();
            var schedule = new RecordingSchedule((@case, depth, allowSplits) =>
            {
                seen.Add(@case);
                var changed = @case.Saturate([w.Copy], 8).Iterations > 1;
                if (allowSplits)
                    changed |= driver!.Step(@case, depth, report);

                Assert.Equal(0, Restamped(@case));
                return changed;
            });
            driver = new SplitDriver([w.BooleanOfB(0), w.SplitOfD(1)], schedule, null, w.Spec, new SplitOptions { DepthLimit = 3 });
            try
            {
                Assert.True(driver.Step(scope, 0, report));
                Assert.True(report.Lifts >= 1);
                foreach (var context in seen)
                    if (context.IsDisposed == false)
                        Assert.Equal(0, Restamped(context));
            }
            finally
            {
                SplitDriver.DisposeRecords(scope);
                if (frozenRoot)
                    scope.Dispose();
            }
        }

        /// <summary>
        /// The re-stamped catch-up copies of <paramref name="context"/> (0 at the root and for a context with no tables
        /// yet).
        /// </summary>
        static long Restamped(EGraphContext context) => context.Context.Tables?.Statistics.RestampedCopies ?? 0;

        /// <summary>
        /// One seed of <see cref="LiftWorkloadsNeverRestampACopy"/>.
        /// </summary>
        static void RunRestampSeed(int seed)
        {
            var random = new Random(seed);
            var (g, ids) = Setup(5 + random.Next(5));
            var copy = g.EdgeToPath();
            var transitive = g.Transitive();
            Populate(g, g.Root, ids, random, terms: false);
            g.Root.FlushUpdates();
            g.Root.RunRules(copy, transitive);

            using var frozenScope = seed % 2 == 1 ? FreezeAndOpen(g) : null;
            var parent = frozenScope ?? g.Root;
            if (frozenScope is not null)
            {
                Populate(g, parent, ids, random, terms: false);
                parent.FlushUpdates();
                parent.RunRules(copy, transitive);
            }

            using var set = new CaseSet(parent);
            var all = new List<EGraphContext>();
            for (var c = 1 + random.Next(4); c > 0; c--)
            {
                var @case = set.AddCase();
                all.Add(@case);
                var locals = Enumerable.Range(0, random.Next(3)).Select(_ => @case.FreshId()).ToArray();
                Populate(g, @case, ids.Concat(locals).ToArray(), random, terms: true);
                if (random.Next(7) == 0)
                    @case.StageRow(g.Bottom, [g.Unit]);

                @case.EnsureClean();
                if (@case.IsStopped == false && random.Next(2) == 0)
                    @case.Saturate([copy, transitive], 8);
            }

            // The parent changes after the cases were created, so they catch up with appends above their floors.
            Populate(g, parent, ids, random, terms: false);
            parent.FlushUpdates();

            set.Lift(Spec(g), new LiftOptions { CandidateLimit = 4096 });
            foreach (var @case in all)
                Assert.Equal(0, Restamped(@case));

            // The cases run again over the lifted facts, and a second lift settles.
            foreach (var @case in set.Cases)
                if (@case.IsStopped == false)
                    @case.Saturate([copy, transitive], 8);

            set.Lift(Spec(g), new LiftOptions { CandidateLimit = 4096 });
            foreach (var @case in all)
                Assert.Equal(0, Restamped(@case));

            Assert.Equal(0, Restamped(parent));
            set.DisposeVacuous();
        }

        static EGraphContext FreezeAndOpen(ContextGraph g)
        {
            g.Root.Freeze();
            return g.Root.CreateChild();
        }

        static void Populate(ContextGraph g, EGraphContext context, Value[] pool, Random random, bool terms)
        {
            Value Pick() => pool[random.Next(pool.Length)];
            for (var i = random.Next(3); i > 0; i--)
                context.StageUnion(Pick(), Pick());

            for (var i = random.Next(5); i > 0; i--)
                g.Assert(context, g.Edge, Pick(), Pick());

            for (var i = random.Next(3); i > 0; i--)
                g.Assert(context, g.Mark, Pick());

            for (var i = random.Next(3); i > 0; i--)
                context.StageRow(g.Low, [Pick(), g.Int(random.Next(10))]);

            if (terms == false)
                return;

            var made = new List<Value>();
            for (var i = random.Next(3); i > 0; i--)
            {
                var term = context.StageTerm(g.Num, [g.Int(random.Next(4))]);
                made.Add(term);
                if (random.Next(2) == 0)
                    context.StageUnion(term, Pick());
            }

            for (var i = random.Next(2); i > 0 && made.Count > 0; i--)
                context.StageTerm(g.Add, [made[random.Next(made.Count)], random.Next(2) == 0 ? Pick() : made[random.Next(made.Count)]]);
        }

    }

}
