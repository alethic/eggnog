using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A slot of per-context state that the layer above the bridge keeps in every <see cref="EGraphContext"/>: one
    /// value of type <typeparamref name="T"/> per context, created on first use by the slot's factory and read with
    /// <see cref="EGraphContext.GetLocal{T}"/>. The rule layer keeps its scope runtime here, and schedulers keep their
    /// collect-matches buffers here.
    /// </summary>
    /// <remarks>
    /// Made by <see cref="EGraph.NewLocalSlot{T}"/>, which numbers the slots of an e-graph densely. Immutable. The
    /// factory may run on any thread that first asks a context for the slot, a rule task of the context's run included;
    /// when two threads race, both may build a value and one is kept (installed with a compare-and-swap), so the factory
    /// must not have side effects beyond the value it returns.
    /// </remarks>
    /// <typeparam name="T">The type of the per-context value.</typeparam>
    internal sealed class ContextLocalSlot<T>
        where T : class
    {

        /// <summary>
        /// Initializes a new instance. Called by <see cref="EGraph.NewLocalSlot{T}"/>.
        /// </summary>
        /// <param name="graph">The e-graph whose contexts hold the slot.</param>
        /// <param name="index">The slot's index among the e-graph's slots.</param>
        /// <param name="factory">Builds the value of a context on first use.</param>
        internal ContextLocalSlot(EGraph graph, int index, Func<EGraphContext, T> factory)
        {
            ArgumentNullException.ThrowIfNull(graph);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentNullException.ThrowIfNull(factory);
            Graph = graph;
            Index = index;
            Factory = factory;
        }

        /// <summary>
        /// Gets the e-graph whose contexts hold the slot.
        /// </summary>
        public EGraph Graph { get; }

        /// <summary>
        /// Gets the slot's index among the e-graph's slots.
        /// </summary>
        public int Index { get; }

        /// <summary>
        /// Gets the factory that builds a context's value on first use.
        /// </summary>
        public Func<EGraphContext, T> Factory { get; }

    }

}
