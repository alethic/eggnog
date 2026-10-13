using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Eggnog.Tests.Cosmos.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Probes of R3's soundness (review): split assumptions, host-driven cases, contradicted cases, faults and concurrent
    /// splits. Each probe states what the spec requires.
    /// </summary>
    public sealed class R3SoundnessProbeTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// §6.13: a write that conflicts on a function without a lattice during a run is a <see cref="RuleEvaluationException"/>
        /// and faults the scope (D18). A split's assumption is written during the run (§6.9.2), so its conflict is one too;
        /// either way the run's cases are disposed.
        /// </summary>
        [Fact]
        public void P01_AnAssumptionConflictDuringARunIsAnEvaluationFailure()
        {
            var graph = new EGraph();
            var choice = graph.DefineFunction<string, string>("choice");
            var options = graph.DefineFunction<string, ValueSet<string>>("options", ValueLattice.Intersect<string>());
            var rules = graph.DefineRuleset("rules");
            var split = graph.DefineSplit("by option", s =>
            {
                var (k, d) = s.Vars<string, ValueSet<string>>("k", "d");
                s.When(d.Is(options.Of(k)));
                var v = s.Cases("v", d);
                s.Key(k);
                s.Assume(choice.Of(k).Is(v));
            });

            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(options.Of("x").Is(ValueSet.Of("a", "b")), choice.Of("x").Is("a"));

            var failure = Record.Exception(() => scope.Run(Schedule.Split(split, Schedule.Saturate(rules))));

            Assert.Empty(scope.Children);
            Assert.Equal(0, scope.Engine.Context.SplitRecordCount);
            Assert.IsType<RuleEvaluationException>(failure);
            Assert.True(scope.IsFaulted);
        }

        /// <summary>
        /// A host-driven set with a faulted case (D18). The spec does not say whether a lift may read a faulted case; what it
        /// lifts must at least be sound: the facts both cases hold, nothing the failed iteration would have derived.
        /// </summary>
        [Fact]
        public void P02_ALiftOverAFaultedCaseLiftsOnlyWhatEveryCaseHolds()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("boom", r =>
            {
                var s = r.Var<string>("s");
                r.When(t.Seen.Of(s));
                r.Where(s, _ => throw new InvalidOperationException("boom"));
                r.Then(t.Fact.Of("never"));
            });

            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var failing = cases.Add();
            var other = cases.Add();
            failing.Assume(t.Fact.Of("x"), t.Seen.Of("s"));
            other.Assume(t.Fact.Of("x"), t.Fact.Of("never"));
            Assert.Throws<RuleEvaluationException>(() => failing.Run(Schedule.Saturate(t.Rules)));
            Assert.True(failing.IsFaulted);

            var lifted = cases.Lift();

            Assert.Equal(2, lifted.LiveCases);
            Assert.True(parent.Check(t.Fact.Of("x")));
            Assert.False(parent.Check(t.Fact.Of("never")));
            Assert.False(parent.IsFaulted);
        }

        /// <summary>
        /// D45 says only case scopes stop and any other contradictory scope stays fully usable; colors §6.9 ("children of a
        /// stopped parent") refuses a plain child of a stopped case every catch-up, so it cannot stay usable. The sound
        /// reading closest to D45: the child, which has no model either, answers <c>IsContradictory</c> (true) and refuses
        /// the rest with <see cref="ScopeContradictedException"/>, as its stopped parent does.
        /// </summary>
        [Fact]
        public void P03_APlainChildOfAStoppedCaseAnswersAsAScopeWithNoModel()
        {
            var t = new ScopeTheory();
            t.DefinePaths();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            @case.Assume(t.Fact.Of("a"), t.Edge.Of(1, 2));
            var child = @case.CreateChild();
            Assert.False(child.IsCase);

            @case.Assume(t.Fact.Of("b"), t.Graph.Contradiction.Of(), t.Edge.Of(2, 3));
            Assert.True(@case.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => @case.Check(t.Fact.Of("a")));

            // The engine refuses to catch a plain child up with a stopped parent (it can only be disposed). D45 has every
            // scope answer IsContradictory, and a scope under a stopped case has no model either: the sound reading is the
            // stopped case's own (true, then ScopeContradictedException), never an engine InvalidOperationException.
            Assert.True(child.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => child.Check(t.Fact.Of("a")));
            Assert.Throws<ScopeContradictedException>(() => child.CreateChild());
            Assert.False(child.IsFaulted);
            child.Dispose();
        }

        /// <summary>
        /// D17, D47: children of a frozen scope may run concurrently, and each query's splits are its own: eight queries
        /// under one frozen container run the scoped Saturation (QuerySplit) on eight threads and reach the serial result.
        /// </summary>
        [Fact]
        public void P04_SplitsInSiblingQueriesRunConcurrentlyAndAgreeWithASerialRun()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            schema.Assume(
                c.Present.Of("kind"),
                c.Dom.Of("kind").Is(ValueSet.Of("order", "invoice", "note")),
                c.Discriminator.Of("kind"));
            c.DeclareBranch(schema, "kind", "order", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(schema, "kind", "invoice", c.Form.Of("ref", "uuid-lower"));
            Assert.Equal(3, schema.Run(c.Facts).CasesOpened);
            schema.Freeze();

            string RunOne()
            {
                using var query = schema.CreateChild(c.Query);
                var q = query.Make(c.And.Of(
                    c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")),
                    c.UuidEq.Of("ref", Ref)));
                query.Assume(c.WhereClause.Of(q));
                var report = query.Run(c.Saturation);
                Assert.Equal(RunStopReason.Completed, report.StopReason);
                Assert.Equal(2, report.CasesOpened);
                Assert.Empty(query.Children);
                Assert.True(query.Check(c.Form.Of("ref", "uuid-lower")));
                return query.Extract(q).ToString();
            }

            var serial = RunOne();
            for (var round = 0; round < 4; round++)
            {
                var results = new string[8];
                Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => results[i] = RunOne());
                Assert.All(results, r => Assert.Equal(serial, r));
            }

            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
        }

        /// <summary>
        /// §6.9.3 and §6.9.4 together: a split run in a scope with an open host-driven set leaves the host's cases alone (the
        /// run releases only the cases its splits made), and the host's set still lifts afterwards.
        /// </summary>
        [Fact]
        public void P05_ARunsSplitLeavesAnOpenHostSetAlone()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("a", "b")), c.Discriminator.Of("kind"));
            c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(scope, "kind", "b", c.Form.Of("ref", "uuid-lower"));

            using var hosted = scope.OpenCases();
            var x = hosted.Add();
            var y = hosted.Add();
            x.Assume(c.Present.Of("x"), c.Present.Of("both"));
            y.Assume(c.Present.Of("y"), c.Present.Of("both"));

            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            var report = scope.Run(Schedule.Saturate(settle, Schedule.Split(c.SchemaSplit, settle)));
            Assert.Equal(2, report.CasesOpened);
            Assert.True(scope.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.Equal(2, scope.Children.Count);
            Assert.False(x.IsDisposed);
            Assert.False(y.IsDisposed);

            // The host cases caught up with the split's lift, and still lift what both of them hold.
            Assert.True(x.Check(c.Form.Of("ref", "uuid-lower")));
            var lifted = hosted.Lift();
            Assert.Equal(2, lifted.LiveCases);
            Assert.True(scope.Check(c.Present.Of("both")));
            Assert.False(scope.Check(c.Present.Of("x")));
        }

        /// <summary>
        /// D45 with a stopped case's run report: <c>Run</c> on a stopped case returns at once with
        /// <see cref="RunStopReason.Contradiction"/>, no iteration, and the scope unchanged.
        /// </summary>
        [Fact]
        public void P06_ARunOnAStoppedCaseDoesNothing()
        {
            var t = new ScopeTheory();
            t.DefinePaths();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            @case.Assume(t.Edge.Of(1, 2), t.Graph.Contradiction.Of());
            var version = @case.Version;

            var report = @case.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(RunStopReason.Contradiction, report.StopReason);
            Assert.Equal(0, report.Iterations);
            Assert.False(report.Changed);
            Assert.Equal(version, @case.Version);
        }

        /// <summary>
        /// L4 through a host lift: a case that went vacuous only because its parent later became contradictory is dropped,
        /// and with every case vacuous the parent stays contradictory; nothing else is lifted.
        /// </summary>
        [Fact]
        public void P07_ACaseMadeVacuousByItsParentIsDropped()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var a = cases.Add();
            var b = cases.Add();
            a.Assume(t.Fact.Of("a"), t.Fact.Of("both"));
            b.Assume(t.Fact.Of("b"), t.Fact.Of("both"));
            parent.Assume(t.Graph.Contradiction.Of());

            var lifted = cases.Lift();

            Assert.True(lifted.ParentContradicted);
            Assert.Equal(0, lifted.LiveCases);
            Assert.Empty(cases.Cases);
            Assert.False(parent.Check(t.Fact.Of("both")));
            Assert.True(parent.IsContradictory);
        }

        /// <summary>
        /// A host case under a scope that becomes contradictory after the case was made, with a host set opened in the case
        /// before that. The case has no model from then on (I25: the parent's contradiction row is visible in it).
        /// </summary>
        static (ScopeTheory T, Scope Parent, CaseSet Outer, Scope Case, CaseSet Inner) ContradictedThroughItsParent()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            var outer = parent.OpenCases();
            var @case = outer.Add();
            @case.Assume(t.Fact.Of("a"));
            var inner = @case.OpenCases();
            parent.Assume(t.Graph.Contradiction.Of());
            return (t, parent, outer, @case, inner);
        }

        /// <summary>
        /// D45, §6.6.8: <c>DefineRule</c> on a case with no model throws <see cref="ScopeContradictedException"/>; the
        /// check must follow the case's catch-up, as every other entry point's does.
        /// </summary>
        [Fact]
        public void P08a_DefineRuleOnACaseContradictedThroughItsParentThrows()
        {
            var s = ContradictedThroughItsParent();
            Assert.Throws<ScopeContradictedException>(() => s.Case.DefineRule(s.T.Rules, "late", r =>
            {
                var x = r.Var<string>("x");
                r.When(s.T.Fact.Of(x));
                r.Then(s.T.Seen.Of(x));
            }));
            Assert.Empty(s.Case.LocalRules);
        }

        /// <summary>
        /// D45, §6.6.8, §6.9.4: <c>OpenCases</c> on a case with no model throws <see cref="ScopeContradictedException"/>.
        /// </summary>
        [Fact]
        public void P08b_OpenCasesOnACaseContradictedThroughItsParentThrows()
        {
            var s = ContradictedThroughItsParent();
            Assert.Throws<ScopeContradictedException>(() => s.Case.OpenCases());
        }

        /// <summary>
        /// N101: <c>CaseSet.Add</c> on a stopped parent throws <see cref="ScopeContradictedException"/>, also when the parent
        /// learns it is stopped from the catch-up <c>Add</c> makes.
        /// </summary>
        [Fact]
        public void P08c_AddUnderACaseContradictedThroughItsParentThrows()
        {
            var s = ContradictedThroughItsParent();
            var refused = Record.Exception(() => s.Inner.Add());
            Assert.False(s.Case.IsFaulted);
            Assert.False(s.Case.Engine.Context.IsFaulted);
            Assert.True(s.Case.IsContradictory);
            Assert.IsType<ScopeContradictedException>(refused);
        }

        /// <summary>
        /// §6.9.2: <c>DefineDomainSplit</c> writes <c>domain(k) = {v}</c> in each case, which only a lattice merge can
        /// narrow. Over a domain function without a lattice the assumption conflicts in every case of every split (D32).
        /// Either the definition refuses such a function, or the run's conflict is what §6.13 makes a write conflict during
        /// a run: a <see cref="RuleEvaluationException"/> that faults the scope. As built, the definition is accepted and a
        /// <see cref="FactConflictException"/>, which <c>Run</c> does not document, escapes the run unfaulted.
        /// </summary>
        [Fact]
        public void P09_ADomainSplitOverAFunctionWithoutALatticeIsRefusedOrFaults()
        {
            var graph = new EGraph();
            var key = graph.DefineRelation<string>("key");
            var domain = graph.DefineFunction<string, ValueSet<string>>("domain");
            var rules = graph.DefineRuleset("rules");

            Exception? definition = Record.Exception(() => graph.DefineDomainSplit("by domain", domain, [key]));
            if (definition is not null)
            {
                Assert.True(definition is ArgumentException or RuleDefinitionException, definition.ToString());
                return;
            }

            var split = graph.SplitRules.Single();
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(key.Of("k"), domain.Of("k").Is(ValueSet.Of("a", "b")));

            var failure = Record.Exception(() => scope.Run(Schedule.Split(split, Schedule.Saturate(rules))));

            Assert.Empty(scope.Children);
            Assert.IsType<RuleEvaluationException>(failure);
            Assert.True(scope.IsFaulted);
        }

        /// <summary>
        /// §3.6: <c>RunReport.Changed</c> says an iteration, split step or lift of the run changed the top-level scope. A
        /// split whose cases prove nothing new opens and drops cases, and leaves the scope as it was: its
        /// <see cref="Scope.Version"/> does not move, so the run did not change it.
        /// </summary>
        [Fact]
        public void P10_ASplitThatLiftsNothingDoesNotChangeTheScope()
        {
            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("a", "b")), c.Discriminator.Of("kind"));
            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            scope.Run(settle);
            var version = scope.Version;

            var report = scope.Run(Schedule.Split(c.SchemaSplit, settle));

            Assert.Equal(2, report.CasesOpened);
            Assert.Equal(version, scope.Version);
            Assert.False(report.Changed);
        }

        /// <summary>
        /// §3.6 as N107 restates it: <c>RunReport.SplitsSkipped</c> counts the requests a split step found and did not take
        /// (over <c>CaseLimit</c>, covered by a live split, or an empty element set). A nested split that the depth limit
        /// cuts off is never looked for, so it is not counted.
        /// </summary>
        [Fact]
        public void P11_ASplitCutOffByTheDepthLimitIsNotLookedFor()
        {
            var c = new CosmosTheory(new EGraph());
            var outerKey = c.Graph.DefineRelation<string>("outer key");
            var innerKey = c.Graph.DefineRelation<string>("inner key");
            var outer = c.Graph.DefineDomainSplit("outer", c.Dom, [outerKey], c.Case);
            var inner = c.Graph.DefineDomainSplit("inner", c.Dom, [innerKey], c.Case);
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(
                outerKey.Of("k0"), c.Present.Of("k0"), c.Dom.Of("k0").Is(ValueSet.Of("a", "b")),
                innerKey.Of("k1"), c.Present.Of("k1"), c.Dom.Of("k1").Is(ValueSet.Of("x", "y")));
            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            var nested = Schedule.Split(outer, Schedule.Saturate(settle, Schedule.Split(inner, settle)));

            var deep = scope.Run(nested, new RunOptions { SplitDepthLimit = 2 });
            Assert.Equal(2 + 2 * 2, deep.CasesOpened);

            var shallow = scope.Run(nested, new RunOptions { SplitDepthLimit = 1 });
            Assert.Equal(2, shallow.CasesOpened);
            Assert.Equal(0, shallow.SplitsSkipped);
        }

    }

}
