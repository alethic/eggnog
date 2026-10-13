using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Cosmos.Literal
{

    /// <summary>
    /// Compares a literal graph's table sizes (<see cref="Scope.CountRows(TableHandle)"/>) with egglog's
    /// <c>(print-size)</c> at the same point of a scenario, as the egglog CLI built from the vendored <c>ext/egglog</c>
    /// (commit 5e655cae) prints it.
    /// </summary>
    public static class PrintSize
    {

        /// <summary>
        /// Asserts that every table egglog lists has its row count in the root of <paramref name="theory"/>'s graph.
        /// </summary>
        /// <param name="theory">The theory.</param>
        /// <param name="egglog">egglog's output: <c>(And 2) (Assume 6) …</c>; its <c>contradiction</c> is the
        /// prelude's, which the port names <c>contradicts</c>.</param>
        public static void Matches(PrototypeTheory theory, string egglog)
        {
            var expected = Parse(egglog);
            var root = theory.Graph.Root;
            var actual = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in theory.Graph.Tables)
            {
                if (ReferenceEquals(table, theory.Graph.Contradiction))
                    continue;

                actual.Add(table.Name == "contradicts" ? "contradiction" : table.Name, root.CountRows(table));
            }

            var differences = expected
                .Where(e => actual.TryGetValue(e.Key, out var a) == false || a != e.Value)
                .Select(e => $"{e.Key}: egglog {e.Value}, eggnog {(actual.TryGetValue(e.Key, out var a) ? a : -1)}");
            Assert.Equal(string.Empty, string.Join("; ", differences));
            Assert.Equal(expected.Count, actual.Count);
        }

        /// <summary>
        /// Parses <c>(name n) (name n) …</c>.
        /// </summary>
        static Dictionary<string, long> Parse(string text) =>
            text.Split(')').Select(p => p.Trim().TrimStart('(').Trim()).Where(p => p.Length > 0).Select(p => p.Split(' '))
                .ToDictionary(p => p[0], p => long.Parse(p[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);

    }

}
