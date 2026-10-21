namespace Eggnog
{

    /// <summary>
    /// What a scheduler holds for one rule in one scope between steps: the matches it left (egglog's residual
    /// <c>matches</c>), whether the rule's query runs at the next step (<c>should_seek</c>), and the query's progress in
    /// the scope's context, which a child that starts from this state takes as its own. Immutable.
    /// </summary>
    /// <param name="Pending">The matches left, one tuple after another, as found: their classes are canonicalized when
    /// offered.</param>
    /// <param name="ShouldSeek">Whether the query runs at the next step.</param>
    /// <param name="Progress">The query rule's progress in the context of the scope that last ran it; <c>null</c> before
    /// it has run.</param>
    internal sealed record ScheduledRuleState(uint[] Pending, bool ShouldSeek, Relations.RuleProgress? Progress)
    {

        /// <summary>
        /// Gets the state of a rule never stepped: nothing left, and the query runs.
        /// </summary>
        public static ScheduledRuleState Initial { get; } = new(System.Array.Empty<uint>(), true, null);

    }

}
