using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The working storage of one child context's container passes (<see cref="ContainerLayers.RebuildPass"/>): made by
    /// the first pass that may have work while a registered type holds ids, one over inherited dirty ids, or over a
    /// delta while the context has a layer or one of the delta's own losers is held by, or is, a container below; and
    /// reused by every pass after it until the context freezes or is disposed, so a child whose own losers no container
    /// concerns allocates none of it. Domain thread only.
    /// </summary>
    /// <remarks>
    /// A pass makes its execution state and its union-find canonicalizer only when it first needs them, to claim an id
    /// or to rebuild a layer (<see cref="State"/>, <see cref="Rebuilder"/>), and drops them at its end: a pass whose own
    /// losers no container holds stages nothing.
    /// </remarks>
    internal sealed class ContainerPassScratch
    {

        /// <summary>
        /// Gets the summary the passes fill.
        /// </summary>
        public ContainerPassSummary Summary { get; } = new();

        /// <summary>
        /// Gets the summary the container types' rebuilds of the current pass fill.
        /// </summary>
        public ContainerRebuildSummary Rebuild { get; } = new();

        /// <summary>
        /// Gets the own losers of the current pass's delta that are not local to the context.
        /// </summary>
        public List<uint> OwnLosers { get; } = new();

        /// <summary>
        /// Gets the ancestor ids phase A claims.
        /// </summary>
        public OrderedValueSet Candidates { get; } = new();

        /// <summary>
        /// Gets the ids phase B revisits.
        /// </summary>
        public OrderedValueSet Revisit { get; } = new();

        /// <summary>
        /// Gets the holders one level's element index lists.
        /// </summary>
        public OrderedValueSet Holders { get; } = new();

        /// <summary>
        /// Gets the ids the dirty closure reached in its current round.
        /// </summary>
        public OrderedValueSet Reached { get; } = new();

        /// <summary>
        /// Gets the dirty closure's frontier: the ids found in its last round.
        /// </summary>
        public List<uint> Frontier { get; } = new();

        /// <summary>
        /// Gets the ids the pass claimed since the list was last cleared: the pass rebuilds each one it claimed, so a
        /// copy that holds an id not canonical in the context is fixed in the same pass.
        /// </summary>
        public List<uint> Claimed { get; } = new();

        /// <summary>
        /// The number of ids the current pass claimed.
        /// </summary>
        public int Claims;

        /// <summary>
        /// The current pass's epoch.
        /// </summary>
        public uint Epoch;

        /// <summary>
        /// The current pass's execution state, made at <see cref="Epoch"/> on first use, or <c>null</c>.
        /// </summary>
        public ExecutionState? State;

        /// <summary>
        /// The context's union-find canonicalizer for the current pass, made on first use, or <c>null</c>.
        /// </summary>
        public Rebuilder? Rebuilder;

    }

}
