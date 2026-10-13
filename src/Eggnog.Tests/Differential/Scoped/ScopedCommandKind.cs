namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// What a <see cref="ScopedCommand"/> does.
    /// </summary>
    internal enum ScopedCommandKind
    {

        /// <summary>
        /// Creates a child of a scope, with a tag and local rules.
        /// </summary>
        Create,

        /// <summary>
        /// Assumes ground facts in a writable scope.
        /// </summary>
        Assume,

        /// <summary>
        /// Saturates a writable scope's rules.
        /// </summary>
        Run,

        /// <summary>
        /// Saturates a writable scope whose parent is frozen, then freezes it.
        /// </summary>
        Freeze,

        /// <summary>
        /// Saturates every writable scope, parents first, then compares every scope's view and every query's answer.
        /// </summary>
        Check,

    }

}
