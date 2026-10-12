using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// egglog's schedules: what <see cref="Scope.Run(Schedule, RunOptions, System.Threading.CancellationToken)"/> runs. A
    /// ruleset converts to <see cref="Run(Ruleset[])"/> of itself. Immutable; renders as an egglog schedule.
    /// </summary>
    public abstract class Schedule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        private protected Schedule()
        {

        }

        /// <summary>
        /// One iteration over the union of the rulesets' rules active in the scope.
        /// </summary>
        /// <param name="rulesets">The rulesets.</param>
        /// <returns>The schedule.</returns>
        public static Schedule Run(params Ruleset[] rulesets) => new RunSchedule(Copy(rulesets, nameof(rulesets)));

        /// <summary>
        /// Up to <paramref name="times"/> passes of <see cref="Sequence"/> of the steps; stops after a pass with no change.
        /// </summary>
        /// <param name="times">The most passes; zero runs nothing.</param>
        /// <param name="steps">The steps of a pass.</param>
        /// <returns>The schedule.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="times"/> is negative.</exception>
        public static Schedule Repeat(int times, params Schedule[] steps)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(times);
            return new RepeatSchedule(times, Copy(steps, nameof(steps)));
        }

        /// <summary>
        /// Passes of <see cref="Sequence"/> of the steps until one changes nothing.
        /// </summary>
        /// <param name="steps">The steps of a pass.</param>
        /// <returns>The schedule.</returns>
        public static Schedule Saturate(params Schedule[] steps) => new SaturateSchedule(Copy(steps, nameof(steps)));

        /// <summary>
        /// The steps in order, once each.
        /// </summary>
        /// <param name="steps">The steps.</param>
        /// <returns>The schedule.</returns>
        public static Schedule Sequence(params Schedule[] steps) => new SequenceSchedule(Copy(steps, nameof(steps)));

        /// <summary>
        /// Renders the schedule as an egglog schedule: <c>(saturate (run horn) (run rewrite))</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => SExpression.Render(this);

        /// <summary>
        /// Executes the schedule in <paramref name="scope"/> (§6.5.2).
        /// </summary>
        /// <param name="executor">The run's executor.</param>
        /// <param name="scope">The scope to run in.</param>
        /// <param name="depth">The split depth (R3); 0 at the top level.</param>
        /// <param name="inCase">Whether the scope is a case of a split (R3).</param>
        /// <param name="splitsEnabled">Whether split steps run (R3).</param>
        /// <returns>Whether the schedule changed the scope.</returns>
        internal abstract bool Execute(ScheduleExecutor executor, Scope scope, int depth, bool inCase, bool splitsEnabled);

        /// <summary>
        /// Collects the rulesets the schedule reaches.
        /// </summary>
        /// <param name="rulesets">The set to add to.</param>
        internal abstract void Collect(ISet<Ruleset> rulesets);

        /// <summary>
        /// Copies an array of non-null items.
        /// </summary>
        static T[] Copy<T>(T[] items, string parameter) where T : class
        {
            ArgumentNullException.ThrowIfNull(items, parameter);
            var copy = new T[items.Length];
            for (var i = 0; i < items.Length; i++)
                copy[i] = items[i] ?? throw new ArgumentNullException(parameter, $"Item {i} is null.");

            return copy;
        }

    }

}
