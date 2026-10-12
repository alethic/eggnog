using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Eggnog
{

    /// <summary>
    /// One step of a run (§6.11, D41), delivered to <see cref="RunOptions.Observer"/> as it happens and kept in
    /// <see cref="RunReport.Steps"/> with <see cref="RunOptions.RecordSteps"/>. Immutable.
    /// </summary>
    /// <remarks>
    /// To find why a fact appeared: run with <see cref="RunOptions.RecordSteps"/> and
    /// <see cref="EGraphOptions.RecordPlans"/>, find the first step whose <see cref="Rules"/> lists the rule that derives it,
    /// read the rule with <see cref="Rule.ToString"/>, the facts it reads with <see cref="Scope.Rows(TableHandle)"/>, and
    /// how the engine joined them with <see cref="RuleStatistics.Plan"/>.
    /// </remarks>
    public sealed class RunStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kind">What the step was.</param>
        /// <param name="scope">The scope it ran in.</param>
        /// <param name="index">Its position among the run's steps.</param>
        /// <param name="changed">Whether it changed the scope.</param>
        /// <param name="elapsed">How long it took.</param>
        /// <param name="rules">For an iteration, the rules that matched in it, in the order they ran; copied.</param>
        internal RunStep(RunStepKind kind, Scope scope, int index, bool changed, TimeSpan elapsed, IReadOnlyList<RuleStatistics> rules)
        {
            Kind = kind;
            Scope = scope;
            ScopePath = scope.Path;
            Index = index;
            Changed = changed;
            Elapsed = elapsed;

            // A copy behind a read-only wrapper: neither the executor's list nor a cast can change a step once made.
            var copy = new RuleStatistics[rules.Count];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = rules[i];

            Rules = new ReadOnlyCollection<RuleStatistics>(copy);
        }

        /// <summary>
        /// Gets what the step was.
        /// </summary>
        public RunStepKind Kind { get; }

        /// <summary>
        /// Gets the scope the step ran in.
        /// </summary>
        public Scope Scope { get; }

        /// <summary>
        /// Gets the path of the scope the step ran in, kept for messages once the scope is gone.
        /// </summary>
        public string ScopePath { get; }

        /// <summary>
        /// Gets the step's position among the run's steps, from 0.
        /// </summary>
        public int Index { get; }

        /// <summary>
        /// Gets whether the step changed its scope (the engine's change flags, §6.5.1).
        /// </summary>
        public bool Changed { get; }

        /// <summary>
        /// Gets how long the step took.
        /// </summary>
        public TimeSpan Elapsed { get; }

        /// <summary>
        /// Gets, for an iteration, the rules that matched in it, in the order they ran, with that iteration's figures.
        /// </summary>
        public IReadOnlyList<RuleStatistics> Rules { get; }

        /// <summary>
        /// Renders the step: <c>#3 iteration root changed: lower uuid 1, and commutes 4</c>, the rules with their match
        /// counts.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append('#').Append(Index.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(KindName(Kind)).Append(' ')
                .Append(ScopePath).Append(Changed ? " changed" : " unchanged");
            for (var i = 0; i < Rules.Count; i++)
            {
                builder.Append(i == 0 ? ": " : ", ").Append(Rules[i].Rule.Name).Append(' ')
                    .Append(Rules[i].Matches.ToString(CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Gets a step kind's name in a rendering.
        /// </summary>
        static string KindName(RunStepKind kind) => kind switch
        {
            RunStepKind.Iteration => "iteration",
            _ => kind.ToString().ToLowerInvariant(),
        };

    }

}
