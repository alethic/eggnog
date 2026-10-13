using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The limits and settings of a <see cref="SplitDriver"/>. Immutable once built.
    /// </summary>
    internal sealed class SplitOptions
    {

        readonly int depthLimit = 4;
        readonly int caseLimit = 16;
        readonly LiftOptions lift = LiftOptions.Default;
        readonly ContextOptions caseOptions = ContextOptions.Case;

        /// <summary>
        /// Gets the default settings.
        /// </summary>
        public static SplitOptions Default { get; } = new();

        /// <summary>
        /// Gets the number of split levels: a step at depth <c>d</c> opens cases only while <c>d</c> is below it, and
        /// deepens into them only while <c>d + 1</c> is. Default 4; at least 1.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
        public int DepthLimit
        {
            get => depthLimit;
            init
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
                depthLimit = value;
            }
        }

        /// <summary>
        /// Gets the most cases one split may open; a request for more is skipped. Default 16; at least 1.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
        public int CaseLimit
        {
            get => caseLimit;
            init
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
                caseLimit = value;
            }
        }

        /// <summary>
        /// Gets the limits of the lifts the driver runs. Default <see cref="LiftOptions.Default"/>.
        /// </summary>
        public LiftOptions Lift
        {
            get => lift;
            init => lift = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Gets the options cases are created with (through them, <see cref="ContextOptions.InheritWatermarks"/>
        /// reaches the cases). They must make a case (<see cref="ContextOptions.IsCase"/>) that stops on a
        /// contradiction (<see cref="ContextOptions.StopOnContradiction"/>). Default <see cref="ContextOptions.Case"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The options do not make a case that stops on a contradiction.</exception>
        public ContextOptions CaseOptions
        {
            get => caseOptions;
            init
            {
                ArgumentNullException.ThrowIfNull(value);
                CaseSet.CheckCaseOptions(value, nameof(value));
                caseOptions = value;
            }
        }

    }

}
