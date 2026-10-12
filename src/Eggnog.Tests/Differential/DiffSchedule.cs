using System;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A schedule of a random program. Immutable.
    /// </summary>
    internal sealed class DiffSchedule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kind">What the node is.</param>
        /// <param name="rulesets">A run's rulesets, by index.</param>
        /// <param name="times">A repeat's most passes.</param>
        /// <param name="steps">The steps of a composite node, or an until's body.</param>
        /// <param name="condition">An until's facts.</param>
        public DiffSchedule(DiffScheduleKind kind, int[] rulesets, int times, DiffSchedule[] steps, DiffFact[] condition)
        {
            Kind = kind;
            Rulesets = rulesets;
            Times = times;
            Steps = steps;
            Condition = condition;
        }

        /// <summary>Gets what the node is.</summary>
        public DiffScheduleKind Kind { get; }

        /// <summary>Gets a run's rulesets, by index.</summary>
        public int[] Rulesets { get; }

        /// <summary>Gets a repeat's most passes.</summary>
        public int Times { get; }

        /// <summary>Gets the steps of a composite node, or an until's body (its one step).</summary>
        public DiffSchedule[] Steps { get; }

        /// <summary>Gets an until's facts.</summary>
        public DiffFact[] Condition { get; }

        /// <summary>
        /// One iteration of the rulesets.
        /// </summary>
        /// <param name="rulesets">The rulesets.</param>
        /// <returns>The node.</returns>
        public static DiffSchedule Run(params int[] rulesets) => new(DiffScheduleKind.Run, rulesets, 0, Array.Empty<DiffSchedule>(), Array.Empty<DiffFact>());

        /// <summary>
        /// A composite node: saturate, sequence or repeat.
        /// </summary>
        /// <param name="kind">The kind.</param>
        /// <param name="times">A repeat's most passes.</param>
        /// <param name="steps">The steps.</param>
        /// <returns>The node.</returns>
        public static DiffSchedule Of(DiffScheduleKind kind, int times, params DiffSchedule[] steps) => new(kind, Array.Empty<int>(), times, steps, Array.Empty<DiffFact>());

        /// <summary>
        /// The body unless every fact holds.
        /// </summary>
        /// <param name="body">The body.</param>
        /// <param name="condition">The facts.</param>
        /// <returns>The node.</returns>
        public static DiffSchedule Until(DiffSchedule body, params DiffFact[] condition) => new(DiffScheduleKind.Until, Array.Empty<int>(), 0, new[] { body }, condition);

    }

}
