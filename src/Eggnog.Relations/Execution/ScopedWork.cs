namespace Eggnog.Relations
{

    /// <summary>
    /// A unit of work spawned into a <see cref="WorkScope"/>: the closure egglog passes to rayon's
    /// <c>Scope::spawn</c>. The executor's work items (a rule, a morsel of frames, a full action batch) derive from it.
    /// </summary>
    /// <remarks>
    /// One virtual call per item, never per row. An item runs once, on whichever thread takes it from the scope.
    /// </remarks>
    internal abstract class ScopedWork
    {

        /// <summary>
        /// Runs the work. Exceptions propagate to the scope, which rethrows the first one from
        /// <see cref="WorkScope.Wait"/>.
        /// </summary>
        public abstract void Run();

    }

}
