namespace Eggnog.Relations
{

    /// <summary>
    /// What one context remembers of one rule's runs: the timestamp of its last successful run there, the executed Id
    /// constants <b>K</b> of that run (the canonical form, in that context, of every Id constant of the rule's query
    /// and actions), and the generation of the rule it belongs to, so progress of a freed rule never applies to a rule
    /// that reuses its id.
    /// </summary>
    /// <remarks>
    /// A semi-naive watermark: rows stamped at or after <see cref="LastRunAt"/> are new to the rule in that context,
    /// and the watermark applies only while the rule's current constants equal <see cref="Constants"/> (a constant that
    /// lost a union forces a full run). <c>default</c> holds generation 0, which no rule has (generations start at 1,
    /// <see cref="RuleTable"/>), so an unused slot never matches. Immutable.
    /// </remarks>
    internal readonly struct RuleProgress
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="lastRunAt">The epoch of the run.</param>
        /// <param name="constants">The run's executed Id constants, or <c>null</c> when the rule has none; shared, never
        /// written.</param>
        /// <param name="generation">The rule's generation.</param>
        public RuleProgress(uint lastRunAt, uint[]? constants, int generation)
            : this(lastRunAt, constants, generation, false)
        {

        }

        /// <summary>
        /// Initializes a new instance, saying whether it is an ancestor's.
        /// </summary>
        RuleProgress(uint lastRunAt, uint[]? constants, int generation, bool inherited)
        {
            LastRunAt = lastRunAt;
            Constants = constants;
            Generation = generation;
            Inherited = inherited;
        }

        /// <summary>
        /// Gets the epoch of the rule's last successful run in the context: its semi-naive watermark there.
        /// </summary>
        public uint LastRunAt { get; }

        /// <summary>
        /// Gets the executed Id constants of that run (K), or <c>null</c> when the rule has none.
        /// </summary>
        public uint[]? Constants { get; }

        /// <summary>
        /// Gets the generation of the rule the progress belongs to; 0 for an unused slot.
        /// </summary>
        public int Generation { get; }

        /// <summary>
        /// Gets whether the progress is an ancestor's, set as a context's own (<see cref="AsInherited"/>): a scheduler's
        /// query rule in a child continues from the run its parent's scheduler state recorded, which may predate the
        /// child. Every other own progress is a run in the context, at or after its creation.
        /// </summary>
        public bool Inherited { get; }

        /// <summary>
        /// Gets this progress as an ancestor's, for a context to take as its own.
        /// </summary>
        /// <returns>The progress, marked <see cref="Inherited"/>.</returns>
        public RuleProgress AsInherited() => new(LastRunAt, Constants, Generation, true);

        /// <summary>
        /// Gets whether two executed-constant vectors are equal: both absent, the same array, or equal element by element.
        /// O(constants).
        /// </summary>
        /// <param name="a">One vector.</param>
        /// <param name="b">The other.</param>
        /// <returns><c>true</c> if equal.</returns>
        public static bool SameConstants(uint[]? a, uint[]? b)
        {
            if (ReferenceEquals(a, b))
                return true;

            if (a is null || b is null || a.Length != b.Length)
                return false;

            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override string ToString() => Generation == 0 ? "(none)" : $"last run {LastRunAt}, generation {Generation}, {Constants?.Length ?? 0} constant(s)";

    }

}
