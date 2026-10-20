using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Support;

namespace Eggnog.Tests.Subsumption
{

    /// <summary>
    /// The theory of the inheritance differential in <see cref="SubsumeDifferentialTests"/>: the <see cref="MathTheory"/>
    /// with a subsumable relation <c>fact</c>, a relation <c>both</c>, and rules that subsume and rules that do not, so that
    /// a scope's rules both match and stop matching rows its ancestors wrote.
    /// </summary>
    public sealed class SubsumeWorld
    {

        /// <summary>
        /// Defines the world in a new graph.
        /// </summary>
        /// <param name="inherit">Whether children inherit their ancestors' watermarks.</param>
        public SubsumeWorld(bool inherit)
        {
            Theory = new MathTheory();
            Theory.Graph.InheritWatermarks = inherit;
            Fact = Theory.Graph.DefineRelation<long>("fact", MathTheory.Subsumable);
            Both = Theory.Graph.DefineRelation<long>("both");
            var t = Theory;
            t.DefineSee();
            t.Rules.DefineRule("pair", r =>
            {
                var n = r.Var<long>("n");
                r.When(Fact.Of(n), t.Seen.Of(n));
                r.Then(Both.Of(n));
            });
            t.Rules.DefineRule("commute", r =>
            {
                var (a, b) = r.Vars<Node, Node>("a", "b");
                r.Rewrite(t.Add.Of(a, b), t.Add.Of(b, a));
            });
            t.Rules.DefineRule("shrink", r =>
            {
                var (a, b) = r.Vars<Node, Node>("a", "b");
                r.Rewrite(t.Mul.Of(a, b), t.Add.Of(a, b), subsume: true);
            });
            t.Rules.DefineRule("prune", r =>
            {
                var n = r.Var<long>("n");
                r.When(Both.Of(n), Eggnog.Fact.Exists(t.Num.Of(n)));
                r.Where(n, v => v % 2 == 0);
                r.Subsume(Fact.Of(n));
                r.Subsume(t.Num.Of(n));
            });
            t.Rules.DefineRule("count", r =>
            {
                var n = r.Var<long>("n");
                r.When(Fact.Of(n));
                r.Make("v", t.Num.Of(n));
            });
        }

        /// <summary>Gets the theory.</summary>
        public MathTheory Theory { get; }

        /// <summary>Gets the subsumable relation <c>fact</c>.</summary>
        public Relation<long> Fact { get; }

        /// <summary>Gets the relation <c>both</c>: a number that is a <c>fact</c> and <c>seen</c>.</summary>
        public Relation<long> Both { get; }

        /// <summary>
        /// Renders what a scope holds without naming a class: each relation's rows with their subsumption, each
        /// constructor's row count and subsumed count, and which of the numbers 0 to 5 are equal.
        /// </summary>
        /// <param name="scope">The scope.</param>
        /// <returns>The rendering.</returns>
        public string Snapshot(Scope scope)
        {
            var t = Theory;
            var parts = new List<string>();
            foreach (var table in new TableHandle[] { Fact, Both, t.Seen })
                parts.Add(table.Name + ":" + string.Join(",", scope.Rows(table).Select(r => r.Keys[0] + (r.Subsumed ? "s" : ""))));

            foreach (var table in new TableHandle[] { t.Num, t.Add, t.Mul })
                parts.Add(table.Name + ":" + scope.CountRows(table) + "/" + scope.Rows(table).Count(r => r.Subsumed));

            var equal = new List<string>();
            for (var i = 0L; i < 6; i++)
                for (var j = i + 1; j < 6; j++)
                    if (scope.TryGet(t.Num.Of(i), out var a) && scope.TryGet(t.Num.Of(j), out var b) && scope.AreEqual(a, b))
                        equal.Add(i + "=" + j);

            parts.Add("equal:" + string.Join(",", equal));
            return string.Join(" | ", parts);
        }

    }

}
