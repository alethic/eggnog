using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Probes of the R2 fixes (verification round 1): the N63 rule for class constants equated in a rule body. From R2 a
    /// global rule runs in every scope a run reaches, and a local rule in its owner's descendants (§6.5.2), and a child
    /// is a copy of its parent with its own unions (context = copy). Two class constants that differ at the root (or in
    /// the owner) but are one class in a child must match there, as they do when the comparison is left to run time; and
    /// whether a definition is refused must not depend on whether another thread happens to be using the root. V01-V04
    /// fail at 8d71c88 (the definition-time comparison refuses with "never matches"); V05 passes.
    /// </summary>
    public sealed class R2FixVerificationTests
    {

        /// <summary>
        /// A frozen root (the Cosmos layout): a global rule can only ever run in children, so the root's answer is the wrong
        /// one to refuse on. The child unions the two root classes; the rule must be accepted and match there.
        /// </summary>
        [Fact]
        public void V01_AGlobalRulesClassConstantsUnionedInAChildOfAFrozenRootMatchThere()
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
        /// The same under a writable, idle root: the child of the writable root unions the two root classes.
        /// </summary>
        [Fact]
        public void V02_AGlobalRulesClassConstantsUnionedInAChildOfAWritableRootMatchThere()
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
        /// A local rule runs in its owner and its descendants (§6.5.2): two classes that differ in the owner but are one class
        /// in a descendant must match in the descendant.
        /// </summary>
        [Fact]
        public void V03_ALocalRulesClassConstantsUnionedInADescendantMatchThere()
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
        /// D6: a rule definition may be made from any thread at any time, and its outcome is the rule's own. The same global
        /// rule body, defined once while another thread runs the writable root and once after, must get the same answer;
        /// today the first is accepted (comparison left to run time, N63) and the second refused as never matching.
        /// </summary>
        [Fact]
        public async Task V04_TheSameRuleGetsTheSameAnswerWhetherOrNotTheRootIsBusy()
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
        /// The run-time path N63 already has gives the sound answer: the rule accepted while the root was busy matches in a
        /// child that unions the two root classes, and not at the root. This passes, and shows the remedy for V01-V04.
        /// </summary>
        [Fact]
        public async Task V05_TheRunTimeComparisonMatchesInAChildThatUnionsTheClasses()
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

    }

}
