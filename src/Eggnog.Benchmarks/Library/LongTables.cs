using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// The synthetic theory of the library's rule benchmarks: <c>T</c> relations <c>p0 … p(T−1)</c> over <c>long</c>
    /// that rules read, one head relation <c>q_i</c> per <c>p_i</c>, a relation <c>idle</c> that no rule reads, and one
    /// ruleset. The root holds <c>p_i(x)</c> for every <c>x</c> in <c>[0, <see cref="Rows"/>)</c>, except that
    /// <c>p0</c> lacks <see cref="Missing"/>, so asserting <c>p0(Missing)</c> is the one write that makes rules fire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rule <c>r</c> of <c>A</c> body atoms reads <see cref="BodyTables"/>: <c>p_h</c> with <c>h = r mod T</c>, then
    /// <c>A − 1</c> consecutive tables after an offset that cycles with <c>r / T</c>; all atoms share the variable
    /// <c>x</c>, and the head is <c>q_h(x)</c>. Every body includes its head's own <c>p_h</c>, so <c>q_0(Missing)</c>
    /// holds only where <c>p0(Missing)</c> was asserted. Semi-naive evaluation gives a rule one variant per body atom,
    /// so the cost of an idle rule scales with <c>A</c>.
    /// </para>
    /// <para>
    /// <see cref="Expected"/> is the reference model of the <c>q</c> tables after saturation, from the rule bodies
    /// alone: <c>q_h</c> holds, for each of its rules, every <c>x</c> its body's tables share, which is all of
    /// <c>[0, Rows)</c> for a body without <c>p0</c> and <c>[0, Rows)</c> less <see cref="Missing"/> for one with it.
    /// </para>
    /// </remarks>
    internal sealed class LongTables
    {

        /// <summary>
        /// The rows of each <c>p</c> relation at the root (<c>p0</c> has one fewer).
        /// </summary>
        public const int Rows = 1_000;

        /// <summary>
        /// The value <c>p0</c> lacks at the root.
        /// </summary>
        public const long Missing = Rows - 1;

        /// <summary>
        /// Defines the theory in a new graph and loads the root; the root is left writable.
        /// </summary>
        /// <param name="tables">The number of <c>p</c> relations, at least 4.</param>
        /// <param name="options">The graph's options; <c>null</c> for the default.</param>
        public LongTables(int tables, EGraphOptions? options = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(tables, 4);
            Graph = new EGraph(options);
            P = new Relation<long>[tables];
            Q = new Relation<long>[tables];
            for (var i = 0; i < tables; i++)
            {
                P[i] = Graph.DefineRelation<long>($"p{i}");
                Q[i] = Graph.DefineRelation<long>($"q{i}");
            }

            Idle = Graph.DefineRelation<long>("idle");
            Rules = Graph.DefineRuleset("rules");
            Graph.Root.Write(w =>
            {
                for (var i = 0; i < P.Length; i++)
                    for (var x = 0L; x < Rows; x++)
                        if (i != 0 || x != Missing)
                            w.Assume(P[i].Of(x));
            });
        }

        /// <summary>
        /// Gets the graph.
        /// </summary>
        public EGraph Graph { get; }

        /// <summary>
        /// Gets the relations rules read.
        /// </summary>
        public Relation<long>[] P { get; }

        /// <summary>
        /// Gets the relations rules write: <c>Q[h]</c> is the head of rules whose first atom is <c>P[h]</c>.
        /// </summary>
        public Relation<long>[] Q { get; }

        /// <summary>
        /// Gets the relation no rule reads.
        /// </summary>
        public Relation<long> Idle { get; }

        /// <summary>
        /// Gets the ruleset every rule belongs to.
        /// </summary>
        public Ruleset Rules { get; }

        /// <summary>
        /// Gets the <c>p</c> tables rule <paramref name="rule"/> reads, its head's table first.
        /// </summary>
        /// <param name="rule">The rule's index.</param>
        /// <param name="tables">The number of <c>p</c> relations.</param>
        /// <param name="atoms">The body atoms, from 1 to <paramref name="tables"/>.</param>
        /// <returns>The distinct table indexes.</returns>
        public static int[] BodyTables(int rule, int tables, int atoms)
        {
            var head = rule % tables;
            var offset = 1 + rule / tables % (tables - atoms + 1);
            var body = new int[atoms];
            body[0] = head;
            for (var a = 1; a < atoms; a++)
                body[a] = (head + offset + a - 1) % tables;

            return body;
        }

        /// <summary>
        /// Defines rule <paramref name="rule"/>'s body and head on a rule builder: <c>p_b(x)</c> for each body table,
        /// then <c>q_h(x)</c>.
        /// </summary>
        /// <param name="builder">The rule builder.</param>
        /// <param name="rule">The rule's index.</param>
        /// <param name="atoms">The body atoms.</param>
        /// <exception cref="InvalidOperationException">The body's tables are not distinct.</exception>
        public void Define(RuleBuilder builder, int rule, int atoms)
        {
            var body = BodyTables(rule, P.Length, atoms);
            if (body.Distinct().Count() != atoms)
                throw new InvalidOperationException($"Rule {rule} of {atoms} atoms over {P.Length} tables reads tables {string.Join(", ", body)}; expected {atoms} distinct tables.");
            var x = builder.Var<long>("x");
            var facts = new Fact[body.Length];
            for (var a = 0; a < body.Length; a++)
                facts[a] = P[body[a]].Of(x);

            builder.When(facts);
            builder.Then(Q[body[0]].Of(x));
        }

        /// <summary>
        /// Gets the reference model's row count of each <c>q</c> table after the first <paramref name="rules"/> rules
        /// saturate the root's rows.
        /// </summary>
        /// <param name="rules">The number of rules.</param>
        /// <param name="atoms">The body atoms of each.</param>
        /// <returns>The row count of each <c>q</c> table.</returns>
        public long[] Expected(int rules, int atoms)
        {
            var counts = new long[Q.Length];
            for (var r = 0; r < rules; r++)
            {
                var body = new List<int>(BodyTables(r, P.Length, atoms));
                var rows = body.Contains(0) ? Rows - 1 : Rows;
                counts[body[0]] = Math.Max(counts[body[0]], rows);
            }

            return counts;
        }

        /// <summary>
        /// Checks the <c>q</c> tables of <paramref name="scope"/> against <see cref="Expected"/>.
        /// </summary>
        /// <param name="scope">The scope whose rules saturated.</param>
        /// <param name="rules">The number of rules.</param>
        /// <param name="atoms">The body atoms of each.</param>
        /// <exception cref="InvalidOperationException">A table's row count differs from the model.</exception>
        public void CheckSaturated(Scope scope, int rules, int atoms)
        {
            var expected = Expected(rules, atoms);
            for (var i = 0; i < Q.Length; i++)
                if (scope.CountRows(Q[i]) is var actual && actual != expected[i])
                    throw new InvalidOperationException($"{Q[i].Name} holds {actual} rows; the model holds {expected[i]}.");
        }

    }

}
