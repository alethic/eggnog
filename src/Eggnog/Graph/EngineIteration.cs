using System;

namespace Eggnog
{

    /// <summary>
    /// What one engine iteration of a scope did: the engine's change flags (the rules' writes and the rebuild reported
    /// separately), whether a case's stop flag is set (always <c>false</c> outside a case), the rule set's report, and
    /// the rebuild time.
    /// </summary>
    /// <param name="Changed">Whether the rules' writes changed the scope.</param>
    /// <param name="RebuildChanged">Whether the rebuild that followed changed it.</param>
    /// <param name="Stopped">Whether a case's stop flag is set.</param>
    /// <param name="Report">The engine's rule set report: matches and search times by rule description.</param>
    /// <param name="RebuildTime">The time the rebuild took.</param>
    internal readonly record struct EngineIteration(bool Changed, bool RebuildChanged, bool Stopped,
        Relations.RuleSetReport Report, TimeSpan RebuildTime);

}
