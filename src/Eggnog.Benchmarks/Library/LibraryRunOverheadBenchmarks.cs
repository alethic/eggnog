using System;
using System.Collections.Generic;
using System.Linq;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// What the library adds to one iteration at the root: <see cref="LibraryRun"/> runs one iteration of
    /// <see cref="Rules"/> global rules through <see cref="Scope.Run"/>, and <see cref="EngineRun"/> (the baseline)
    /// runs the same lowered rules through the engine's <c>RunRules</c>, with nothing of the library around it. The
    /// rules saturated in the setup and nothing changes after, so every rule is idle: the difference is the library's
    /// per-run work (the schedule's checks, choosing and lowering the iteration's rules, the per-rule statistics and
    /// the report), and its allocated bytes against <see cref="Rules"/> give the library's bytes per rule per
    /// iteration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The theory is <see cref="LongTables"/> over 10 relations; its root is writable, defines the rules as global
    /// rules of <see cref="Atoms"/> body atoms, and saturates them in the setup. <see cref="LibraryRun"/> uses the
    /// public API only; the baseline takes the variants the library lowered for the root (its internal
    /// <c>PrepareRules</c>, semi-naive as a run lowers them) and calls the root's engine context directly. Both advance
    /// the clock by one epoch per invocation. The class is not sealed because BenchmarkDotNet generates a class that
    /// derives from it.
    /// </para>
    /// <para>
    /// The setup checks the saturated tables against the model of <see cref="LongTables.Expected"/>, and one run of
    /// each path: the library's reports one iteration, no change, every rule and no match; the engine's reports no
    /// change and no match. The engine path is given the variant lowered for every rule, in order. The setup then calls
    /// each benchmark method once and checks that it ran every one of those variants: each variant's last run moves to
    /// the same new epoch. Every invocation checks that its run changed nothing, and the library's that it reported one
    /// iteration of every rule.
    /// </para>
    /// </remarks>
    public class LibraryRunOverheadBenchmarks : Relations.Benchmarks.IItemsPerInvocation
    {

        /// <summary>
        /// The relations the rules read.
        /// </summary>
        public const int Tables = 10;

        LongTables? theory;
        Schedule? once;
        Relations.EGraphContext? engine;
        Relations.EGraphRuleId[]? ids;

        /// <summary>
        /// Gets or sets the number of global rules.
        /// </summary>
        [Params(10, 100)]
        public int Rules { get; set; }

        /// <summary>
        /// Gets or sets the body atoms of each rule.
        /// </summary>
        [Params(2, 4)]
        public int Atoms { get; set; }

        /// <summary>
        /// Defines the rules, saturates the root, lowers the rules for the engine path, and checks one run of each
        /// path.
        /// </summary>
        /// <exception cref="InvalidOperationException">The saturated tables differ from the model, the engine path
        /// misses a lowered variant, a run changed something, matched, or reported other than one iteration of every
        /// rule, or a benchmark method did not run every variant.</exception>
        [GlobalSetup]
        public void GlobalSetup()
        {
            theory = new LongTables(Tables);
            var t = theory;
            var atoms = Atoms;
            for (var r = 0; r < Rules; r++)
            {
                var rule = r;
                theory.Rules.DefineRule($"rule {r}", b => t.Define(b, rule, atoms));
            }

            var root = theory.Graph.Root;
            if (root.Run(Schedule.Saturate(theory.Rules)).Saturated == false)
                throw new InvalidOperationException("The root did not saturate.");

            theory.CheckSaturated(root, Rules, Atoms);
            once = Schedule.Run(theory.Rules);

            var lowered = root.Engine.PrepareRules(theory.Rules.Compiled, semiNaive: true);
            ids = lowered.Select(variant => variant.Id).ToArray();
            engine = root.Engine.Context;
            var described = new HashSet<string>();
            foreach (var variant in lowered)
                described.Add(variant.Description);

            if (lowered.Length != Rules || described.Count != Rules)
                throw new InvalidOperationException($"The engine path runs {lowered.Length} lowered variants of {described.Count} rules; expected one variant of each of {Rules} rules.");

            var report = root.Run(once);
            long matches = 0;
            foreach (var statistics in report.Rules.Values)
                matches += statistics.Matches;

            if (report.Iterations != 1 || report.Changed || report.Rules.Count != Rules || matches != 0)
                throw new InvalidOperationException($"A library run reported {report.Iterations} iterations, {report.Rules.Count} rules and {matches} matches (changed: {report.Changed}); expected 1, {Rules}, 0, unchanged.");

            var iteration = engine.RunRules(ids);
            var engineMatches = 0L;
            foreach (var variant in lowered)
                engineMatches += iteration.RuleSetReport.NumMatches(variant.Description);

            if (iteration.Changed || iteration.RebuildChanged || engineMatches != 0)
                throw new InvalidOperationException($"An engine run matched {engineMatches} rows (changed: {iteration.Changed}, rebuild changed: {iteration.RebuildChanged}); expected none and no change.");

            CheckEveryVariantRuns(nameof(LibraryRun), () => LibraryRun());
            CheckEveryVariantRuns(nameof(EngineRun), () => EngineRun());
        }

        /// <summary>
        /// Calls a benchmark method once and checks that it ran every variant of <c>ids</c>: each one's last run at the
        /// root moves to the same epoch, past every earlier one.
        /// </summary>
        /// <param name="name">The method's name, for the message.</param>
        /// <param name="method">The method.</param>
        /// <exception cref="InvalidOperationException">A variant's last run did not move to the method's epoch.</exception>
        void CheckEveryVariantRuns(string name, Action method)
        {
            var before = LastRuns().Max();
            method();
            var after = LastRuns();
            var epoch = after.Max();
            var missed = after.Count(lastRun => lastRun != epoch);
            if (epoch <= before || missed != 0)
                throw new InvalidOperationException($"{name} left {missed} of {after.Length} lowered variants unrun (last runs from {after.Min()} to {epoch}, before {before}).");
        }

        /// <summary>
        /// Gets the epoch of each variant of <c>ids</c>' last run at the root.
        /// </summary>
        /// <returns>The epochs, in the order of <c>ids</c>.</returns>
        uint[] LastRuns() => ids!.Select(id => engine!.Owner.GetRuleInfo(id).LastRunAt).ToArray();

        /// <summary>
        /// One iteration of every rule through the library.
        /// </summary>
        /// <returns>The iterations run: 1.</returns>
        /// <exception cref="InvalidOperationException">The run changed something, or reported other than one
        /// iteration of every rule.</exception>
        [Benchmark]
        public int LibraryRun()
        {
            var report = theory!.Graph.Root.Run(once!);
            if (report.Changed || report.Iterations != 1 || report.Rules.Count != Rules)
                throw new InvalidOperationException($"An idle library run reported {report.Iterations} iterations of {report.Rules.Count} rules (changed: {report.Changed}); expected 1 of {Rules}, unchanged.");

            return report.Iterations;
        }

        /// <summary>
        /// One iteration of the same lowered rules through the engine alone.
        /// </summary>
        /// <returns>The rules run.</returns>
        /// <exception cref="InvalidOperationException">The run changed something.</exception>
        [Benchmark(Baseline = true)]
        public int EngineRun()
        {
            var iteration = engine!.RunRules(ids!);
            if (iteration.Changed || iteration.RebuildChanged)
                throw new InvalidOperationException("An idle engine run changed the root.");

            return ids!.Length;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The rules one iteration runs: the mean time divided by it is the time per rule per iteration.
        /// </remarks>
        long Relations.Benchmarks.IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => Rules;

    }

}
