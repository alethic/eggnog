namespace Eggnog
{

    /// <summary>
    /// What a scheduler holds for one rule in one scope between steps: the matches it left (egglog's residual
    /// <c>matches</c>), whether the rule's query runs at the next step (<c>should_seek</c>), the query's progress in
    /// the scope's context, which a child that starts from this state takes as its own, and the matches a failed step
    /// chose but did not apply, which the scope's worklist holds and a child stages into its own. Immutable.
    /// </summary>
    /// <param name="Pending">The matches left, one tuple after another, as found: their classes are canonicalized when
    /// offered.</param>
    /// <param name="ShouldSeek">Whether the query runs at the next step.</param>
    /// <param name="Progress">The query rule's progress in the context of the scope that last ran it; <c>null</c> before
    /// it has run.</param>
    /// <param name="Decided">The matches a step chose that its worklist still held when the step failed, one tuple
    /// after another (egglog's <c>decided</c> rows, which its <c>push</c> clones); <c>null</c> for none.</param>
    internal sealed record ScheduledRuleState(uint[] Pending, bool ShouldSeek, Relations.RuleProgress? Progress, uint[]? Decided = null)
    {

        /// <summary>
        /// Gets the state of a rule never stepped: nothing left, and the query runs.
        /// </summary>
        public static ScheduledRuleState Initial { get; } = new(System.Array.Empty<uint>(), true, null);

    }

}
