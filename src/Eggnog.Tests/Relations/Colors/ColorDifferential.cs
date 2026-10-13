using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The seed, soak, replay and shrink conventions the rule differentials share, with the environment variables of
    /// the context differentials (<see cref="ContextDifferentialTests"/>,
    /// <see cref="UnionFindContextDifferentialTests"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EGGNOG_CONTEXT_SEEDS</c> sets the number of seeds (400 by default). <c>EGGNOG_CONTEXT_SOAK</c> runs a soak of
    /// at least that many commands per family from <c>EGGNOG_CONTEXT_SEED_BASE</c> (or a random base, printed).
    /// <c>EGGNOG_CONTEXT_RULE_SEED</c> replays one program, named <c>&lt;A|B&gt;&lt;m|h&gt;:&lt;seed&gt;</c> (family,
    /// then monotone or history), printing it and what it exercised.
    /// </para>
    /// <para>
    /// A failing program is shrunk (<see cref="ColorShrinker"/>) and reported with its seed, the replay setting, the
    /// original failure, and the shrunk program with its failure.
    /// </para>
    /// </remarks>
    static class ColorDifferential
    {

        /// <summary>
        /// The steps of a program.
        /// </summary>
        public const int Steps = 120;

        /// <summary>
        /// The seeds to run: 400 by default, or as many as <c>EGGNOG_CONTEXT_SEEDS</c> says.
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var count = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_CONTEXT_SEEDS"), out var n) && n > 0 ? n : 400;
            var data = new TheoryData<int>();
            for (var seed = 0; seed < count; seed++)
                data.Add(seed);

            return data;
        }

        /// <summary>
        /// Names a program for <c>EGGNOG_CONTEXT_RULE_SEED</c>.
        /// </summary>
        public static string Name(ColorFamily family, int seed) => (family.Minting ? "B" : "A") + (family.History ? "h" : "m") + ":" + seed;

        /// <summary>
        /// Parses a name of <see cref="Name"/>.
        /// </summary>
        public static (bool Minting, bool History, int Seed) Parse(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (name.Length < 4 || name[2] != ':' || int.TryParse(name.AsSpan(3), out var seed) == false || "AB".Contains(name[0]) == false || "mh".Contains(name[1]) == false)
                throw new ArgumentException($"'{name}' is not a program name such as Ah:12.", nameof(name));

            return (name[0] == 'B', name[1] == 'h', seed);
        }

        /// <summary>
        /// Runs <paramref name="program"/>; on a failure, shrinks it and fails with the report.
        /// </summary>
        /// <returns>What the program exercised.</returns>
        public static ColorRunStats RunOrShrink(ColorProgram program, ColorRunOptions options)
        {
            var runner = new ColorRunner(program, options);
            try
            {
                return runner.Run();
            }
            catch (Exception failure)
            {
                var (shrunk, shrunkFailure) = ColorShrinker.Shrink(program, options, failure);
                throw new Xunit.Sdk.XunitException(
                    $"{program.Family}, seed {program.Seed}, command {runner.Current}: the engine disagrees with an oracle; replay with EGGNOG_CONTEXT_RULE_SEED={Name(program.Family, program.Seed)}." + Environment.NewLine +
                    $"Failure: {failure.GetType().Name}: {failure.Message}" + Environment.NewLine +
                    $"Shrunk to {shrunk.Commands.Count} of {program.Commands.Count} commands, failing with {shrunkFailure.GetType().Name}: {shrunkFailure.Message}" + Environment.NewLine +
                    shrunk.Describe() + Environment.NewLine + shrunkFailure,
                    failure);
            }
        }

        /// <summary>
        /// Runs programs from consecutive seeds until <c>EGGNOG_CONTEXT_SOAK</c> commands ran; skipped when it is not
        /// set.
        /// </summary>
        /// <param name="family">The family; its <see cref="ColorFamily.History"/> alternates per seed when
        /// <paramref name="alternateHistory"/>.</param>
        /// <param name="options">What runs with the engine.</param>
        /// <param name="alternateHistory">Whether odd seeds run the history family.</param>
        public static void Soak(ColorFamily family, ColorRunOptions options, bool alternateHistory)
        {
            var target = long.TryParse(Environment.GetEnvironmentVariable("EGGNOG_CONTEXT_SOAK"), out var s) ? s : 0;
            Assert.SkipUnless(target > 0, "Set EGGNOG_CONTEXT_SOAK to a number of commands to soak the rule differential.");
            var first = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_CONTEXT_SEED_BASE"), out var b) ? b : Random.Shared.Next(1 << 30);
            var stats = new ColorRunStats();
            var seed = first;
            while (stats.Commands < target)
            {
                var current = alternateHistory ? family with { History = seed % 2 == 1 } : family;
                stats.Add(RunOrShrink(ColorProgram.Generate(seed, current, Steps * 4), options));
                seed++;
            }

            TestContext.Current.SendDiagnosticMessage($"Soaked {stats.Commands} commands over seeds [{first}, {seed}): {stats}");
        }

        /// <summary>
        /// Replays the program <c>EGGNOG_CONTEXT_RULE_SEED</c> names, printing it; skipped when it is not set.
        /// </summary>
        /// <param name="options">What runs with the engine.</param>
        /// <param name="deletesOnlyUnwritten">Whether the program is generated for the seeded-versus-unseeded
        /// property.</param>
        public static void Replay(ColorRunOptions options, bool deletesOnlyUnwritten = false)
        {
            var name = Environment.GetEnvironmentVariable("EGGNOG_CONTEXT_RULE_SEED");
            Assert.SkipUnless(string.IsNullOrEmpty(name) == false, "Set EGGNOG_CONTEXT_RULE_SEED to a program such as Ah:12 to replay one rule differential program.");
            var (minting, history, seed) = Parse(name!);
            var program = ColorProgram.Generate(seed, new ColorFamily(minting, history, deletesOnlyUnwritten), Steps);
            TestContext.Current.SendDiagnosticMessage(program.Describe());
            var stats = RunOrShrink(program, options);
            TestContext.Current.SendDiagnosticMessage(stats.ToString());
        }

    }

}
