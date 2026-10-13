using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// Watermark inheritance changes nothing but the work (§8.2, colors §10.2, I31): random scoped programs with
    /// constructors in children run with the graph's <c>InheritWatermarks</c> off and on, and every scope's view must be
    /// equal, modulo renaming of classes, at every check; each seed's seeded match total must be at most its unseeded one,
    /// and the total over all seeds below. The driver uses every body variable of a rule (a filter or disequality that
    /// keeps every match), so match counts are distinct full bindings in both runs, as colors §21.9 does with
    /// <c>ColorSchema.Keep</c>. 300 seeds by default; <c>EGGNOG_SEEDS</c> raises it.
    /// </summary>
    public sealed class InheritanceOffEquivalenceTests
    {

        /// <summary>
        /// The number of seeds without <c>EGGNOG_SEEDS</c>.
        /// </summary>
        const int DefaultSeeds = 300;

        [Fact]
        public void SeededAndUnseededRunsAgree()
        {
            var failures = new List<string>();
            long seeded = 0, unseeded = 0;
            for (var seed = 0; seed < Seeds() && failures.Count < 3; seed++)
            {
                var (failure, on, off) = Run(seed);
                if (failure is not null)
                    failures.Add(failure);
                else if (on > off)
                    failures.Add($"seed {seed}: seeded {on} matches, unseeded {off}");

                seeded += on;
                unseeded += off;
            }

            Assert.True(failures.Count == 0, string.Join("\n\n", failures));
            Assert.True(seeded < unseeded, $"seeded {seeded}, unseeded {unseeded}");
        }

        [Fact]
        public void TheProgramsMakeTermsInChildren()
        {
            var programs = Enumerable.Range(0, DefaultSeeds).Select(s => ScopedProgram.Generate(s, true)).ToList();
            Assert.All(programs, p => Assert.Contains(p.Tables, t => t.Kind == ScopedTableKind.Constructor));
            Assert.Contains(programs, p => p.Commands.Any(c => c.Kind == ScopedCommandKind.Assume && c.Scope > 0
                && c.Facts.Any(f => f.Keys.Any(k => k.Construct >= 0))));
            Assert.Contains(programs, p => p.Commands.Any(c => c.Kind == ScopedCommandKind.Create
                && c.Rules.Any(r => r.Head.Any(a => a.Keys.Any(k => k.Construct >= 0)))));
        }

        /// <summary>
        /// Runs one seed both ways; returns a description of the first disagreement or <c>null</c>, and each way's match
        /// total.
        /// </summary>
        static (string? Failure, long Seeded, long Unseeded) Run(int seed)
        {
            var program = ScopedProgram.Generate(seed, true);
            var on = new ScopedDriver(program, inheritWatermarks: true);
            var off = new ScopedDriver(program, inheritWatermarks: false);
            for (var i = 0; i < program.Commands.Length; i++)
            {
                var command = program.Commands[i];
                Dictionary<int, string>? seeded;
                Dictionary<int, string>? unseeded;
                try
                {
                    seeded = on.Execute(command, named: true);
                    unseeded = off.Execute(command, named: true);
                }
                catch (Exception exception)
                {
                    return ($"seed {seed}, command {i} ({command}) threw {exception}\n{program}", on.Matches, off.Matches);
                }

                if (seeded is null || unseeded is null)
                    continue;

                foreach (var (scope, rendering) in unseeded)
                {
                    if (string.Equals(seeded[scope], rendering, StringComparison.Ordinal) == false)
                    {
                        return ($"seed {seed}, check at command {i}, scope s{scope.ToString(CultureInfo.InvariantCulture)}:\n"
                            + $"seeded:\n{seeded[scope]}unseeded:\n{rendering}\nprogram:\n{program}", on.Matches, off.Matches);
                    }
                }
            }

            return (null, on.Matches, off.Matches);
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
