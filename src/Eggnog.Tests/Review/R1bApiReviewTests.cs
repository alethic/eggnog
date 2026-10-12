using System;
using System.Collections.Generic;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review probes of the R1b API (queries, schedules, run steps) against the rule-layer spec and the conventions. Each
    /// test asserts what the spec or the conventions require, and each fails on the reviewed sandbox.
    /// </summary>
    public sealed class R1bApiReviewTests
    {

        /// <summary>
        /// A graph whose <c>num</c> relation holds <paramref name="rows"/> numbers, and a query over it.
        /// </summary>
        static (NodeTheory T, Query Query, Var<long> X) Numbers(int rows)
        {
            var t = new NodeTheory();
            Var<long> x = null!;
            var query = t.Graph.DefineQuery("nums", q =>
            {
                x = q.Var<long>("x");
                q.When(t.Num.Of(x));
            });
            var facts = new Fact[rows];
            for (var i = 0; i < rows; i++)
                facts[i] = t.Num.Of((long)i);

            t.Root.Assume(facts);
            return (t, query, x);
        }

        /// <summary>
        /// Bytes allocated on this thread by a second call of <paramref name="action"/> (the graph is serial, so the engine
        /// runs on this thread).
        /// </summary>
        static long Allocated(Action action)
        {
            action();
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>
        /// The engine's own allocation for running a query's rule to completion with a collector that keeps nothing.
        /// </summary>
        static long EngineOnly(NodeTheory t, Query query) => Allocated(() =>
        {
            var compiled = query.Compiled;
            var variant = t.Root.Engine.PrepareVariant(compiled.Variants, compiled.Program.WrittenIdConstants, false);
            t.Root.Runtime.QueryMatches = null;
            t.Root.Engine.RunQuery(variant);
        });

        /// <summary>
        /// Conventions ("No allocation per row, per match or per probe on hot paths"): Count must not allocate per match
        /// where the engine run it wraps does not. It allocates about 415 bytes per match (TupleBuffer's List per tuple,
        /// two buffers, the hash index rebuilt after the sort) against the engine's 1.
        /// </summary>
        [Fact]
        public void CountAllocatesNoMoreThanTheEnginePerMatch()
        {
            const int small = 1_000;
            const int large = 21_000;
            var (a, qa, _) = Numbers(small);
            var (b, qb, _) = Numbers(large);

            var count = (Allocated(() => b.Root.Count(qb)) - Allocated(() => a.Root.Count(qa))) / (double)(large - small);
            var engine = (EngineOnly(b, qb) - EngineOnly(a, qa)) / (double)(large - small);

            Assert.True(count <= engine + 16, $"Count allocates {count:0.#} bytes per match; the engine run allocates {engine:0.#}.");
        }

        /// <summary>
        /// As <see cref="CountAllocatesNoMoreThanTheEnginePerMatch"/>, for ForEachMatch with a callback that reads each
        /// match.
        /// </summary>
        [Fact]
        public void ForEachMatchAllocatesNoMoreThanTheEnginePerMatch()
        {
            const int small = 1_000;
            const int large = 21_000;
            var (a, qa, xa) = Numbers(small);
            var (b, qb, xb) = Numbers(large);
            long sink = 0;

            var each = (Allocated(() => b.Root.ForEachMatch(qb, m => { sink += m.Get(xb); return true; }))
                - Allocated(() => a.Root.ForEachMatch(qa, m => { sink += m.Get(xa); return true; }))) / (double)(large - small);
            var engine = (EngineOnly(b, qb) - EngineOnly(a, qa)) / (double)(large - small);

            Assert.True(each <= engine + 16, $"ForEachMatch allocates {each:0.#} bytes per match; the engine run allocates {engine:0.#}.");
            Assert.NotEqual(0, sink);
        }

        /// <summary>
        /// Any stops the engine early (§6.6.4), and an early-stopped engine run does not keep its serial join
        /// (<c>Database.RunRulesSerial</c>), so every Any rebuilds the join and its table-sized leaf buffer: about 25 bytes per
        /// row of the scanned table, where a run to completion allocates about 1. An engine-side cost of the spec's
        /// mechanism, not of the rule layer's code.
        /// </summary>
        [Fact]
        public void AnyAllocatesNoMoreThanAFullEngineRunPerRow()
        {
            const int small = 1_000;
            const int large = 21_000;
            var (a, qa, _) = Numbers(small);
            var (b, qb, _) = Numbers(large);

            var any = (Allocated(() => b.Root.Any(qb)) - Allocated(() => a.Root.Any(qa))) / (double)(large - small);
            var engine = (EngineOnly(b, qb) - EngineOnly(a, qa)) / (double)(large - small);

            Assert.True(any <= engine + 4, $"Any allocates {any:0.#} bytes per row; a full engine run allocates {engine:0.#}.");
        }

        /// <summary>
        /// §6.13 (a bad host read is an ArgumentException with nothing written) and §6.5.3 (Run validates before any work):
        /// an Until condition that no host check can test (a variable) must be refused before the run writes. Schedule.Until
        /// and Run accept it; it fails only when the node executes, after the sequence's first step has written.
        /// </summary>
        [Fact]
        public void AnUncheckableUntilConditionIsRefusedBeforeAnyWork()
        {
            var t = new NodeTheory();
            var seed = t.Graph.DefineRuleset("seed");
            seed.DefineRule("one", r => r.Then(t.Num.Of(1L)));
            Var<long> leaked = null!;
            t.Graph.DefineQuery("leak", q =>
            {
                leaked = q.Var<long>("v");
                q.When(t.Num.Of(leaked));
            });

            Schedule schedule;
            try
            {
                schedule = Schedule.Sequence(seed, Schedule.Until(t.Rules, leaked.Is(5L)));
            }
            catch (ArgumentException)
            {
                return;
            }

            Assert.Throws<ArgumentException>(() => t.Root.Run(schedule));
            Assert.False(t.Root.Check(t.Num.Of(1L)), "The run wrote num(1) before refusing its own Until condition.");
        }

        /// <summary>
        /// As above, for an Until condition over another graph's table: Run refuses another graph's ruleset before any work,
        /// but another graph's table in an Until condition only when the node executes.
        /// </summary>
        [Fact]
        public void AnUntilConditionOfAnotherGraphIsRefusedBeforeAnyWork()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            var seed = t.Graph.DefineRuleset("seed");
            seed.DefineRule("one", r => r.Then(t.Num.Of(1L)));
            var schedule = Schedule.Sequence(seed, Schedule.Until(t.Rules, other.Num.Of(5L)));

            Assert.Throws<ArgumentException>(() => t.Root.Run(schedule));
            Assert.False(t.Root.Check(t.Num.Of(1L)), "The run wrote num(1) before refusing another graph's table.");
        }

        /// <summary>
        /// Hard to misuse: Until with no condition is accepted and never runs its body, so a forgotten condition silently
        /// turns the step into a no-op that reports a saturated, completed run.
        /// </summary>
        [Fact]
        public void AnUntilWithoutAConditionIsRefused()
        {
            var t = new NodeTheory();
            Assert.Throws<ArgumentException>(() => Schedule.Until(t.Rules));
        }

        /// <summary>
        /// §3.5 QueryMatch.Get: "a class is canonical in the queried scope". ForEachMatch documents that the callback may call
        /// the scope; after a union made from the callback, later matches return classes that are not canonical.
        /// </summary>
        [Fact]
        public void LaterMatchesAreCanonicalAfterTheCallbackWrites()
        {
            var t = new NodeTheory();
            Var<Node> n = null!;
            var lits = t.Graph.DefineQuery("lits", q =>
            {
                n = q.Var<Node>("n");
                q.When(Fact.Exists(t.Lit.Of(q.Var<long>("x"))), n.Is(t.Lit.Of(q.Var<long>("y"))));
            });
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            var classes = new List<Node>();
            t.Root.ForEachMatch(lits, m =>
            {
                classes.Add(m.Get(n));
                if (classes.Count == 1)
                    t.Root.Union(one, two);

                return true;
            });

            foreach (var c in classes)
                Assert.Equal(t.Root.Find(c), c);
        }

        /// <summary>
        /// RunStep is documented immutable, but its Rules is the executor's List: a downcast (from an observer, say)
        /// mutates the step that RunReport.Steps keeps.
        /// </summary>
        [Fact]
        public void RunStepRulesCannotBeMutatedThroughACast()
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
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                RecordSteps = true,
                Observer = step =>
                {
                    if (step.Rules is IList<RuleStatistics> { IsReadOnly: false } list)
                        list.Clear();
                },
            });

            Assert.Single(report.Steps[0].Rules);
        }

    }

}
