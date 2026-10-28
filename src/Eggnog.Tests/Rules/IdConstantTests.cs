using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// Class constants in rules and queries. A constant that loses a union is matched in its canonical form wherever
    /// the rule runs, the engine canonicalizing it in every context. Two constants equated in a body are compared where
    /// the rule runs: at definition when that scope can be read then, and at run time otherwise. A constant that
    /// changes forces a full run, so watermarks never hide its new matches.
    /// </summary>
    public sealed class IdConstantTests
    {

        [Fact]
        public void ARuleWithAClassConstantMatchesTheClassAfterTheConstantLosesAUnion()
        {
            var t = new NodeTheory();
            var link = t.Graph.DefineRelation<Node, long>("link");
            var hit = t.Graph.DefineRelation<long>("hit");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Rules.DefineRule("watch b", r =>
            {
                var n = r.Var<long>("n");
                r.When(link.Of(b, n));
                r.Then(hit.Of(n));
            });
            t.Root.Assume(link.Of(a, 3L), link.Of(b, 1L));
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(hit.Of(1L)));
            Assert.False(t.Root.Check(hit.Of(3L)));

            Assert.True(t.Root.Union(a, b));
            Assert.Equal(a.Id, t.Root.Find(b).Id);
            t.Root.Assume(link.Of(a, 2L));
            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.Check(hit.Of(2L)));
            Assert.True(t.Root.Check(hit.Of(3L)));
        }

        [Fact]
        public void AnActionWritingSuchAConstantWritesTheCanonicalCell()
        {
            var t = new NodeTheory();
            var trigger = t.Graph.DefineRelation<long>("trigger");
            var mark = t.Graph.DefineRelation<Node>("mark");
            var both = t.Graph.DefineRelation<Node>("both");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Rules.DefineRule("mark b", r =>
            {
                var n = r.Var<long>("n");
                r.When(trigger.Of(n));
                r.Then(mark.Of(b));
            });
            t.Rules.DefineRule("both", r =>
            {
                var x = r.Var<Node>("x");
                r.When(mark.Of(x), t.Tag.Of(x));
                r.Then(both.Of(x));
            });
            t.Root.Assume(t.Tag.Of(a));
            t.Root.Union(a, b);
            Assert.Equal(a.Id, t.Root.Find(b).Id);
            t.Root.Assume(trigger.Of(5L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.Check(mark.Of(a)));
            Assert.True(t.Root.Check(both.Of(a)));
        }

        /// <summary>
        /// <c>c1.Is(c2)</c> of class constants is evaluated when the rule is defined, and dropped when true: two class
        /// constants already in one class are equal, so the rule compiles and matches.
        /// </summary>
        [Fact]
        public void ClassConstantsAlreadyEqualAtDefinitionMatch()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Union(a, b);
            t.Root.Assume(t.Tag.Of(a));
            t.Rules.DefineRule("both constants", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                r.Then(t.Num.Of(1L));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(t.Num.Of(1L)));
        }

        /// <summary>
        /// The variable-free form of the same: <c>a.Is(b)</c> of two class constants that are equal now.
        /// </summary>
        [Fact]
        public void TwoEqualClassConstantsEquatedDirectlyAreDropped()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Union(a, b);
            t.Rules.DefineRule("equal constants", r =>
            {
                r.When(((Term<Node>)a).Is(b));
                r.Then(t.Num.Of(2L));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(t.Num.Of(2L)));
        }

        /// <summary>
        /// A class constant in a <c>NotEqual</c> step that lost a union is compared canonically.
        /// </summary>
        [Fact]
        public void AClassConstantInANotEqualIsCanonical()
        {
            var t = new NodeTheory();
            var other = t.Graph.DefineRelation<Node>("other");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(t.Tag.Of(a));
            t.Root.Union(a, b);
            Assert.Equal(a.Id, t.Root.Find(b).Id);
            t.Rules.DefineRule("not b", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.IsNot(b));
                r.Then(other.Of(x));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.Check(other.Of(a)));
        }

        /// <summary>
        /// A query may hold classes of any scope; its origin checks run at each call, against the scope it runs in. Two
        /// class constants that are one class in that scope match there, even when the root never saw them unioned.
        /// </summary>
        [Fact]
        public void AQuerysEquatedClassConstantsCompareInTheScopeItRunsIn()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            schema.Union(a, b);
            schema.Assume(t.Num.Of(1L));

            var query = t.Graph.DefineQuery("equated in the child", q =>
            {
                q.When(t.Num.Of(q.Var<long>("n")), ((Term<Node>)a).Is(b));
            });

            Assert.Equal(1, schema.Count(query));
        }

        /// <summary>
        /// The same with classes minted in the child: the root has never seen either id.
        /// </summary>
        [Fact]
        public void AQuerysEquatedClassConstantsMintedInAChildCompareThere()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            var a = schema.Make(t.A.Of());
            var b = schema.Make(t.B.Of());
            schema.Union(a, b);
            schema.Assume(t.Num.Of(1L));

            var query = t.Graph.DefineQuery("minted in the child", q =>
            {
                q.When(t.Num.Of(q.Var<long>("n")), ((Term<Node>)a).Is(b));
            });

            Assert.Equal(1, schema.Count(query));
        }

        /// <summary>
        /// Rule definitions may be made from any thread at any time and join the next iteration: a global rule whose
        /// two class constants are one class at the root is defined without an error while the writable root runs on
        /// another thread.
        /// </summary>
        [Fact]
        public void AGlobalRuleWithEquatedClassConstantsMayBeDefinedWhileTheRootRuns()
        {
            using var run = new BlockingRun();
            run.Graph.DefineSort<Node>("Node");
            var a = run.Graph.DefineConstructor<Node>("A");
            var b = run.Graph.DefineConstructor<Node>("B");
            var num = run.Graph.DefineRelation<long>("num");
            var ca = run.Graph.Root.Make(a.Of());
            var cb = run.Graph.Root.Make(b.Of());
            run.Graph.Root.Union(ca, cb);
            run.Graph.Root.Assume(num.Of(1L));
            run.Start();

            var defined = Record.Exception(() => run.Rules.DefineRule("equated", r =>
            {
                r.When(num.Of(r.Var<long>("n")), ((Term<Node>)ca).Is(cb));
                r.Then(run.Done.Of(2L));
            }));

            run.Finish();
            Assert.Null(defined);
        }

        /// <summary>
        /// Under a frozen root a global rule can only ever run in children, so the root's answer is the wrong one to
        /// refuse on: the child unions the two root classes, and the rule is accepted and matches there.
        /// </summary>
        [Fact]
        public void AGlobalRulesClassConstantsUnionedInAChildOfAFrozenRootMatchThere()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            schema.Union(a, b);
            schema.Assume(t.Tag.Of(a));

            var defined = Record.Exception(() => t.Rules.DefineRule("equated", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                r.Then(t.Num.Of(7L));
            }));

            Assert.Null(defined);
            schema.Run(Schedule.Saturate(t.Rules));
            Assert.True(schema.Check(t.Num.Of(7L)));
        }

        /// <summary>
        /// The same under a writable, idle root: the child of the writable root unions the two root classes, and the
        /// rule matches in the child and not at the root.
        /// </summary>
        [Fact]
        public void AGlobalRulesClassConstantsUnionedInAChildOfAWritableRootMatchThere()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var child = t.Root.CreateChild();
            child.Union(a, b);
            child.Assume(t.Tag.Of(a));

            var defined = Record.Exception(() => t.Rules.DefineRule("equated", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                r.Then(t.Num.Of(7L));
            }));

            Assert.Null(defined);
            child.Run(Schedule.Saturate(t.Rules));
            Assert.True(child.Check(t.Num.Of(7L)));
            Assert.False(t.Root.Check(t.Num.Of(7L)));
        }

        /// <summary>
        /// A local rule runs in its owner and its descendants: two classes that differ in the owner but are one class
        /// in a descendant match in the descendant.
        /// </summary>
        [Fact]
        public void ALocalRulesClassConstantsUnionedInADescendantMatchThere()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Freeze();
            var owner = t.Root.CreateChild();
            var inner = owner.CreateChild();
            inner.Union(a, b);
            inner.Assume(t.Tag.Of(a));

            var defined = Record.Exception(() => owner.DefineRule(t.Rules, "equated", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                r.Then(t.Num.Of(7L));
            }));

            Assert.Null(defined);
            inner.Run(Schedule.Saturate(t.Rules));
            Assert.True(inner.Check(t.Num.Of(7L)));
        }

        /// <summary>
        /// A rule definition may be made from any thread at any time, and its outcome is the rule's own: the same
        /// global rule body, defined once while another thread runs the writable root and once after, gets the same
        /// answer.
        /// </summary>
        [Fact]
        public async Task TheSameRuleGetsTheSameAnswerWhetherOrNotTheRootIsBusy()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var blocking = t.Graph.DefineRuleset("blocking");
            blocking.DefineRule("block", r =>
            {
                var n = r.Var<long>("n");
                r.When(t.Num.Of(n));
                r.Where(n, _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return true;
                });
            });
            t.Root.Assume(t.Num.Of(1L));
            var run = Task.Factory.StartNew(() => t.Root.Run(blocking), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            Exception? busy;
            try
            {
                busy = Record.Exception(() => t.Rules.DefineRule("while busy", r =>
                {
                    var x = r.Var<Node>("x");
                    r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                    r.Then(t.Num.Of(7L));
                }));
            }
            finally
            {
                release.Set();
            }

            await run;
            var idle = Record.Exception(() => t.Rules.DefineRule("while idle", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                r.Then(t.Num.Of(7L));
            }));

            Assert.True(busy?.GetType() == idle?.GetType(),
                $"while busy: {busy?.GetType().Name ?? "accepted"}; while idle: {idle?.GetType().Name ?? "accepted"} ({idle?.Message})");
        }

        /// <summary>
        /// A rule defined while the root is busy compares its equated constants at run time: it matches in a child that
        /// unions the two root classes, and not at the root.
        /// </summary>
        [Fact]
        public async Task TheRunTimeComparisonMatchesInAChildThatUnionsTheClasses()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var blocking = t.Graph.DefineRuleset("blocking");
            blocking.DefineRule("block", r =>
            {
                var n = r.Var<long>("n");
                r.When(t.Num.Of(n));
                r.Where(n, _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return true;
                });
            });
            t.Root.Assume(t.Num.Of(1L));
            var run = Task.Factory.StartNew(() => t.Root.Run(blocking), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            try
            {
                t.Rules.DefineRule("while busy", r =>
                {
                    var x = r.Var<Node>("x");
                    r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                    r.Then(t.Num.Of(7L));
                });
            }
            finally
            {
                release.Set();
            }

            await run;
            t.Root.Assume(t.Tag.Of(a));
            var child = t.Root.CreateChild();
            child.Union(a, b);
            child.Run(Schedule.Saturate(t.Rules));
            Assert.True(child.Check(t.Num.Of(7L)));
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.Check(t.Num.Of(7L)));
        }

        /// <summary>
        /// An <c>Equal</c> step of a global rule: no match at the root, then the root unions the two classes. The
        /// atom's row is old, so only the constants' change can make the rule run in full again.
        /// </summary>
        /// <param name="reversed">Whether the union names <c>lit</c> before <c>a</c>.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnEqualStepMatchesAfterTheRootUnionsItsConstants(bool reversed)
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var lit = t.Root.Make(t.Lit.Of(1L));
            t.Root.Assume(t.Tag.Of(a));
            t.Rules.DefineRule("through a variable", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(lit));
                r.Then(t.Num.Of(3L));
            });
            t.Rules.DefineRule("direct", r =>
            {
                r.When(((Term<Node>)a).Is(lit));
                r.Then(t.Num.Of(2L));
            });

            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.Check(t.Num.Of(3L)));
            Assert.False(t.Root.Check(t.Num.Of(2L)));

            if (reversed)
                t.Root.Union(lit, a);
            else
                t.Root.Union(a, lit);

            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(t.Num.Of(3L)));
            Assert.True(t.Root.Check(t.Num.Of(2L)));
        }

        /// <summary>
        /// The same in a child that ran before its union: its watermarks do not hide the new match.
        /// </summary>
        /// <param name="reversed">Whether the union names <c>lit</c> before <c>a</c>.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnEqualStepMatchesAfterAChildThatAlreadyRanUnionsItsConstants(bool reversed)
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var lit = t.Root.Make(t.Lit.Of(1L));
            t.Root.Assume(t.Tag.Of(a));
            t.Rules.DefineRule("through a variable", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(lit));
                r.Then(t.Num.Of(3L));
            });
            t.Rules.DefineRule("direct", r =>
            {
                r.When(((Term<Node>)a).Is(lit));
                r.Then(t.Num.Of(2L));
            });
            t.Root.Freeze();
            var child = t.Root.CreateChild();
            child.Run(Schedule.Saturate(t.Rules));
            Assert.False(child.Check(t.Num.Of(3L)));

            if (reversed)
                child.Union(lit, a);
            else
                child.Union(a, lit);

            child.Run(Schedule.Saturate(t.Rules));
            Assert.True(child.Check(t.Num.Of(3L)));
            Assert.True(child.Check(t.Num.Of(2L)));
        }

        /// <summary>
        /// A local rule's constants, different in the owner, unioned later in the owner itself after a run.
        /// </summary>
        [Fact]
        public void ALocalRulesEqualStepMatchesAfterTheOwnerUnionsItsConstants()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Freeze();
            var owner = t.Root.CreateChild();
            owner.Assume(t.Tag.Of(a));
            owner.DefineRule(t.Rules, "equated", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                r.Then(t.Num.Of(7L));
            });
            owner.Run(Schedule.Saturate(t.Rules));
            Assert.False(owner.Check(t.Num.Of(7L)));

            owner.Union(b, a);
            owner.Run(Schedule.Saturate(t.Rules));
            Assert.True(owner.Check(t.Num.Of(7L)));
        }

        /// <summary>
        /// A query's <c>Equal</c> step on a writable root, before and after the root unions the constants.
        /// </summary>
        [Fact]
        public void AQuerysEqualStepFollowsTheRootsUnions()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(t.Tag.Of(a));
            var query = t.Graph.DefineQuery("equated", q =>
            {
                var x = q.Var<Node>("x");
                q.When(t.Tag.Of(x), x.Is(a), x.Is(b));
            });
            Assert.Equal(0, t.Root.Count(query));
            t.Root.Union(b, a);
            Assert.Equal(1, t.Root.Count(query));
        }

        /// <summary>
        /// A child inherits the root's watermarks: the root ran the rule without a match, froze, and the child unions
        /// the constants. The inherited watermark was stored with the root's constants, so the child runs in full.
        /// </summary>
        [Fact]
        public void AnInheritedWatermarkDoesNotHideAChildsUnionOfTheConstants()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var lit = t.Root.Make(t.Lit.Of(1L));
            t.Root.Assume(t.Tag.Of(a));
            t.Rules.DefineRule("through a variable", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(lit));
                r.Then(t.Num.Of(3L));
            });
            t.Rules.DefineRule("direct", r =>
            {
                r.When(((Term<Node>)a).Is(lit));
                r.Then(t.Num.Of(2L));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.Check(t.Num.Of(3L)));
            t.Root.Freeze();

            var child = t.Root.CreateChild();
            child.Union(lit, a);
            child.Run(Schedule.Saturate(t.Rules));
            Assert.True(child.Check(t.Num.Of(3L)));
            Assert.True(child.Check(t.Num.Of(2L)));
        }

        /// <summary>
        /// A definition from another thread while the writable root is inside an engine call gets the definition's own
        /// answer, never the engine's domain error: the root cannot be read then, so the comparison of the global
        /// rule's equated class constants is left to run time, where the two distinct classes never match.
        /// </summary>
        [Fact]
        public async Task AGlobalRuleWithEquatedClassConstantsDefinedDuringARunGetsItsOwnAnswer()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            t.Rules.DefineRule("block", r =>
            {
                var n = r.Var<long>("n");
                r.When(t.Num.Of(n));
                r.Where(n, _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return true;
                });
            });
            t.Root.Assume(t.Num.Of(1L));
            var run = Task.Factory.StartNew(() => t.Root.Run(t.Rules), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            Exception? thrown;
            try
            {
                thrown = Record.Exception(() => t.Rules.DefineRule("distinct", r =>
                {
                    var x = r.Var<Node>("x");
                    r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                    r.Then(t.Num.Of(7L));
                }));
            }
            finally
            {
                release.Set();
            }

            await run;
            Assert.False(thrown is InvalidOperationException, thrown?.Message);
            Assert.Null(thrown);

            // Left to run time, the comparison keeps no match: a and b are two classes at the root.
            t.Root.Assume(t.Tag.Of(a), t.Tag.Of(b));
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.Check(t.Num.Of(7L)));
        }

        /// <summary>
        /// A rule's class constant that loses a union only in a child, after the child already ran the rule, still
        /// matches the rows the child had: a watermark is not used with changed constants. Both union directions, for
        /// global and local rules.
        /// </summary>
        /// <param name="local">Whether the rule is local to the frozen schema rather than global.</param>
        /// <param name="constantWins">Whether the query unions <c>b</c> with <c>a</c> rather than <c>a</c> with
        /// <c>b</c>.</param>
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void AConstantThatLosesAUnionOnlyInAChildStillMatchesOldRows(bool local, bool constantWins)
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            void Define(RuleBuilder r)
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a));
                r.Then(t.Num.Of(1L));
            }

            if (local)
                schema.DefineRule(t.Rules, "tagged a", Define);
            else
                t.Rules.DefineRule("tagged a", Define);

            schema.Freeze();
            var query = schema.CreateChild();
            query.Assume(t.Tag.Of(b));
            query.Run(Schedule.Saturate(t.Rules));
            Assert.False(query.Check(t.Num.Of(1L)));

            if (constantWins)
                query.Union(b, a);
            else
                query.Union(a, b);

            query.Run(Schedule.Saturate(t.Rules));
            Assert.True(query.Check(t.Num.Of(1L)));
        }

    }

}
