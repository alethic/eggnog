using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review probes of the R1b API that pass on the reviewed sandbox: edge cases of queries, rows, observers and limits
    /// that the milestone's own tests do not cover.
    /// </summary>
    public sealed class R1bApiCoverageTests
    {

        /// <summary>
        /// §6.11: CountRows is print-size, and Rows lists the same rows, after congruence collapsed two.
        /// </summary>
        [Fact]
        public void RowsAndCountRowsAgreeAfterCongruence()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Make(t.Wrap.Of(a));
            t.Root.Make(t.Wrap.Of(b));
            Assert.Equal(2, t.Root.CountRows(t.Wrap));

            t.Root.Union(a, b);

            Assert.Equal(1, t.Root.CountRows(t.Wrap));
            Assert.Single(t.Root.Rows(t.Wrap));
        }

        /// <summary>
        /// §6.6.4 and §6.11: content order is numeric, negatives and the extremes included.
        /// </summary>
        [Fact]
        public void ContentOrderIsNumericForNegativeNumbers()
        {
            var t = new NodeTheory();
            Var<long> x = null!;
            var query = t.Graph.DefineQuery("nums", q =>
            {
                x = q.Var<long>("x");
                q.When(t.Num.Of(x));
            });
            t.Root.Assume(t.Num.Of(3L), t.Num.Of(-5L), t.Num.Of(long.MinValue), t.Num.Of(-1L), t.Num.Of(long.MaxValue));
            var seen = new List<long>();
            t.Root.ForEachMatch(query, m =>
            {
                seen.Add(m.Get(x));
                return true;
            });

            Assert.Equal(new[] { long.MinValue, -5L, -1L, 3L, long.MaxValue }, seen);
            Assert.Equal(seen, t.Root.Rows(t.Num).Select(r => (long)r.Keys[0]));
        }

        /// <summary>
        /// D42: the row limit is checked before the first iteration too.
        /// </summary>
        [Fact]
        public void TheRowLimitIsCheckedBeforeTheFirstIteration()
        {
            var t = new NodeTheory();
            var facts = new Fact[100];
            for (var i = 0; i < facts.Length; i++)
                facts[i] = t.Num.Of((long)i);

            t.Root.Assume(facts);
            t.Rules.DefineRule("copy", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Tag.Of(t.Lit.Of(x)));
            });

            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { RowLimit = 10 });

            Assert.Equal(RunStopReason.RowLimit, report.StopReason);
            Assert.Equal(0, report.Iterations);
        }

        /// <summary>
        /// D49: the first call of a new query plans it, and still leaves an extractor usable.
        /// </summary>
        [Fact]
        public void AFirstCallOfANewQueryLeavesAnExtractorUsable()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.Wrap.Of(t.A.Of()));
            var extractor = t.Root.CreateExtractor();
            var fresh = t.Graph.DefineQuery("wraps", q =>
            {
                var (n, m) = q.Vars<Node, Node>("n", "m");
                q.When(n.Is(t.Wrap.Of(m)));
            });

            Assert.Equal(1, t.Root.Count(fresh));
            Assert.Equal("(Wrap (A))", extractor.Extract(a).ToString());
        }

        /// <summary>
        /// D39, §6.6.4: a query constant made by a Write batch that did not commit is refused at each call.
        /// </summary>
        [Fact]
        public void AnUncommittedBatchsClassIsRefusedByAQuery()
        {
            var t = new NodeTheory();
            Node made = default;
            Assert.Throws<InvalidOperationException>(() => t.Root.Write(w =>
            {
                made = w.Make(t.A.Of());
                throw new InvalidOperationException("abort");
            }));
            var query = t.Graph.DefineQuery("wrapped", q =>
            {
                var n = q.Var<Node>("n");
                q.When(n.Is(t.Wrap.Of(made)));
            });

            var error = Assert.Throws<ArgumentException>(() => t.Root.Count(query));
            Assert.Contains("did not commit", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// §6.11, D49: a query that fails inside an observer ends the run with its RuleEvaluationException and faults
        /// nothing; a query disposed inside an observer is freed at once.
        /// </summary>
        [Fact]
        public void QueriesInsideAnObserverFailWithoutFaultingAndDisposeAtOnce()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("grow", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 3L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            t.Root.Assume(t.Num.Of(0L));
            var boom = t.Graph.DefineQuery("boom", q =>
            {
                var x = q.Var<long>("x");
                q.When(t.Num.Of(x));
                q.Where(x, v => v == 1 ? throw new InvalidOperationException("one") : true);
            });
            Assert.Throws<RuleEvaluationException>(() => t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = step => step.Scope.Count(boom),
            }));
            Assert.False(t.Root.IsFaulted);

            var plain = t.Graph.DefineQuery("plain", q => q.When(t.Num.Of(q.Var<long>("x"))));
            Assert.Equal(2, t.Root.Count(plain));
            var description = plain.Compiled.Variants.Default!.Description;
            Assert.Equal(RunStopReason.Completed, t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { Observer = _ => plain.Dispose() }).StopReason);
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(description));
            Assert.Throws<ObjectDisposedException>(() => t.Root.Count(plain));
        }

        /// <summary>
        /// A ForEachMatch callback that throws releases the query and the guard: Dispose frees at once, and another thread
        /// may read the scope.
        /// </summary>
        [Fact]
        public void ACallbackThatThrowsReleasesTheQueryAndTheGuard()
        {
            var t = new NodeTheory();
            var query = t.Graph.DefineQuery("q", q => q.When(t.Num.Of(q.Var<long>("x"))));
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L));
            Assert.Throws<InvalidOperationException>(() => t.Root.ForEachMatch(query, _ => throw new InvalidOperationException("callback")));
            Assert.Equal(0, query.Compiled.Users);

            var description = query.Compiled.Variants.Default!.Description;
            query.Dispose();
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(description));
            long other = -1;
            var thread = new Thread(() => other = t.Root.CountRows(t.Num));
            thread.Start();
            thread.Join();
            Assert.Equal(2, other);
        }

        /// <summary>
        /// §6.11: rows of a nullary function, of the contradiction relation, and of a set-valued function keyed by an enum
        /// render as s-expressions.
        /// </summary>
        [Fact]
        public void RowsOfNullaryTablesSetsAndEnumsRender()
        {
            var t = new NodeTheory();
            t.Graph.Values.RegisterEnum<Shade>();
            var f = t.Graph.DefineFunction<long>("f");
            var dom = t.Graph.DefineFunction<Shade, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            t.Root.Assume(f.Of().Is(5L), t.Graph.Contradiction.Of(), dom.Of(default(Shade)).Is(ValueSet.Of("b", "a")));

            Assert.Equal("(f) = 5", t.Root.Rows(f).Single().ToString());
            Assert.Equal("(contradiction)", t.Root.Rows(t.Graph.Contradiction).Single().ToString());
            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"(dom {default(Shade)}) = (set-of \"a\" \"b\")"), t.Root.Rows(dom).Single().ToString());
        }

    }

}
