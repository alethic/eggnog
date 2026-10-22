using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Cases
{

    /// <summary>
    /// What a case whose contradiction stopped it answers: it has no model, so it answers whether it is contradictory
    /// and its identity, does nothing on <c>Assume</c>, returns <c>false</c> from <c>Union</c>, returns at once from
    /// <c>Run</c>, may be disposed, and refuses every other read and write with
    /// <see cref="ScopeContradictedException"/>. Scopes under a stopped case answer as it does, and a case its parent's
    /// contradiction reached stops when it catches up. Any other contradictory scope stays fully usable.
    /// </summary>
    public sealed class ContradictedCaseTests
    {

        [Fact]
        public void AStoppedCaseAnswersOnlyWhatNeedsNoModel()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            var b = parent.Make(t.B.Of());
            var query = t.Graph.DefineQuery("tags", q => { var n = q.Var<Node>("n"); q.When(t.Tag.Of(n)); });
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            var mine = @case.Make(t.Wrap.Of(a));
            var extractor = @case.CreateExtractor();
            var version = @case.Version;

            // A rule derives the contradiction in the case, which stops it: Run reports it.
            t.Rules.DefineRule("a tagged node contradicts", r => { var n = r.Var<Node>("n"); r.When(t.Tag.Of(n)); r.Then(t.Graph.Contradiction.Of()); });
            @case.Assume(t.Tag.Of(a));
            var stopped = @case.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(RunStopReason.Contradiction, stopped.StopReason);
            Assert.True(@case.Engine.IsStopped);

            // What still answers.
            Assert.True(@case.IsContradictory);
            Assert.True(@case.IsCase);
            Assert.False(@case.IsFaulted);
            Assert.Null(@case.Failure);
            Assert.False(@case.IsDisposed);
            Assert.False(@case.IsFrozen);
            Assert.Same(parent, @case.Parent);
            Assert.Empty(@case.Children);
            Assert.Empty(@case.LocalRules);
            Assert.True(@case.Version > version);
            Assert.StartsWith(parent.Path + "/case#", @case.Path, StringComparison.Ordinal);
            @case.Assume(t.Tag.Of(b));                                       // nothing
            Assert.False(@case.Union(a, b));
            var again = @case.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(RunStopReason.Contradiction, again.StopReason);
            Assert.Equal(0, again.Iterations);

            // What needs a model.
            void Refused(Action call)
            {
                var refused = Assert.Throws<ScopeContradictedException>(call);
                Assert.Equal(@case.Path, refused.ScopePath);
                Assert.IsAssignableFrom<InvalidOperationException>(refused);
            }

            Refused(() => @case.Check(t.Tag.Of(a)));
            Refused(() => @case.Get(t.A.Of()));
            Refused(() => @case.TryGet(t.A.Of(), out _));
            Refused(() => @case.Find(mine));
            Refused(() => @case.AreEqual(a, b));
            Refused(() => @case.AreEqual(a, t.A.Of()));
            Refused(() => @case.Count(query));
            Refused(() => @case.Any(query));
            Refused(() => @case.ForEachMatch(query, _ => true));
            Refused(() => @case.CountRows(t.Tag));
            Refused(() => @case.Rows(t.Tag));
            Refused(() => @case.Extract(a));
            Refused(() => @case.ExtractVariants(a, 2));
            Refused(() => @case.CreateExtractor());
            Refused(() => extractor.Extract(mine));
            Refused(() => extractor.ExtractVariants(mine, 2));
            Refused(() => extractor.TryGetCost(mine, out _));
            Refused(() => @case.Make(t.B.Of()));
            Refused(() => @case.Write(w => w.Assume(t.Tag.Of(b))));
            Refused(() => @case.CreateChild());
            Refused(() => @case.OpenCases());
            Refused(() => @case.DefineRule(t.Rules, "late", r => { }));
            Refused(@case.Freeze);

            // The parent is untouched, and the lift drops the case as vacuous.
            Assert.False(parent.IsContradictory);
            var lifted = cases.Lift();
            Assert.True(lifted.ParentContradicted);
            Assert.True(@case.IsDisposed);
            Assert.True(parent.IsContradictory);
        }

        [Fact]
        public void AContradictoryScopeThatIsNotACaseStaysUsable()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            parent.Assume(t.Graph.Contradiction.Of());

            Assert.True(parent.IsContradictory);
            Assert.False(parent.IsCase);
            Assert.False(parent.Engine.IsStopped);
            parent.Assume(t.Tag.Of(a));
            Assert.True(parent.Check(t.Tag.Of(a)));
            Assert.Equal("(A)", parent.Extract(a).ToString());
            Assert.NotNull(parent.CreateChild());
        }

        [Fact]
        public void AWriteThatContradictsACaseStopsIt()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var @case = cases.Add();

            @case.Assume(t.Graph.Contradiction.Of());
            Assert.True(@case.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => @case.Check(t.Graph.Contradiction.Of()));

            // A batch that stops the case commits; the case is stopped from then on.
            var other = cases.Add();
            var a = other.Make(t.A.Of());
            other.Write(w => { w.Make(t.Wrap.Of(a)); w.Assume(t.Graph.Contradiction.Of()); });
            Assert.True(other.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => other.Find(a));

            @case.Dispose();
            Assert.True(@case.IsDisposed);
        }

        [Fact]
        public void ABatchThatAFlushStopsWritesNothingMore()
        {
            // A write of a function of classes after a union flushes the batch first, which here stops the case: the
            // batch's later calls write nothing, as a stopped case's do.
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, Node>("f");
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var write = cases.Add();
            var a = write.Make(t.A.Of());
            var b = write.Make(t.B.Of());
            write.Write(w =>
            {
                w.Union(a, b);
                w.Assume(t.Graph.Contradiction.Of());
                w.Assume(f.Of(1L).Is(t.A.Of()));
                w.Assume(t.Tag.Of(a));
            });
            Assert.True(write.IsContradictory);

            var assume = cases.Add();
            assume.Assume(t.A.Of().Is(t.B.Of()), t.Graph.Contradiction.Of(), f.Of(1L).Is(t.A.Of()), t.Tag.Of(t.A.Of()));
            Assert.True(assume.IsContradictory);
        }

        /// <summary>
        /// A scope under a stopped case has no model either, and the engine refuses to catch it up with the stopped
        /// case. A plain child, and a case of a set opened under that child, answer as the stopped case does, naming
        /// themselves and the stopped case; the lift that drops the case disposes everything under it.
        /// </summary>
        [Fact]
        public void AScopeUnderAStoppedCaseAnswersAsTheStoppedCase()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            var b = parent.Make(t.B.Of());
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            var child = @case.CreateChild();
            using var inner = child.OpenCases();
            var grandcase = inner.Add();
            var version = child.Version;

            @case.Assume(t.Graph.Contradiction.Of());
            Assert.True(@case.IsContradictory);

            foreach (var below in new[] { child, grandcase })
            {
                Assert.True(below.IsContradictory);
                below.Assume(t.Tag.Of(a));                                    // nothing
                Assert.False(below.Union(a, b));
                var run = below.Run(Schedule.Saturate(t.Rules));
                Assert.Equal(RunStopReason.Contradiction, run.StopReason);
                Assert.Equal(0, run.Iterations);
                Assert.False(below.IsFaulted);

                void Refused(Action call)
                {
                    var refused = Assert.Throws<ScopeContradictedException>(call);
                    Assert.Equal(below.Path, refused.ScopePath);
                    Assert.Contains(@case.Path, refused.Message, StringComparison.Ordinal);
                }

                Refused(() => below.Check(t.Tag.Of(a)));
                Refused(() => below.Find(a));
                Refused(() => below.Extract(a));
                Refused(() => below.Make(t.Wrap.Of(a)));
                Refused(() => below.Write(w => w.Assume(t.Tag.Of(a))));
                Refused(() => below.CreateChild());
                Refused(() => below.OpenCases());
                Refused(() => below.DefineRule(t.Rules, "late", r => { }));
                Refused(below.Freeze);
            }

            Assert.Equal(version, child.Version);
            Assert.Equal(child.Path, Assert.Throws<ScopeContradictedException>(() => inner.Add()).ScopePath);
            Assert.Equal(child.Path, Assert.Throws<ScopeContradictedException>(() => inner.Lift()).ScopePath);

            Assert.True(cases.Lift().ParentContradicted);
            Assert.True(@case.IsDisposed);
            Assert.True(child.IsDisposed);
            Assert.True(grandcase.IsDisposed);
            Assert.Empty(parent.Children);
        }

        /// <summary>
        /// A case its parent's contradiction reached since its last call is stopped once it catches up (the parent's
        /// contradiction row is visible in the case), and the structural calls that enter without reading catch it up
        /// before they check: <c>Freeze</c> answers as on any stopped case.
        /// </summary>
        [Fact]
        public void AFreezeOfACaseContradictedThroughItsParentIsRefusedAsOnAStoppedCase()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            parent.Assume(t.Graph.Contradiction.Of());

            Assert.Equal(@case.Path, Assert.Throws<ScopeContradictedException>(@case.Freeze).ScopePath);
            Assert.True(@case.IsContradictory);
            Assert.False(@case.IsFaulted);
        }

        /// <summary>
        /// An extractor made before its case was contradicted through the case's parent catches its scope up before it
        /// checks, so it refuses as on a stopped case, whichever extractor is asked first, and never extracts from a
        /// scope with no model. Before the parent's contradiction, the catch-up of an extraction leaves
        /// <see cref="Scope.Version"/> alone and the extractor keeps working.
        /// </summary>
        [Fact]
        public void AnExtractorUnderACaseContradictedThroughItsParentRefusesAsOnAStoppedCase()
        {
            foreach (var childFirst in new[] { false, true })
            {
                var t = new NodeTheory();
                var parent = t.Root.CreateChild();
                var a = parent.Make(t.A.Of());
                using var cases = parent.OpenCases();
                var @case = cases.Add();
                var child = @case.CreateChild();
                var ofCase = @case.CreateExtractor();
                var ofChild = child.CreateExtractor();
                var version = @case.Version;
                var childVersion = child.Version;
                Assert.Equal("(A)", ofCase.Extract(a).ToString());
                Assert.Equal("(A)", ofChild.Extract(a).ToString());
                Assert.True(ofChild.TryGetCost(a, out _));
                Assert.Equal(version, @case.Version);
                Assert.Equal(childVersion, child.Version);

                parent.Assume(t.Graph.Contradiction.Of());

                var order = childFirst ? new[] { (ofChild, child), (ofCase, @case) } : new[] { (ofCase, @case), (ofChild, child) };
                foreach (var (extractor, scope) in order)
                {
                    var refused = Assert.Throws<ScopeContradictedException>(() => extractor.Extract(a));
                    Assert.Equal(scope.Path, refused.ScopePath);
                    Assert.Throws<ScopeContradictedException>(() => extractor.TryGetCost(a, out _));
                    Assert.Throws<ScopeContradictedException>(() => extractor.TryExtract(a, out _));
                    Assert.Throws<ScopeContradictedException>(() => extractor.ExtractVariants(a, 2));
                }

                Assert.True(@case.IsContradictory);
                Assert.False(@case.IsFaulted);
                Assert.False(child.IsFaulted);
            }
        }

        /// <summary>
        /// Only a case has the stop flag, and a stopped case still answers <c>IsContradictory</c>. A child made under a
        /// host case before the case stopped is not a case and has no model either: it answers <c>IsContradictory</c>,
        /// and each of its members either works or refuses with <see cref="ScopeContradictedException"/>, never with an
        /// engine error. The lift drops the vacuous case and disposes the child with it.
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
        /// A case its parent's contradiction reached since its last call stops at its next catch-up. A plain child of
        /// it, and a case of a set opened under that child, asked first, answer as the stopped case, without a fault
        /// and without the engine's own exception; their catch-up is what stops the case.
        /// </summary>
        [Fact]
        public void ScopesUnderACaseContradictedThroughItsParentAnswerAsTheStoppedCase()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            var b = parent.Make(t.B.Of());
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            var child = @case.CreateChild();
            using var inner = child.OpenCases();
            var grandcase = inner.Add();
            var version = child.Version;

            parent.Assume(t.Graph.Contradiction.Of());

            // The descendants are asked first: their catch-up is what stops the case.
            Assert.True(grandcase.IsContradictory);
            Assert.True(child.IsContradictory);
            foreach (var below in new[] { child, grandcase })
            {
                var refused = Assert.Throws<ScopeContradictedException>(() => below.Check(t.Tag.Of(a)));
                Assert.Equal(below.Path, refused.ScopePath);
                Assert.Contains(@case.Path, refused.Message, StringComparison.Ordinal);
                below.Assume(t.Tag.Of(a));
                Assert.False(below.Union(a, b));
                var run = below.Run(Schedule.Saturate(t.Rules));
                Assert.Equal(RunStopReason.Contradiction, run.StopReason);
                Assert.Throws<ScopeContradictedException>(() => below.CreateChild());
                Assert.Throws<ScopeContradictedException>(() => below.DefineRule(t.Rules, "late", r => { }));
                Assert.Throws<ScopeContradictedException>(below.Freeze);
                Assert.False(below.IsFaulted);
            }

            Assert.Equal(child.Path, Assert.Throws<ScopeContradictedException>(() => inner.Add()).ScopePath);
            Assert.Equal(child.Path, Assert.Throws<ScopeContradictedException>(() => inner.Lift()).ScopePath);
            Assert.Equal(version, child.Version);
            Assert.True(@case.IsContradictory);
            Assert.False(@case.IsFaulted);
            Assert.False(parent.IsFaulted);

            Assert.True(cases.Lift().ParentContradicted);
            Assert.True(@case.IsDisposed);
            Assert.True(child.IsDisposed);
            Assert.True(grandcase.IsDisposed);
        }

        /// <summary>
        /// An extractor of a plain child, made before the case above it stopped, refuses as the stopped case does.
        /// </summary>
        [Fact]
        public void AnExtractorOfAChildMadeBeforeItsCaseStoppedRefuses()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            var child = @case.CreateChild();
            var extractor = child.CreateExtractor();
            Assert.NotNull(extractor.Extract(a));

            @case.Assume(t.Graph.Contradiction.Of());

            var refused = Assert.Throws<ScopeContradictedException>(() => extractor.Extract(a));
            Assert.Equal(child.Path, refused.ScopePath);
            Assert.Throws<ScopeContradictedException>(() => extractor.TryGetCost(a, out _));
            Assert.Throws<ScopeContradictedException>(() => extractor.ExtractVariants(a, 2));
        }

        /// <summary>
        /// An extractor never extracts a term from a scope with no model: when the case above it is contradicted
        /// through its own parent, the extractors of the case and of a child of it throw an
        /// <see cref="InvalidOperationException"/> (refusing as on a stopped case, or reporting that the scope
        /// changed); neither returns a term.
        /// </summary>
        [Fact]
        public void AnExtractorUnderACaseContradictedThroughItsParentNeverExtracts()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            var child = @case.CreateChild();
            var ofCase = @case.CreateExtractor();
            var ofChild = child.CreateExtractor();
            Assert.NotNull(ofCase.Extract(a));
            Assert.NotNull(ofChild.Extract(a));

            parent.Assume(t.Graph.Contradiction.Of());

            foreach (var extractor in new[] { ofCase, ofChild })
            {
                var refused = Record.Exception(() => extractor.Extract(a));
                Assert.True(refused is InvalidOperationException, $"extracted from '{extractor.Scope.Path}', which has no model: {refused}");
            }
        }

        /// <summary>
        /// <c>Run</c> on a stopped case returns at once whatever the schedule: a schedule of split steps alone reports
        /// the contradiction, opens no case and records no step.
        /// </summary>
        [Fact]
        public void RunOnAStoppedCaseReportsTheContradictionWhateverTheSchedule()
        {
            var t = new ChooseSplitTheory();
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(Set.Of("a", "b")));
            using var cases = root.OpenCases();
            var @case = cases.Add();
            @case.Assume(t.Graph.Contradiction.Of());
            Assert.True(@case.Engine.IsStopped);

            foreach (var schedule in new[] { Schedule.Split(t.Split, t.Rules), Schedule.Saturate(Schedule.Split(t.Split, t.Rules)) })
            {
                var report = @case.Run(schedule, new RunOptions { RecordSteps = true });
                Assert.Equal(RunStopReason.Contradiction, report.StopReason);
                Assert.Equal(0, report.CasesOpened);
                Assert.Empty(report.Steps);
            }

            Assert.Empty(@case.Children);
        }

        /// <summary>
        /// A case its parent's contradiction reaches stops when <c>Make</c> brings it up to date, and the call throws
        /// <see cref="ScopeContradictedException"/>. <c>Make</c> checks again after its commit, for a batch that stops
        /// its case; a <c>Make</c> batch writes only constructor rows and unions, so here the entry check is the one
        /// that throws.
        /// </summary>
        [Fact]
        public void AMakeOnACaseItsParentContradictedThrows()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            Assert.False(@case.Engine.IsStopped);

            // The parent's contradiction reaches the case when the Make brings it up to date.
            parent.Assume(t.Graph.Contradiction.Of());
            var refused = Assert.Throws<ScopeContradictedException>(() => @case.Make(t.A.Of()));

            Assert.Equal(@case.Path, refused.ScopePath);
            Assert.True(@case.Engine.IsStopped);
            Assert.True(@case.IsContradictory);
        }

        /// <summary>
        /// <see cref="CaseSet.Add"/> and <see cref="CaseSet.Lift"/> on a set whose parent is a case that stopped after
        /// the set was opened both throw <see cref="ScopeContradictedException"/> naming the case.
        /// </summary>
        [Fact]
        public void ACaseSetUnderAStoppedCaseRefusesAddAndLift()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var outer = parent.OpenCases();
            var @case = outer.Add();
            using var inner = @case.OpenCases();
            inner.Add().Assume(t.Fact.Of("a"));

            @case.Assume(t.Graph.Contradiction.Of());
            Assert.True(@case.Engine.IsStopped);

            Assert.Equal(@case.Path, Assert.Throws<ScopeContradictedException>(() => inner.Add()).ScopePath);
            Assert.Equal(@case.Path, Assert.Throws<ScopeContradictedException>(() => inner.Lift()).ScopePath);
        }

        /// <summary>
        /// A host-driven set whose parent is a stopped case refuses <c>Add</c> and <c>Lift</c> with
        /// <see cref="ScopeContradictedException"/>.
        /// </summary>
        [Fact]
        public void AnInnerSetOfAStoppedCaseRefusesAddAndLift()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            using var outer = parent.OpenCases();
            var @case = outer.Add();
            using var inner = @case.OpenCases();
            inner.Add();

            @case.Assume(t.Graph.Contradiction.Of());

            Assert.True(@case.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => inner.Lift());
            Assert.Throws<ScopeContradictedException>(() => inner.Add());
        }

        /// <summary>
        /// Only case scopes stop, but a plain child of a stopped case cannot be caught up with it, so it has no model
        /// either: it answers <c>IsContradictory</c> (true) and refuses the rest with
        /// <see cref="ScopeContradictedException"/>, as its stopped parent does, and it is not faulted.
        /// </summary>
        [Fact]
        public void APlainChildOfAStoppedCaseAnswersAsAScopeWithNoModel()
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

            // The engine refuses to catch a plain child up with a stopped parent (it can only be disposed). Every scope
            // answers IsContradictory, and a scope under a stopped case has no model either: it answers as the stopped
            // case does (true, then ScopeContradictedException), never with an engine InvalidOperationException.
            Assert.True(child.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => child.Check(t.Fact.Of("a")));
            Assert.Throws<ScopeContradictedException>(() => child.CreateChild());
            Assert.False(child.IsFaulted);
            child.Dispose();
        }

        /// <summary>
        /// <c>Run</c> on a stopped case returns at once with <see cref="RunStopReason.Contradiction"/>, no iteration,
        /// and the scope unchanged.
        /// </summary>
        [Fact]
        public void ARunOnAStoppedCaseDoesNothing()
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
        /// A host case under a scope that becomes contradictory after the case was made, with a host set opened in the
        /// case before that. The case has no model from then on: the parent's contradiction row is visible in it.
        /// </summary>
        /// <returns>The theory, the parent, the set the case belongs to, the case, and the set opened in the
        /// case.</returns>
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
        /// <c>DefineRule</c> on a case with no model throws <see cref="ScopeContradictedException"/>: the check follows
        /// the case's catch-up, as every other entry point's does.
        /// </summary>
        [Fact]
        public void DefineRuleOnACaseContradictedThroughItsParentThrows()
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
        /// <c>OpenCases</c> on a case with no model throws <see cref="ScopeContradictedException"/>.
        /// </summary>
        [Fact]
        public void OpenCasesOnACaseContradictedThroughItsParentThrows()
        {
            var s = ContradictedThroughItsParent();
            Assert.Throws<ScopeContradictedException>(() => s.Case.OpenCases());
        }

        /// <summary>
        /// <see cref="CaseSet.Add"/> on a stopped parent throws <see cref="ScopeContradictedException"/>, also when the
        /// parent learns it is stopped from the catch-up <c>Add</c> makes; the case is not faulted.
        /// </summary>
        [Fact]
        public void AddUnderACaseContradictedThroughItsParentThrows()
        {
            var s = ContradictedThroughItsParent();
            var refused = Record.Exception(() => s.Inner.Add());
            Assert.False(s.Case.IsFaulted);
            Assert.False(s.Case.Engine.Context.IsFaulted);
            Assert.True(s.Case.IsContradictory);
            Assert.IsType<ScopeContradictedException>(refused);
        }

    }

}
