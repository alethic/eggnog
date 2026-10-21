using System;

namespace Eggnog
{

    /// <summary>
    /// The interning of containers that hold classes, egglog's eq-sort containers (<see cref="ColumnType.HoldsClasses"/>).
    /// </summary>
    internal static class ContainerClasses
    {

        /// <summary>
        /// Interns a container of classes for <paramref name="scope"/>: at the root, as egglog's primitive registers its
        /// container through the execution state; in a child, the container the root holds, since a child context may
        /// look such a container up but not intern it, children never rebuilding containers (decisions row 9).
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="env">The container type's environment, which holds ids.</param>
        /// <param name="container">The container.</param>
        /// <param name="counters">The graph's counters, from which the root mints ids.</param>
        /// <param name="name">The container type's name.</param>
        /// <param name="scope">The scope interning it.</param>
        /// <returns>The container's id.</returns>
        /// <exception cref="InvalidOperationException">There is no scope, or the scope is not the root and the root holds
        /// no such container.</exception>
        /// <exception cref="OverflowException">The graph has no ids left.</exception>
        public static uint Intern<TContainer>(
            Relations.ContainerEnv<TContainer> env,
            in TContainer container,
            Relations.CounterSet counters,
            string name,
            Scope? scope)
            where TContainer : struct, Relations.IContainerValue<TContainer>
        {
            if (scope is null)
                throw NeedScope(name);

            if (scope.IsRoot)
                return env.GetOrInsertAtRoot(container, counters);

            if (env.TryGetId(container, out var id))
                return id;

            throw new InvalidOperationException(
                $"A new {name} holds classes, egglog's eq-sort container, which only the root interns; scope '{scope.Path}' can use one the root holds but not make one.");
        }

        /// <summary>
        /// Makes the error for a container of classes encoded outside a scope.
        /// </summary>
        /// <param name="name">The container type's name.</param>
        /// <returns>The error.</returns>
        public static InvalidOperationException NeedScope(string name) =>
            new($"A {name} holds classes, egglog's eq-sort container, which encodes in a scope.");

    }

}
