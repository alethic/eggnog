using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Subsumption
{

    /// <summary>
    /// Random programs that subsume in scope chains. Watermark inheritance changes nothing but the work: a program run
    /// with the graph's <c>InheritWatermarks</c> on and off ends with the same view, with and without freezes in the
    /// chain. 100 seeds by default; <c>EGGNOG_SEEDS</c> raises it.
    /// </summary>
    public sealed class SubsumeDifferentialTests
    {

        /// <summary>
        /// The number of seeds without <c>EGGNOG_SEEDS</c>.
        /// </summary>
        const int DefaultSeeds = 100;

        [Fact]
        public void InheritanceDoesNotChangeTheResult()
        {
            var failures = new List<string>();
            for (var seed = 0; seed < Seeds() && failures.Count < 3; seed++)
            {
                var on = Play(seed, true, false);
                var off = Play(seed, false, false);
                if (on != off)
                    failures.Add($"seed {seed}\n  inherited {on}\n  fresh     {off}");
            }

            Assert.Empty(failures);
        }

        [Fact]
        public void InheritanceDoesNotChangeTheResultUnderFreezes()
        {
            var failures = new List<string>();
            for (var seed = 0; seed < Seeds() && failures.Count < 3; seed++)
            {
                var on = Play(seed, true, true);
                var off = Play(seed, false, true);
                if (on != off)
                    failures.Add($"seed {seed}\n  inherited {on}\n  fresh     {off}");
            }

            Assert.Empty(failures);
        }

        /// <summary>
        /// Plays one random program over a chain of up to five scopes, with makes, unions, host and rule subsumptions,
        /// and runs; with <paramref name="freezes"/>, the first writable scope of the chain is sometimes frozen. Renders the
        /// last scope after a final run.
        /// </summary>
        static string Play(int seed, bool inherit, bool freezes)
        {
            var world = new SubsumeWorld(inherit);
            var t = world.Theory;
            var random = new Random(seed);
            var scopes = new List<Scope> { t.Root };
            for (var step = 0; step < 40; step++)
            {
                var choice = random.Next(freezes ? 13 : 12);
                var writable = scopes.Where(x => x.IsFrozen == false).ToList();
                var scope = writable[random.Next(writable.Count)];
                long k = random.Next(6);
                long k2 = random.Next(6);
                switch (choice)
                {
                    case 0:
                        if (scopes.Count < 5)
                            scopes.Add(scopes[^1].CreateChild());

                        break;
                    case 1:
                        scope.Make(t.Num.Of(k));
                        break;
                    case 2:
                        scope.Make(t.Add.Of(t.Num.Of(k), t.Num.Of(k2)));
                        break;
                    case 3:
                        scope.Make(t.Mul.Of(t.Num.Of(k), t.Num.Of(k2)));
                        break;
                    case 4:
                        scope.Subsume(t.Num.Of(k));
                        break;
                    case 5:
                        scope.Assume(world.Fact.Of(k));
                        break;
                    case 6:
                        scope.Subsume(world.Fact.Of(k));
                        break;
                    case 7:
                        scope.Union(scope.Make(t.Num.Of(k)), scope.Make(t.Num.Of(k2)));
                        break;
                    case 8:
                    case 9:
                        scope.Run(t.Rules);
                        break;
                    case 10:
                        scope.Run(Schedule.Repeat(3, t.Rules));
                        break;
                    case 12:
                    {
                        var first = scopes.FirstOrDefault(x => x.IsFrozen == false);
                        if (first is not null && (first.Parent is null || first.Parent.IsFrozen))
                        {
                            first.Freeze();
                            if (ReferenceEquals(first, scopes[^1]))
                                scopes.Add(first.CreateChild());
                        }

                        break;
                    }
                    default:
                        scope.Subsume(t.Add.Of(t.Num.Of(k), t.Num.Of(k2)));
                        break;
                }
            }

            var last = scopes[^1];
            last.Run(Schedule.Repeat(2, t.Rules));
            return world.Snapshot(last);
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
