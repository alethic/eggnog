using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probes for colors M10.4 (colored-contexts spec §11.2, §12.6 I32-I33, §12.7 C4, §19 item 9; rule-layer E8): the
    /// lift's memo across differing options, the validation of <see cref="LiftTableKind.Relation"/>, the cancellation
    /// token between rounds, C4 on a corrupt cached member map, and <c>RestampedCopies == 0</c> over lift and split
    /// workloads.
    /// </summary>
    public class LiftCompletenessReviewTests
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
        /// The memo is keyed on versions only, so a lift whose candidate limit skipped a row, followed by a lift over the
        /// same unchanged cases with a limit that admits the row, returns at once and never lifts it. A lift that skipped
        /// candidates has not settled any more than one cut short by its round limit (which the memo already leaves out).
        /// </summary>
        [Fact]
        public void ALiftWithAWiderCandidateLimitIsNotSkippedByTheMemo()
        {
            var (g, ids) = Setup(5);
            var (a, b, c, d, e) = (ids[0], ids[1], ids[2], ids[3], ids[4]);
            using var set = new CaseSet(g.Root);

            // The seed (first of two one-group cases) holds edge(a, d) with a standing for {a, b, c}: three parent keys.
            var one = set.AddCase();
            one.StageUnion(a, b);
            one.StageUnion(b, c);
            g.Assert(one, g.Edge, c, d);
            var two = set.AddCase();
            two.StageUnion(d, e);
            g.Assert(two, g.Edge, a, d);

            var narrow = set.Lift(Spec(g), new LiftOptions { CandidateLimit = 2 });
            Assert.Equal(3, narrow.Skipped);
            Assert.False(g.Root.ContainsKey(g.Edge, [a, d]));

            var wide = set.Lift(Spec(g), new LiftOptions { CandidateLimit = 64 });
            Assert.True(g.Root.ContainsKey(g.Edge, [a, d]), "edge(a, d) holds in both cases and fits the wider limit, but the memo skipped the lift");
            Assert.True(wide.Changed);
        }

        /// <summary>
        /// <see cref="LiftTableKind.Relation"/> writes the seed's output unchecked. Listed for a constructor, whose output in
        /// each case is a term minted there, it writes a case-local id into the parent (I10: local ids appear in no ancestor
        /// row). Either the kind is refused for a function whose output is not the unit, or what reaches the parent must
        /// name no case-local id.
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
                    Assert.False(@case.IsLocalId(output), $"the parent's num(7) names id {output.Rep}, local to a case (I10)");

                @case.CheckInvariants();
            }
        }

        /// <summary>
        /// <see cref="LiftTableKind.Relation"/> listed for a function with a base output writes the seed's value although
        /// another live case holds a different one: <c>low(a) = 3</c> reaches the parent while one case has
        /// <c>low(a) = 5</c>, which does not imply it (I33 soundness: a lifted fact holds in every live case). Either the
        /// kind is refused for such a function, or the value written holds in every live case.
        /// </summary>
        [Fact]
        public void ARelationKindNeverLiftsAValueOneLiveCaseDoesNotHold()
        {
            var (g, ids) = Setup(1);
            using var set = new CaseSet(g.Root);
            set.AddCase().StageRow(g.Low, [ids[0], g.Int(3)]);
            set.AddCase().StageRow(g.Low, [ids[0], g.Int(5)]);

            try
            {
                set.Lift(new LiftSpec(new LiftTable(g.Low, LiftTableKind.Relation)), LiftOptions.Default);
            }
            catch (ArgumentException)
            {
                return;
            }

            var lifted = g.Root.LookupId(g.Low, [ids[0]]);
            Assert.True(lifted is null || lifted == g.Int(5), $"low(a) = {(lifted is { } v ? g.EGraph.BaseValues.Unwrap<Int64Codec, long>(v) : 0)} was lifted, but one live case holds low(a) = 5");
        }

        /// <summary>
        /// A function with an id output and egglog's default merge for custom functions (<see cref="MergeFn.AssertEq"/>, a
        /// lattice merge here, so cases may write it). The parent holds <c>f(k) = e</c>; every case unions <c>e</c> with the
        /// smaller <c>m</c>, so every case's <c>f(k)</c> is <c>m</c>. The fact <c>f(k) = e</c> already holds in the parent, and
        /// <c>m = e</c> is the meet's equality: the lift must write that union, and must not fail. Writing <c>f(k) = m</c>
        /// next to the union, in the same flush, makes the assert-eq merge compare <c>e</c> with <c>m</c> before the union
        /// is applied.
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

        /// <summary>
        /// A lift into a writable child of a writable root (the parent's ancestor is not frozen, so the memo also keys on
        /// it). After a first lift, the root learns <c>edge(t, c)</c> and <c>edge(f, c)</c>; the boolean cases of <c>b</c>
        /// see them through two levels (the winner probe reaches the root's rows) and the second lift writes
        /// <c>edge(b, c)</c> into the scope, not into the root.
        /// </summary>
        [Fact]
        public void ALiftUnderAWritableAncestorSeesTheAncestorsNewFacts()
        {
            var (g, ids) = Setup(4);
            var (t, f, c, b) = (ids[0], ids[1], ids[2], ids[3]);
            using var scope = g.Root.CreateChild();
            using var set = new CaseSet(scope);
            set.AddCase().StageUnion(b, t);
            set.AddCase().StageUnion(b, f);

            var first = set.Lift(Spec(g), LiftOptions.Default);
            Assert.False(first.Changed);

            g.Assert(g.Root, g.Edge, t, c);
            g.Assert(g.Root, g.Edge, f, c);
            g.Root.FlushUpdates();

            var second = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(second.Changed);
            Assert.True(scope.ContainsKey(g.Edge, [b, c]));
            Assert.False(g.Root.ContainsKey(g.Edge, [b, c]));
            scope.CheckInvariants();
            foreach (var @case in set.Cases)
                @case.CheckInvariants();
        }

        /// <summary>
        /// A case disposed outside the set (not found vacuous by it) leaves the cases' coverage: <c>b</c> is split over
        /// <c>{t, f}</c> and the case <c>b = f</c> is disposed by its owner, not because it is contradictory. The set must not
        /// lift <c>b = t</c> from the one remaining case as if the disposed one had been vacuous (L4 drops only vacuous
        /// cases); refusing the lift is the sound answer.
        /// </summary>
        [Fact]
        public void ACaseDisposedOutsideTheSetIsNotTreatedAsVacuous()
        {
            var (g, ids) = Setup(3);
            var (t, f, b) = (ids[0], ids[1], ids[2]);
            using var set = new CaseSet(g.Root);
            set.AddCase().StageUnion(b, t);
            var other = set.AddCase();
            other.StageUnion(b, f);
            other.Dispose();

            try
            {
                set.Lift(Spec(g), LiftOptions.Default);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            Assert.True(g.Root.Find(b) != g.Root.Find(t), "b = t was lifted from one case of two; the other was disposed, not found vacuous");
        }

        /// <summary>
        /// L4, §11.2: a lift whose cases are all vacuous reports <see cref="LiftReport.ParentContradicted"/>. A second lift
        /// over the same unchanged set hits the memo; its report must still say what holds (no live case, the parent
        /// contradicted), not <c>ParentContradicted = false</c> with zero live cases.
        /// </summary>
        [Fact]
        public void AMemoizedAllVacuousLiftStillReportsTheContradiction()
        {
            var (g, _) = Setup(1);
            using var set = new CaseSet(g.Root);
            set.AddCase().StageRow(g.Bottom, [g.Unit]);
            set.AddCase().StageRow(g.Bottom, [g.Unit]);

            var first = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(first.ParentContradicted);
            var again = set.Lift(Spec(g), LiftOptions.Default);
            Assert.Equal(0, again.LiveCases);
            Assert.True(again.ParentContradicted, "every case is vacuous and the parent is contradictory, but the memoized lift reports ParentContradicted = false");
        }

        /// <summary>
        /// Edge case 33 (§13) through the bridge's lift reads (<c>EGraphContext.Lift.cs</c>): <c>IsLocalId</c>,
        /// <c>ScanMerges</c>, <c>ScanOwn</c>, <c>ScanAncestorsMentioning</c> and the member map are refused at the root.
        /// </summary>
        [Fact]
        public void TheLiftReadsAreRefusedAtTheRoot()
        {
            var (g, ids) = Setup(1);
            g.Assert(g.Root, g.Mark, ids[0]);
            g.Root.FlushUpdates();
            Assert.Throws<InvalidOperationException>(() => g.Root.IsLocalId(ids[0]));
            Assert.Throws<InvalidOperationException>(() =>
            {
                var sink = new RowCountSink();
                g.Root.ScanOwn(g.Mark, ref sink);
            });
            Assert.Throws<InvalidOperationException>(() =>
            {
                var sink = new RowCountSink();
                g.Root.ScanAncestorsMentioning(g.Mark, ids[0].Rep, ref sink);
            });
            Assert.Throws<InvalidOperationException>(() =>
            {
                var visitor = new MergeCollector();
                g.Root.ScanMerges(ref visitor);
            });
            Assert.Throws<InvalidOperationException>(() => g.Root.GetMemberMap());
        }

        /// <summary>
        /// rule-layer E8: the lift checks its token between rounds, not only before the first. A case join cancels the
        /// token during round 1 of a lift that needs two rounds (terms bottom-up); round 2 must not run.
        /// </summary>
        [Fact]
        public void TheTokenIsCheckedBetweenRounds()
        {
            var (g, ids) = Setup(1);
            using var cancellation = new CancellationTokenSource();
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                var one = @case.AddTerm(g.Num, [g.Int(1)]);
                @case.AddTerm(g.Add, [one, one]);
                @case.StageRow(g.Low, [ids[0], g.Int(i)]);
            }

            var spec = new LiftSpec(
                new LiftTable(g.Num, LiftTableKind.Constructor),
                new LiftTable(g.Add, LiftTableKind.Constructor),
                new LiftTable(g.Low, LiftTableKind.Function, (a, b) =>
                {
                    cancellation.Cancel();
                    return Max(g, a, b);
                }));

            Assert.Throws<OperationCanceledException>(() => set.Lift(spec, LiftOptions.Default, cancellation.Token));
            Assert.NotNull(g.Root.LookupId(g.Num, [g.Int(1)]));
            Assert.Equal(g.Int(1), g.Root.LookupId(g.Low, [ids[0]]));
            Assert.Equal(0, g.Root.TableSize(g.Add));
            Assert.False(g.Root.IsFaulted);
            foreach (var @case in set.Cases)
            {
                @case.EnsureClean();
                @case.CheckInvariants();
            }
        }

        /// <summary>
        /// C4 (§12.7, I32, X29): a cached member map whose version is current but whose groups differ from the overlay's is
        /// reported. The corrupt map is another case's map at the same overlay version.
        /// </summary>
        [Fact]
        public void C4ReportsACachedMemberMapThatDiffersFromTheOverlay()
        {
            var (g, ids) = Setup(4);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            one.StageUnion(ids[0], ids[1]);
            var two = set.AddCase();
            two.StageUnion(ids[2], ids[3]);
            one.EnsureClean();
            two.EnsureClean();
            var mapOne = one.GetMemberMap();
            var mapTwo = two.GetMemberMap();
            Assert.Equal(mapOne.Version, mapTwo.Version);
            one.CheckInvariants();

            var field = typeof(EGraphContext).GetField("memberMap", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(one, mapTwo);
            var thrown = Assert.Throws<ContextInvariantException>(() => one.CheckInvariants());
            Assert.Contains("C4", thrown.Message);
        }

        /// <summary>
        /// §19 item 9 (§21.10: "M10.4, once its workloads (the lift) have run under the same assertion"): random lifts over
        /// writable roots and writable children of frozen roots, with cases that union, assert, mint terms, lower lattice
        /// values, run rules and go vacuous, never re-stamp a catch-up copy, in the parent or in any case.
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
        /// §19 item 9 over split workloads: nested split steps (lift before deepening, phase B lifts) under a writable root
        /// and under a writable child of a frozen root never re-stamp a catch-up copy in any context they touch.
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
        /// The re-stamped catch-up copies of <paramref name="context"/> (0 at the root and for a context with no tables yet).
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
