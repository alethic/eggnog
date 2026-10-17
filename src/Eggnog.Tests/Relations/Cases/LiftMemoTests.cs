using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The lift's memo. A lift whose first round finds the signature of the last settled lift (the versions of the
    /// parent, of its ancestors that are not frozen and of each case, and the spec, compared by content) returns at
    /// once. A lift that skipped candidates in its last round, or stopped at its round limit, is not memoized, so a
    /// wider limit lifts what a narrower one skipped; a lift with another spec is no memo hit; a memoized all-vacuous
    /// lift still reports the parent contradicted; and a lift that writes nothing leaves the root's clock and version
    /// alone. A memo answer is told from a lift that ran and wrote nothing by <see cref="LiftReport.Memoized"/>, which
    /// the tests assert on every lift.
    /// </summary>
    public class LiftMemoTests
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
        /// <see cref="LiftSpec"/> each time) still skips a lift over unchanged cases, while a spec with another table
        /// is lifted in full.
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
            Assert.False(first.Memoized);
            Assert.True(g.Root.ContainsKey(g.Mark, [ids[0]]));

            var again = set.Lift(MarkSpec(g), LiftOptions.Default);
            Assert.False(again.Changed);
            Assert.Equal(0, again.Rounds);
            Assert.True(again.Memoized);

            // Another spec reads other rows: not a memo hit, though nothing changed.
            var other = set.Lift(new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation), new LiftTable(g.Edge, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.True(other.Changed);
            Assert.False(other.Memoized);
            Assert.True(g.Root.ContainsKey(g.Edge, [ids[0], ids[1]]));
            Assert.True(MarkSpec(g).HasSameTables(MarkSpec(g)));
            Assert.False(MarkSpec(g).HasSameTables(new LiftSpec(new LiftTable(g.Edge, LiftTableKind.Relation))));
        }

        /// <summary>
        /// The memo answers exactly while nothing the settled lift read has changed: a repeat is answered; a fact a case
        /// learns, or a write to the parent, makes the next lift run; and its repeat is answered again.
        /// </summary>
        [Fact]
        public void TheMemoAnswersOnlyWhileNothingTheLiftReadChanges()
        {
            var (g, ids) = Setup(4);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            foreach (var @case in new[] { one, two })
                g.Assert(@case, g.Mark, ids[0]);

            var spec = EdgeAndMarkSpec(g);
            Assert.False(set.Lift(spec, LiftOptions.Default).Memoized);
            Assert.True(set.Lift(spec, LiftOptions.Default).Memoized);

            // A case learns a fact: the next lift runs, and writes nothing, since the other case lacks it.
            g.Assert(one, g.Edge, ids[1], ids[2]);
            var afterCase = set.Lift(spec, LiftOptions.Default);
            Assert.False(afterCase.Memoized);
            Assert.False(afterCase.Changed);
            Assert.True(set.Lift(spec, LiftOptions.Default).Memoized);

            // The parent learns a fact: its version moved, so the next lift runs.
            g.Assert(g.Root, g.Edge, ids[2], ids[3]);
            g.Root.FlushUpdates();
            var afterParent = set.Lift(spec, LiftOptions.Default);
            Assert.False(afterParent.Memoized);
            Assert.False(afterParent.Changed);
            Assert.True(set.Lift(spec, LiftOptions.Default).Memoized);
        }

        /// <summary>
        /// The memo is kept after a lift whose earlier round skipped a row over the candidate limit but whose last
        /// round skipped nothing: that lift reached a state no limit can extend, so a later lift with a wider limit
        /// over the same cases is a memo hit and must find nothing new either.
        /// </summary>
        [Fact]
        public void ALiftThatSkippedOnlyInAnEarlierRoundSettlesCompletely()
        {
            var (g, ids) = Setup(4);
            var (a, b, c, d) = (ids[0], ids[1], ids[2], ids[3]);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            foreach (var @case in new[] { one, two })
            {
                @case.StageUnion(a, b);
                @case.StageUnion(b, c);
                g.Assert(@case, g.Edge, a, d);
            }

            var first = set.Lift(new LiftSpec(new LiftTable(g.Edge, LiftTableKind.Relation)), new LiftOptions { CandidateLimit = 2 });
            var second = set.Lift(new LiftSpec(new LiftTable(g.Edge, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.True(g.Root.ContainsKey(g.Edge, [a, d]), $"edge(a, d) holds in both cases (first: skipped {first.Skipped}, rounds {first.Rounds}; second: changed {second.Changed}, rounds {second.Rounds})");
            Assert.Equal(g.Root.Find(a), g.Root.Find(c));
            Assert.False(first.Memoized);
            Assert.True(second.Memoized, "the first lift settled, so the second is a memo hit");
        }

        static LiftSpec EdgeSpec(ContextGraph g) => new(new LiftTable(g.Edge, LiftTableKind.Relation));

        static LiftSpec EdgeAndMarkSpec(ContextGraph g) => new(
            new LiftTable(g.Edge, LiftTableKind.Relation),
            new LiftTable(g.Mark, LiftTableKind.Relation));

        /// <summary>
        /// The root departs from egglog only in operations egglog lacks: a lift that finds nothing to write leaves the
        /// root's clock, version and rows as they were, and so does its memoized repeat.
        /// </summary>
        [Fact]
        public void ANoOpLiftLeavesTheRootClockAndVersionAlone()
        {
            var (g, ids) = Setup(3);
            g.Assert(g.Root, g.Edge, ids[0], ids[1]);
            g.Root.FlushUpdates();

            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            g.Assert(one, g.Mark, ids[2]);
            one.EnsureClean();
            two.EnsureClean();

            var clock = g.EGraph.Store.ReadClock();
            var version = g.Root.Version;
            var report = set.Lift(EdgeAndMarkSpec(g), LiftOptions.Default);
            Assert.False(report.Changed);
            Assert.Equal(0, report.Rounds);
            Assert.False(report.Memoized, "the first lift ran, though it wrote nothing");
            Assert.Equal(clock, g.EGraph.Store.ReadClock());
            Assert.Equal(version, g.Root.Version);

            var again = set.Lift(EdgeAndMarkSpec(g), LiftOptions.Default);
            Assert.False(again.Changed);
            Assert.True(again.Memoized);
            Assert.Equal(clock, g.EGraph.Store.ReadClock());
            Assert.Equal(version, g.Root.Version);
        }

        /// <summary>
        /// A lift whose candidate limit skipped a source row is not memoized, since its answer depends on the limit: a
        /// second lift over the unchanged cases with a limit wide enough to take the row lifts that row, a fact that
        /// holds in every live case. Hosts pass their <c>LiftOptions</c> on every <c>CaseSet.Lift</c>.
        /// </summary>
        [Fact]
        public void ARaisedCandidateLimitIsNotSkippedByTheMemo()
        {
            var (g, ids) = Setup(6);
            var (a, b, c, d, e, f) = (ids[0], ids[1], ids[2], ids[3], ids[4], ids[5]);
            using var set = new CaseSet(g.Root);

            // The seed (first, one group) merges a, b and c and holds edge(a, d), which stands for three parent keys.
            var one = set.AddCase();
            one.StageUnion(a, b);
            one.StageUnion(b, c);
            g.Assert(one, g.Edge, c, d);

            // The other case holds edge(a, d) without those equalities, and one group of its own.
            var two = set.AddCase();
            g.Assert(two, g.Edge, a, d);
            two.StageUnion(e, f);
            one.EnsureClean();
            two.EnsureClean();

            var narrow = set.Lift(EdgeSpec(g), new LiftOptions { CandidateLimit = 2 });
            Assert.Equal(3, narrow.Skipped);
            Assert.False(narrow.Memoized);
            Assert.False(g.Root.ContainsKey(g.Edge, [a, d]));

            // edge(a, d) holds in every live case, and the wider limit admits it.
            var wide = set.Lift(EdgeSpec(g), new LiftOptions { CandidateLimit = 64 });
            Assert.True(g.Root.ContainsKey(g.Edge, [a, d]), $"edge(a, d) holds in every live case but was not lifted (Changed = {wide.Changed}, Rounds = {wide.Rounds}, Skipped = {wide.Skipped})");
            Assert.False(wide.Memoized);
        }

        /// <summary>
        /// The memo is keyed on the spec: after a lift of <c>edge</c> settles, a lift of <c>edge</c> and <c>mark</c>
        /// over the unchanged cases is no memo hit, and it lifts a <c>mark</c> fact that holds in every live case.
        /// <c>CaseSet.Lift</c> takes the spec on every call.
        /// </summary>
        [Fact]
        public void AWiderSpecIsNotSkippedByTheMemo()
        {
            var (g, ids) = Setup(3);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            g.Assert(one, g.Edge, ids[0], ids[1]);
            g.Assert(two, g.Edge, ids[0], ids[1]);
            g.Assert(one, g.Mark, ids[2]);
            g.Assert(two, g.Mark, ids[2]);

            var first = set.Lift(EdgeSpec(g), LiftOptions.Default);
            Assert.True(first.Changed);
            Assert.False(first.Memoized);
            Assert.True(g.Root.ContainsKey(g.Edge, [ids[0], ids[1]]));

            var wider = set.Lift(EdgeAndMarkSpec(g), LiftOptions.Default);
            Assert.True(g.Root.ContainsKey(g.Mark, [ids[2]]), $"mark(c) holds in every live case but was not lifted (Changed = {wider.Changed}, Rounds = {wider.Rounds})");
            Assert.False(wider.Memoized);
        }

        /// <summary>
        /// The memo does not skip a lift whose spec differs from the one memoized: <c>mark(a)</c> holds in every case,
        /// and the second lift lists <c>mark</c> for the first time (the rule layer rebuilds the spec when a table is
        /// defined, and host-driven lifts pass it on every call).
        /// </summary>
        [Fact]
        public void TheMemoDoesNotSkipALiftWithAnotherSpec()
        {
            var (g, ids) = Setup(2);
            var (a, b) = (ids[0], ids[1]);
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                g.Assert(@case, g.Mark, a);
                g.Assert(@case, g.Edge, a, b);
            }

            var first = set.Lift(new LiftSpec(new LiftTable(g.Edge, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.False(first.Memoized);
            Assert.True(g.Root.ContainsKey(g.Edge, [a, b]));

            var second = set.Lift(new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.True(g.Root.ContainsKey(g.Mark, [a]), "mark(a) holds in every case, but the memo skipped the lift that lists mark");
            Assert.False(second.Memoized);
        }

        /// <summary>
        /// The memo of a case set must not skip a lift whose candidate limit is wider than the one memoized: the first
        /// lift skips <c>mark(a)</c> (two candidates over a limit of one) and reports it in
        /// <see cref="LiftReport.Skipped"/>; the second, with the default limit, must lift it.
        /// </summary>
        [Fact]
        public void TheMemoDoesNotSkipALiftWithAWiderCandidateLimit()
        {
            var (g, ids) = Setup(3);
            var (a, b, c) = (ids[0], ids[1], ids[2]);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            one.StageUnion(a, b);
            g.Assert(one, g.Mark, a);
            var two = set.AddCase();
            two.StageUnion(a, c);
            g.Assert(two, g.Mark, a);
            var spec = new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation));

            var narrow = set.Lift(spec, new LiftOptions { CandidateLimit = 1 });
            Assert.True(narrow.Skipped > 0);
            Assert.False(narrow.Memoized);
            Assert.False(g.Root.ContainsKey(g.Mark, [a]));

            var wide = set.Lift(spec, LiftOptions.Default);
            Assert.True(g.Root.ContainsKey(g.Mark, [a]), $"mark(a) holds in every case, but the memo skipped the wider lift (Changed = {wide.Changed})");
            Assert.False(wide.Memoized);
        }

        /// <summary>
        /// A second lift over the same all-vacuous cases still reports the parent contradicted, as the first did (the
        /// split driver ends its step on <see cref="LiftReport.ParentContradicted"/>).
        /// </summary>
        [Fact]
        public void AMemoizedAllVacuousLiftStillReportsTheContradiction()
        {
            var (g, _) = Setup(1);
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
                set.AddCase().StageRow(g.Bottom, [g.Unit]);

            var spec = new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation));
            var first = set.Lift(spec, LiftOptions.Default);
            Assert.True(first.ParentContradicted);
            Assert.False(first.Memoized);
            var second = set.Lift(spec, LiftOptions.Default);
            Assert.True(second.Memoized);
            Assert.Equal(0, second.LiveCases);
            Assert.True(second.ParentContradicted, "every case is vacuous, but the memoized lift reports the parent not contradicted");
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
        /// A lift whose candidate limit skipped a row, then a lift over the same unchanged cases with a limit that
        /// admits the row: the second lift is no memo hit, and lifts the row. A lift that skipped candidates has not
        /// settled, any more than one cut short by its round limit.
        /// </summary>
        [Fact]
        public void ALiftWithAWiderCandidateLimitIsNotSkippedByTheMemo()
        {
            var (g, ids) = Setup(5);
            var (a, b, c, d, e) = (ids[0], ids[1], ids[2], ids[3], ids[4]);
            using var set = new CaseSet(g.Root);

            // The seed (first of two one-group cases) holds edge(a, d) with a standing for {a, b, c}: three parent
            // keys.
            var one = set.AddCase();
            one.StageUnion(a, b);
            one.StageUnion(b, c);
            g.Assert(one, g.Edge, c, d);
            var two = set.AddCase();
            two.StageUnion(d, e);
            g.Assert(two, g.Edge, a, d);

            var narrow = set.Lift(Spec(g), new LiftOptions { CandidateLimit = 2 });
            Assert.Equal(3, narrow.Skipped);
            Assert.False(narrow.Memoized);
            Assert.False(g.Root.ContainsKey(g.Edge, [a, d]));

            var wide = set.Lift(Spec(g), new LiftOptions { CandidateLimit = 64 });
            Assert.True(g.Root.ContainsKey(g.Edge, [a, d]), "edge(a, d) holds in both cases and fits the wider limit, but the memo skipped the lift");
            Assert.True(wide.Changed);
            Assert.False(wide.Memoized);
        }

        /// <summary>
        /// A lift into a writable child of a writable root (the parent's ancestor is not frozen, so the memo also keys
        /// on it). After a first lift, the root learns <c>edge(t, c)</c> and <c>edge(f, c)</c>; the boolean cases of
        /// <c>b</c> see them through two levels (the winner probe reaches the root's rows) and the second lift writes
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
            Assert.False(first.Memoized);

            g.Assert(g.Root, g.Edge, t, c);
            g.Assert(g.Root, g.Edge, f, c);
            g.Root.FlushUpdates();

            var second = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(second.Changed);
            Assert.False(second.Memoized, "the root, an ancestor that is not frozen, changed since the first lift");
            Assert.True(scope.ContainsKey(g.Edge, [b, c]));
            Assert.False(g.Root.ContainsKey(g.Edge, [b, c]));
            scope.CheckInvariants();
            foreach (var @case in set.Cases)
                @case.CheckInvariants();
        }

    }

}
