using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A random scoped program (run by <c>ScopeDifferentialTests</c> and <c>InheritanceOffEquivalenceTests</c>):
    /// tables, two tags, global rules, queries, and a sequence of scope commands that build a tree up to four deep with
    /// frozen prefixes, host writes into writable ancestors between child runs, and local rules with scoped atoms.
    /// </summary>
    /// <param name="Seed">The seed it was generated from.</param>
    /// <param name="Tables">The tables.</param>
    /// <param name="Globals">The global rules.</param>
    /// <param name="Queries">The queries (heads empty).</param>
    /// <param name="Commands">The commands, ending with a check.</param>
    /// <param name="Constructors">Whether the program has a constructor and node relation, which children
    /// write.</param>
    internal sealed record ScopedProgram(int Seed, ScopedTableSpec[] Tables, ScopedRuleSpec[] Globals, ScopedRuleSpec[] Queries,
        ScopedCommand[] Commands, bool Constructors)
    {

        /// <summary>
        /// The number of tags every program defines, <c>t0</c> and <c>t1</c>.
        /// </summary>
        public const int TagCount = 2;

        /// <summary>
        /// Generates a program from a seed.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <param name="constructors">Whether the program has constructors in children.</param>
        /// <returns>The program.</returns>
        public static ScopedProgram Generate(int seed, bool constructors) => new ScopedProgramGenerator(seed, constructors).Generate();

        /// <summary>
        /// Gets the parent of every scope, by number; the root's is -1.
        /// </summary>
        public int[] Parents()
        {
            var parents = new List<int> { -1 };
            foreach (var command in Commands.Where(c => c.Kind == ScopedCommandKind.Create))
                parents.Add(command.Parent);

            return parents.ToArray();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append("seed ").Append(Seed).Append(Constructors ? " with constructors" : "").Append('\n');
            for (var i = 0; i < Tables.Length; i++)
            {
                var table = Tables[i];
                builder.Append("T").Append(i).Append(' ').Append(table.Kind).Append(" (")
                    .Append(string.Join(", ", table.StringKeys.Select(k => k ? "String" : "i64"))).Append(')');
                if (table.Kind == ScopedTableKind.Function)
                    builder.Append(table.Max ? " max" : " min");

                builder.Append('\n');
            }

            foreach (var rule in Globals)
                builder.Append("global ").Append(rule).Append('\n');

            foreach (var query in Queries)
                builder.Append("query ").Append(query).Append('\n');

            foreach (var command in Commands)
                builder.Append(command).Append('\n');

            return builder.ToString();
        }

    }

}
