using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// The scope differential (§8.2): random scope trees up to four deep, with tags, frozen prefixes, host writes into
    /// writable ancestors between child runs, local rules with scoped atoms, and host queries with scoped atoms from tagged
    /// scopes and their descendants, all over value-only tables, run with scopes as contexts and checked against the
    /// program's literal encoding at one root (<see cref="LiteralOracle"/>). At every check each scope's view, its scoped
    /// reads, and every query's answer must equal the oracle's facts for its context constant. 300 seeds by default;
    /// <c>EGGNOG_SEEDS</c> raises it. A failing program prints itself.
    /// </summary>
    public sealed class ScopeDifferentialTests
    {

        /// <summary>
        /// The number of seeds without <c>EGGNOG_SEEDS</c>.
        /// </summary>
        const int DefaultSeeds = 300;

        [Fact]
        public void ScopesAgreeWithTheLiteralEncoding()
        {
            var failures = new List<string>();
            for (var seed = 0; seed < Seeds() && failures.Count < 3; seed++)
                if (Run(seed) is { } failure)
                    failures.Add(failure);

            Assert.True(failures.Count == 0, string.Join("\n\n", failures));
        }

        [Fact]
        public void TheProgramsCoverWhatTheSpecLists()
        {
            var programs = Enumerable.Range(0, DefaultSeeds).Select(s => ScopedProgram.Generate(s, false)).ToList();
            var creates = programs.SelectMany(p => p.Commands.Where(c => c.Kind == ScopedCommandKind.Create)).ToList();

            // Trees four deep, tags, frozen prefixes, writes into ancestors with children, local rules with scoped atoms.
            Assert.Contains(programs, p => Depths(p).Max() == 4);
            Assert.Contains(creates, c => c.Tag >= 0);
            Assert.Contains(programs, p => p.Commands.Count(c => c.Kind == ScopedCommandKind.Freeze) >= 2);
            Assert.Contains(creates, c => c.Rules.Any(r => r.Body.Any(a => a.Tag >= 0)));
            Assert.Contains(programs, WritesAnAncestorBetweenChildRuns);
            Assert.Contains(programs, p => p.Queries.Any(q => q.Body.Any(a => a.Tag >= 0)));
            Assert.All(programs, p => Assert.Equal(ScopedCommandKind.Check, p.Commands[^1].Kind));
        }

        /// <summary>
        /// Runs one seed; returns a description of the first disagreement, or <c>null</c>.
        /// </summary>
        static string? Run(int seed)
        {
            var program = ScopedProgram.Generate(seed, false);
            var scoped = new ScopedDriver(program, inheritWatermarks: true);
            var oracle = new LiteralOracle(program);
            for (var i = 0; i < program.Commands.Length; i++)
            {
                var command = program.Commands[i];
                Dictionary<int, string>? actual;
                Dictionary<int, string>? expected;
                try
                {
                    actual = scoped.Execute(command, named: false);
                    expected = oracle.Execute(command);
                }
                catch (Exception exception)
                {
                    return $"seed {seed}, command {i} ({command}) threw {exception}\n{program}";
                }

                if (actual is null || expected is null)
                    continue;

                foreach (var (scope, rendering) in expected)
                {
                    if (string.Equals(actual[scope], rendering, StringComparison.Ordinal) == false)
                    {
                        return $"seed {seed}, check at command {i}, scope s{scope.ToString(CultureInfo.InvariantCulture)} ({scoped.Scopes[scope].Path}):\n"
                            + $"scoped:\n{actual[scope]}literal:\n{rendering}\nprogram:\n{program}";
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets each scope's depth.
        /// </summary>
        static int[] Depths(ScopedProgram program)
        {
            var parents = program.Parents();
            return parents.Select((_, s) =>
            {
                var depth = 0;
                for (var p = parents[s]; p >= 0; p = parents[p])
                    depth++;

                return depth;
            }).ToArray();
        }

        /// <summary>
        /// Gets whether a program writes a writable scope after a child of it was created, then runs a scope.
        /// </summary>
        static bool WritesAnAncestorBetweenChildRuns(ScopedProgram program)
        {
            var parents = program.Parents();
            var created = new HashSet<int> { 0 };
            for (var i = 0; i < program.Commands.Length; i++)
            {
                var command = program.Commands[i];
                if (command.Kind == ScopedCommandKind.Create)
                    created.Add(command.Scope);

                if (command.Kind == ScopedCommandKind.Assume && created.Any(s => parents[s] == command.Scope)
                    && program.Commands.Skip(i + 1).Any(c => c.Kind is ScopedCommandKind.Run or ScopedCommandKind.Check))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The number of seeds: <c>EGGNOG_SEEDS</c>, or <see cref="DefaultSeeds"/>.
        /// </summary>
        static int Seeds() =>
            int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_SEEDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeds) && seeds > 0
                ? seeds
                : DefaultSeeds;

    }

}
