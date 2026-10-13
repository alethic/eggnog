using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Cosmos;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review probes of R3's public surface (rule-layer §3.1, §3.6, §3.7, §6.9, §6.13, D9, D18, D40, D45): each test asserts
    /// what the spec and the XML docs of the public members promise, or pins the behaviour a finding describes.
    /// </summary>
    public sealed class R3ApiReviewTests
    {

        /// <summary>
        /// A graph with a domain function and two guard relations, each with a domain split, under an empty frozen root,
        /// and a writable scope that declares <c>k1</c> (outer, {a, b}) and <c>k2</c> (inner, {c, d}).
        /// </summary>
        static (EGraph Graph, Scope Scope, SplitRule Outer, SplitRule Inner, Ruleset Rules) TwoSplits()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var outer = graph.DefineRelation<string>("outer");
            var inner = graph.DefineRelation<string>("inner");
            var outerSplit = graph.DefineDomainSplit("outer", dom, [outer]);
            var innerSplit = graph.DefineDomainSplit("inner", dom, [inner]);
            var rules = graph.DefineRuleset("rules");
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(dom.Of("k1").Is(ValueSet.Of("a", "b")), outer.Of("k1"), dom.Of("k2").Is(ValueSet.Of("c", "d")), inner.Of("k2"));
            return (graph, scope, outerSplit, innerSplit, rules);
        }

        /// <summary>
        /// §3.6 documents <c>RunReport.SplitsSkipped</c> as "Over CaseLimit, over SplitDepthLimit, or an empty element
        /// set", and the member's XML doc lists "at SplitDepthLimit". A nested split the depth limit stops is never searched
        /// (colors §11.3: no phase B at the limit, nested splits disabled in phase A), so it adds nothing to the count; a
        /// request a live split of the run covers (B6) is counted, which §3.6 does not list. This pins what the run does;
        /// the documentation should say the same.
        /// </summary>
        [Fact]
        public void SplitsSkippedCountsCoveredKeysButNeverADepthLimitedSplit()
        {
            // Depth limit 1: the outer split opens its two cases, and the inner split, which would open two more in each,
            // never runs.
            var (_, scope, outer, inner, rules) = TwoSplits();
            var limited = scope.Run(Schedule.Split(outer, Schedule.Split(inner, rules)), new RunOptions { SplitDepthLimit = 1 });
            Assert.Equal(2, limited.CasesOpened);
            Assert.Equal(0, limited.SplitsSkipped);

            // Depth limit 2: the inner split opens two cases in each outer case.
            (_, scope, outer, inner, rules) = TwoSplits();
            var deep = scope.Run(Schedule.Split(outer, Schedule.Split(inner, rules)), new RunOptions { SplitDepthLimit = 2 });
            Assert.Equal(2 + 4, deep.CasesOpened);
            Assert.Equal(0, deep.SplitsSkipped);

            // A second execution of the step in one run finds k1 covered by its own live split (B6): counted as skipped.
            (_, scope, outer, _, rules) = TwoSplits();
            var twice = scope.Run(Schedule.Sequence(Schedule.Split(outer, rules), Schedule.Split(outer, rules)));
            Assert.Equal(2, twice.CasesOpened);
            Assert.Equal(1, twice.SplitsSkipped);
        }

        /// <summary>
        /// §6.13: "a rule's write conflicts on a function without a lattice, during a run" is a RuleEvaluationException that
        /// faults the scope (D18), and Scope.Run documents no other evaluation failure. A split's assumptions are written by
        /// the run, into its cases; an assumption that gives a function without a lattice a second value is such a write.
        /// </summary>
        [Fact]
        public void AnAssumptionConflictInASplitIsAnEvaluationFailureOfTheRun()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var pick = graph.DefineFunction<string, string>("pick");
            var rules = graph.DefineRuleset("rules");
            var split = graph.DefineSplit("pick a value", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(pick.Of(p).Is(v));
            });
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(dom.Of("k").Is(ValueSet.Of("a", "b")), pick.Of("k").Is("a"));

            // The b case's assumption pick(k) = b conflicts with the inherited pick(k) = a.
            var failure = Record.Exception(() => scope.Run(Schedule.Split(split, rules)));

            Assert.NotNull(failure);
            Assert.IsType<RuleEvaluationException>(failure);
            Assert.True(scope.IsFaulted);
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// The cases of a split whose assumption conflicts are disposed when the run ends (§6.9.3: "whether the run
        /// completes, stops at a limit, or throws"), and the scope's split records are gone.
        /// </summary>
        [Fact]
        public void AnAssumptionConflictLeavesNoCaseBehind()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var pick = graph.DefineFunction<string, string>("pick");
            var rules = graph.DefineRuleset("rules");
            var split = graph.DefineSplit("pick a value", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(pick.Of(p).Is(v));
            });
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(dom.Of("k").Is(ValueSet.Of("a", "b")), pick.Of("k").Is("a"));

            Assert.ThrowsAny<Exception>(() => scope.Run(Schedule.Split(split, rules)));

            Assert.Empty(scope.Children);
            Assert.Equal(0, scope.Engine.Context.SplitRecordCount);
            Assert.Equal(0, scope.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// §3.7 and the XML docs of CaseSet: "Dispose may be called from any thread and never throws", and it "disposes
        /// every case" (D40), after which the parent's release can proceed. A case that Add makes while another thread
        /// disposes the set must not outlive the set: here Add is held inside the parent's catch-up (a lattice merge of a
        /// value its writable ancestor wrote since) while the set is disposed on another thread.
        /// </summary>
        [Fact]
        public void ACaseAddedWhileTheSetIsDisposedOnAnotherThreadIsDisposedWithIt()
        {
            using var entered = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            var block = 0;
            var graph = new EGraph();
            var max = new ValueLattice<long>("blocking max", (a, b) =>
            {
                if (Interlocked.Exchange(ref block, 0) == 1)
                {
                    entered.Set();
                    proceed.Wait(TimeSpan.FromSeconds(30));
                }

                return Math.Max(a, b);
            }, Math.Min);
            var f = graph.DefineFunction<string, long>("f", max);
            graph.Root.Freeze();
            var ancestor = graph.Root.CreateChild();
            var parent = ancestor.CreateChild();
            parent.Assume(f.Of("x").Is(1L));
            var cases = parent.OpenCases();

            // The ancestor's write reaches the parent at its next call: Add's catch-up merges 1 with 2.
            ancestor.Assume(f.Of("x").Is(2L));
            Volatile.Write(ref block, 1);
            var disposer = Task.Run(() =>
            {
                if (entered.Wait(TimeSpan.FromSeconds(30)))
                    cases.Dispose();

                proceed.Set();
            });

            Scope? added = null;
            Exception? refused = null;
            try
            {
                added = cases.Add();
            }
            catch (ObjectDisposedException exception)
            {
                refused = exception;
            }

            disposer.Wait();
            Assert.True(entered.IsSet, "the catch-up ran the lattice merge");

            // Either Add refuses (the set was disposed under it), or the case it made is disposed with the set.
            Assert.True(refused is not null || added is not null);
            var listed = cases.Cases.Count;
            var alive = added is { IsDisposed: false };

            // The parent's release, which the open set deferred, proceeds once the parent is disposed, even when the
            // stray case is disposed directly as well.
            added?.Dispose();
            parent.Dispose();
            Assert.True(parent.Engine.IsDisposed, "the parent is never released: a case of the disposed set still holds it");
            Assert.Equal(0, listed);
            Assert.False(alive, "the set was disposed, and a case of it is alive");
        }

        /// <summary>
        /// §2.4, §6.12 and D17: children of a frozen scope have their own thread domains and may run concurrently, and a
        /// split's cases run on their parent's thread. Queries that split (sharing the split rules, their sources, the
        /// graph's lift spec and the functions' case joins) run on 8 threads under one frozen schema and give the serial
        /// answer.
        /// </summary>
        [Fact]
        public void SplittingQueriesUnderOneFrozenSchemaRunOnParallelThreads()
        {
            var c = new Cosmos.Scoped.CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = Cosmos.Scoped.SweepLinks.Compile(c);
            ConstructorTerm<Pred> Term() =>
                c.And.Of(c.Or.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.type", "map")), c.UuidEq.Of("data.data.parkId", Cosmos.Scoped.SweepLinks.Park));

            string Ask()
            {
                using var query = schema.CreateChild(c.Query);
                var q = query.Make(Term());
                query.Assume(c.WhereClause.Of(q));
                var report = query.Run(c.Saturation);
                Assert.Equal(RunStopReason.Completed, report.StopReason);
                Assert.Equal(2, report.CasesOpened);
                Assert.Empty(query.Children);
                return query.Extract(q).ToString();
            }

            var serial = Ask();
            var answers = new string[32];
            Support.DedicatedThreads.For(8, answers.Length, i => answers[i] = Ask());

            Assert.All(answers, answer => Assert.Equal(serial, answer));
            Assert.Empty(schema.Children);
        }

        /// <summary>
        /// D45 and §6.6.8: only a case has the stop flag; "any other contradictory scope stays fully usable", and a stopped
        /// case still answers <c>IsContradictory</c>. A child made under a host case before the case stopped is not a case.
        /// Whether it stays usable or answers as a scope with no model (<see cref="ScopeContradictedException"/>), its
        /// members must answer as the rule layer documents them, never with an engine error.
        /// </summary>
        [Fact]
        public void AChildOfAStoppedCaseAnswersAsDocumented()
        {
            var t = new Support.NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            var child = @case.CreateChild();
            @case.Assume(t.Graph.Contradiction.Of());
            Assert.True(@case.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => @case.Check(t.Tag.Of(a)));

            Assert.False(child.IsCase);
            var contradictory = Record.Exception(() => Assert.True(child.IsContradictory));
            Assert.Null(contradictory);
            foreach (var call in new Action[]
            {
                () => child.Check(t.Tag.Of(a)),
                () => child.Assume(t.Tag.Of(a)),
                () => child.Extract(a),
                () => child.Run(t.Rules),
                () => child.CreateChild(),
            })
            {
                var refused = Record.Exception(call);
                Assert.True(refused is null or ScopeContradictedException, $"an engine error escapes: {refused}");
            }

            // The lift drops the vacuous case, and the child under it with it.
            Assert.True(cases.Lift().ParentContradicted);
            Assert.True(child.IsDisposed);
        }

        /// <summary>
        /// §3.1 and D43: <c>DefineDomainSplit</c>'s guards are relations that must hold of the key, and
        /// <c>DefineSplit</c> accepts scoped reads in its body (§6.9.2). A guard read through a scoped view is refused by
        /// <c>DefineDomainSplit</c>, which no part of the spec asks for, with a message that calls the handle "not a table
        /// of this graph".
        /// </summary>
        [Fact]
        public void ADomainSplitRefusesAScopedGuardThatDefineSplitAccepts()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var present = graph.DefineRelation<string>("present");
            var schema = graph.DefineScopeTag("schema");

            // The same split, written out, with the guard read in the schema.
            var written = graph.DefineSplit("written out", s =>
            {
                var (k, d) = s.Vars<string, ValueSet<string>>("k", "d");
                s.When(present.In(schema).Of(k), d.Is(dom.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<string>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Key(k);
                s.Assume(dom.Of(k).Is(Primitives.Singleton<string>().Of(v)));
            });
            Assert.Equal(new[] { schema }, written.ReadScopes);

            var refused = Record.Exception(() => graph.DefineDomainSplit("guarded in the schema", dom, [present.In(schema)]));
            Assert.Null(refused);
        }

    }

}
