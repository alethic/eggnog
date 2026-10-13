using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The M10.4 fixes (colored-contexts spec §11.2, §11.3, §19 item 9): the memo keyed on the spec's content, the
    /// refusals of <see cref="LiftTableKind.Relation"/> and of misplaced case joins, an arena reset that costs what it
    /// held, a split step that revisits live records and still settles, and the raw-write exemption of the re-stamp
    /// assertion.
    /// </summary>
    public class LiftFixTests
    {

        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        static LiftSpec MarkSpec(ContextGraph g) => new(new LiftTable(g.Mark, LiftTableKind.Relation));

        /// <summary>
        /// The memo compares specs by content: a host that builds an equal spec for every call (a new
        /// <see cref="LiftSpec"/> each time) still skips a lift over unchanged cases, while a spec with another table is
        /// lifted in full.
        /// </summary>
        [Fact]
        public void AnEqualSpecBuiltAgainHitsTheMemo()
        {
            var (g, ids) = Setup(2);
            using var set = new CaseSet(g.Root);
            foreach (var @case in new[] { set.AddCase(), set.AddCase() })
            {
                g.Assert(@case, g.Mark, ids[0]);
                g.Assert(@case, g.Edge, ids[0], ids[1]);
            }

            var first = set.Lift(MarkSpec(g), LiftOptions.Default);
            Assert.True(first.Changed);
            Assert.True(g.Root.ContainsKey(g.Mark, [ids[0]]));

            var again = set.Lift(MarkSpec(g), LiftOptions.Default);
            Assert.False(again.Changed);
            Assert.Equal(0, again.Rounds);

            // Another spec reads other rows: not a memo hit, though nothing changed.
            var other = set.Lift(new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation), new LiftTable(g.Edge, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.True(other.Changed);
            Assert.True(g.Root.ContainsKey(g.Edge, [ids[0], ids[1]]));
            Assert.True(MarkSpec(g).HasSameTables(MarkSpec(g)));
            Assert.False(MarkSpec(g).HasSameTables(new LiftSpec(new LiftTable(g.Edge, LiftTableKind.Relation))));
        }

        /// <summary>
        /// A case join applies to <see cref="LiftTableKind.Function"/> only, and a relation with an id output is refused
        /// (its seed output could be local to the seed, I10): both before anything is written.
        /// </summary>
        [Fact]
        public void MisplacedJoinsAndIdRelationsAreRefused()
        {
            var (g, ids) = Setup(1);
            using var set = new CaseSet(g.Root);
            set.AddCase().AddTerm(g.Num, [g.Int(7)]);
            var version = g.Root.Version;

            Assert.Throws<ArgumentException>(() => set.Lift(new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation, (a, _) => a)), LiftOptions.Default));
            Assert.Throws<ArgumentException>(() => set.Lift(new LiftSpec(new LiftTable(g.Num, LiftTableKind.Relation)), LiftOptions.Default));
            Assert.Equal(version, g.Root.Version);
            _ = ids;
        }

        /// <summary>
        /// A relation-kind table with a base output lifts a key only where every live case holds the same output.
        /// </summary>
        [Fact]
        public void ARelationKindLiftsABaseOutputTheCasesAgreeOn()
        {
            var (g, ids) = Setup(2);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            one.StageRow(g.Low, [ids[0], g.Int(4)]);
            two.StageRow(g.Low, [ids[0], g.Int(4)]);
            one.StageRow(g.Low, [ids[1], g.Int(1)]);
            two.StageRow(g.Low, [ids[1], g.Int(2)]);

            set.Lift(new LiftSpec(new LiftTable(g.Low, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.Equal<Value?>(g.Int(4), g.Root.LookupId(g.Low, [ids[0]]));
            Assert.Null(g.Root.LookupId(g.Low, [ids[1]]));
        }

        /// <summary>
        /// <see cref="LiftCandidates.Reset"/> clears only the slots the arena used once its slot table is large: after a
        /// large table, a small one deduplicates correctly and keeps none of the large one's keys.
        /// </summary>
        [Fact]
        public void ACandidateArenaResetAfterALargeTableKeepsNoStaleSlots()
        {
            var arena = new LiftCandidates();
            arena.Reset(2, 1);
            for (var i = 0u; i < 10_000; i++)
                Assert.True(arena.Add([i, i + 1]));

            Assert.False(arena.Add([5u, 0u]));

            arena.Reset(2, 1);
            Assert.Equal(0, arena.Count);
            Assert.True(arena.Add([5u, 1u]));
            Assert.True(arena.Add([9_999u, 1u]));
            Assert.False(arena.Add([5u, 2u]));
            Assert.Equal(2, arena.Count);

            // Few entries in a large table: the next reset clears them one by one.
            arena.Reset(3, 2);
            for (var i = 0u; i < 10_000; i += 1000)
                Assert.True(arena.Add([i, 7u, 0u]));

            Assert.False(arena.Add([3000u, 7u, 1u]));
            Assert.True(arena.Add([3000u, 8u, 1u]));
            arena.Reset(3, 2);
            for (var i = 0u; i < 10_000; i += 1000)
                Assert.True(arena.Add([i, 7u, 0u]), $"key ({i}, 7) was left behind by the last reset");

            Assert.Equal(10, arena.Count);
            Assert.Equal(new uint[] { 9000u, 7u, 0u }, arena[9].ToArray());
        }

        /// <summary>
        /// A split step revisits the scope's live records (§11.3 step 6), and a step that finds nothing new reports no
        /// change, so a schedule that repeats the step until nothing changes ends; the memo keeps the revisit's lift free.
        /// </summary>
        [Fact]
        public void ARevisitThatFindsNothingNewReportsNoChange()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var (t, f, b) = (g.Root.FreshId(), g.Root.FreshId(), g.Root.FreshId());
            g.Root.FlushUpdates();
            var source = new TestSplitSource(0, _ => [([b.Rep], 2)], (@case, i, _) =>
            {
                @case.StageUnion(b, i == 0 ? t : f);
                g.Assert(@case, g.Mark, t);
            });

            var schedule = new RecordingSchedule();
            var driver = new SplitDriver([source], schedule, null, MarkSpec(g), SplitOptions.Default);
            try
            {
                Assert.True(driver.Step(g.Root, 0, new SplitReport()));
                Assert.True(g.Root.ContainsKey(g.Mark, [t]));
                var calls = schedule.Calls.Count;

                var report = new SplitReport();
                Assert.False(driver.Step(g.Root, 0, report));
                Assert.Equal(0, report.CasesOpened);
                Assert.Equal(1, report.SplitsSkipped);
                Assert.True(report.Lifts >= 1, "the live record is lifted again");
                Assert.True(schedule.Calls.Count > calls, "the live record's cases run again");
                Assert.False(driver.Step(g.Root, 0, new SplitReport()));
            }
            finally
            {
                SplitDriver.DisposeRecords(g.Root);
            }
        }

#if DEBUG
        /// <summary>
        /// §19 item 9: the re-stamp assertion exempts only stores written through the raw execution-state API; host writes,
        /// rule runs, lifts and catch-up never mark a store.
        /// </summary>
        [Fact]
        public void OnlyTheRawApiMarksAStoreAsWrittenRaw()
        {
            var (g, ids) = Setup(2);
            var store = g.EGraph.Store;
            g.Assert(g.Root, g.Edge, ids[0], ids[1]);
            g.Root.FlushUpdates();
            g.Root.Saturate([g.EdgeToPath()], 4);
            using (var set = new CaseSet(g.Root))
            {
                var @case = set.AddCase();
                @case.StageUnion(ids[0], ids[1]);
                g.Assert(@case, g.Mark, ids[0]);
                @case.EnsureClean();
                set.Lift(MarkSpec(g), LiftOptions.Default);
            }

            Assert.False(store.HasRawWrites);
            store.Root.Database.WithExecutionState(_ => 0);
            Assert.True(store.HasRawWrites);
        }
#endif

    }

}
