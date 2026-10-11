namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Implemented by a benchmark class whose invocations each process a known number of items (rows staged, union-find
    /// operations applied): the counterpart of divan's <c>input_counter(ItemsCount::new(..))</c>.
    /// <see cref="ThroughputColumn"/> reads it to report items per second.
    /// </summary>
    /// <remarks>
    /// The column runs in the host process, which never ran the benchmark: it creates a fresh instance, sets the
    /// benchmark case's parameters on it and calls <see cref="ItemsPerInvocation"/>. Implementations must therefore
    /// derive the count from the parameters alone (regenerating a seeded workload where they must), never from state a
    /// setup method left behind.
    /// </remarks>
    internal interface IItemsPerInvocation
    {

        /// <summary>
        /// Gets the number of items one invocation of <paramref name="benchmarkMethod"/> processes, for the parameter
        /// values currently set on this instance.
        /// </summary>
        /// <param name="benchmarkMethod">The name of the benchmark method.</param>
        /// <returns>The item count; zero if the method counts no items.</returns>
        long ItemsPerInvocation(string benchmarkMethod);

    }

}
