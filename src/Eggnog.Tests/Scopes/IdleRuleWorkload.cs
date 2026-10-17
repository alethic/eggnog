using System;
using System.Collections.Generic;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A frozen container with local rules, queried through transient children: the workload of the idle-rule
    /// allocation probe. Ten relations <c>p0</c>..<c>p9</c> over <see cref="long"/> hold 1,000 rows each at the root,
    /// <c>p_i</c> the values <c>100 i</c> to <c>100 i + 999</c>, and the root is frozen. The container defines
    /// <see cref="RuleCount"/> local rules <c>p_i(x), p_j(x) =&gt; q_i(x)</c> with <c>i ≠ j</c>, runs them to
    /// saturation and freezes.
    /// </summary>
    /// <remarks>
    /// A query is a child of the container, one <c>Assume</c>, <c>Run(Saturate)</c>, one <c>Check</c> and
    /// <c>Dispose</c>. An idle query asserts into <c>other</c>, which no rule reads, so every rule's variants find no
    /// new rows and the run takes one iteration. An active query asserts <c>p0(1050)</c>, which the tables
    /// <c>p1</c>..<c>p9</c> also hold, so the rules over <c>p0</c> derive rows and the run takes two iterations.
    /// </remarks>
    public sealed class IdleRuleWorkload
    {

        /// <summary>
        /// The value an active query asserts into <c>p0</c>.
        /// </summary>
        public const long ActiveValue = 1050;

        readonly Relation<long>[] p = new Relation<long>[10];
        readonly Relation<long>[] q = new Relation<long>[10];
        readonly Relation<long> other;
        readonly Ruleset rules;
        readonly Scope container;

        /// <summary>
        /// Builds the graph, the container and its rules.
        /// </summary>
        /// <param name="ruleCount">The number of local rules.</param>
        /// <param name="layered">Whether the container also holds rows of every <c>p_i</c> (ten values from
        /// <c>100 i + 2000</c>), so the tables the rules read lie in two layers in a query.</param>
        public IdleRuleWorkload(int ruleCount, bool layered = false)
        {
            RuleCount = ruleCount;
            Graph = new EGraph();
            for (var i = 0; i < p.Length; i++)
            {
                p[i] = Graph.DefineRelation<long>($"p{i}");
                q[i] = Graph.DefineRelation<long>($"q{i}");
            }

            other = Graph.DefineRelation<long>("other");
            rules = Graph.DefineRuleset("rules");
            var facts = new List<Fact>();
            for (var i = 0; i < p.Length; i++)
                for (var v = 0; v < 1000; v++)
                    facts.Add(p[i].Of(100L * i + v));

            Graph.Root.Assume(facts.ToArray());
            Graph.Root.Freeze();
            container = Graph.Root.CreateChild();
            if (layered)
            {
                facts.Clear();
                for (var i = 0; i < p.Length; i++)
                    for (var v = 0; v < 10; v++)
                        facts.Add(p[i].Of(100L * i + 2000 + v));

                container.Assume(facts.ToArray());
            }

            for (var k = 0; k < ruleCount; k++)
            {
                var head = q[k % 10];
                var first = p[k % 10];
                var second = p[(k % 10 + 1 + k / 10 % 9) % 10];
                container.DefineRule(rules, $"r{k}", r =>
                {
                    var x = r.Var<long>("x");
                    r.When(first.Of(x), second.Of(x));
                    r.Then(head.Of(x));
                });
            }

            container.Run(Schedule.Saturate(rules));
            container.Freeze();
        }

        /// <summary>
        /// Gets the graph.
        /// </summary>
        public EGraph Graph { get; }

        /// <summary>
        /// Gets the number of local rules.
        /// </summary>
        public int RuleCount { get; }

        /// <summary>
        /// Runs one query.
        /// </summary>
        /// <param name="active">Whether the query asserts into a table the rules read.</param>
        /// <returns>The run's iterations and whether the check held.</returns>
        public (int Iterations, bool Checked) Query(bool active)
        {
            using var child = container.CreateChild();
            child.Assume(active ? p[0].Of(ActiveValue) : other.Of(1L));
            var report = child.Run(Schedule.Saturate(rules));
            var held = child.Check(active ? q[0].Of(ActiveValue) : other.Of(1L));
            return (report.Iterations, held);
        }

        /// <summary>
        /// Runs an idle query whose child runs the rules twice, and measures the bytes the current thread allocates in
        /// each run; the graph is serial, so a run allocates on this thread only.
        /// </summary>
        /// <returns>The bytes of the child's first run and of its second.</returns>
        public (long First, long Second) BytesPerRun()
        {
            using var child = container.CreateChild();
            child.Assume(other.Of(1L));
            var before = GC.GetAllocatedBytesForCurrentThread();
            child.Run(Schedule.Saturate(rules));
            var first = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            child.Run(Schedule.Saturate(rules));
            return (first, GC.GetAllocatedBytesForCurrentThread() - before);
        }

    }

}
