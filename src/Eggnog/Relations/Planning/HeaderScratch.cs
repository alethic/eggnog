using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Eggnog.Relations
{

    /// <summary>
    /// Reusable buffers for evaluating the headers of plan instantiations
    /// (<see cref="PlanTemplate.InstantiateLayered{TEvaluator}"/>), so an instantiation that a header proves empty
    /// allocates nothing: the per-header lists, the headers evaluated so far, and the per-layer working buffers of
    /// <see cref="ContextView.Evaluate(TableId, ReadOnlySpan{Constraint}, List{Constraint}, List{Constraint}, HeaderScratch?)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exact-size arrays a <see cref="PlanInstance"/> keeps are allocated only once every header is nonempty
    /// (<see cref="TakeHeaders"/>). egglog's instantiation (core-relations/src/query.rs:191-243) pushes each header
    /// into a <c>Vec</c> that allocates on its first push, and takes its constraint lists from pools, so for a single
    /// plan whose first header is empty it allocates nothing either. This departs from egglog only where egglog
    /// allocates before a header proves empty: for a decomposed plan, whose block vector it allocates first
    /// (query.rs:216-217), and when a header after the first is empty, once the first push has allocated the vector
    /// (decisions, row 19).
    /// </para>
    /// <para>
    /// Owned by one <see cref="RuleSetBuilder"/>, never by a <see cref="PlanTemplate"/>, which concurrent contexts
    /// share. Between instantiations it holds no subset and no constraint (<see cref="ReleaseHeaders"/>). Not
    /// thread-safe.
    /// </para>
    /// </remarks>
    internal sealed class HeaderScratch
    {

        LayeredSubset[] subsets = Array.Empty<LayeredSubset>();
        AtomId[] atoms = Array.Empty<AtomId>();
        int[] ends = Array.Empty<int>();
        readonly List<Constraint> specConstraints = new();
        int count;
        Subset[]? layerSubsets;

        /// <summary>
        /// Gets the list a header's fast constraints are evaluated into.
        /// </summary>
        public List<Constraint> Fast { get; } = new();

        /// <summary>
        /// Gets the list a header's slow constraints are evaluated into.
        /// </summary>
        public List<Constraint> Slow { get; } = new();

        /// <summary>
        /// Gets the list one layer's fast constraints are evaluated into, when a header spans several layers.
        /// </summary>
        public List<Constraint> LayerFast { get; } = new();

        /// <summary>
        /// Gets the list one layer's slow constraints are evaluated into, when a header spans several layers.
        /// </summary>
        public List<Constraint> LayerSlow { get; } = new();

        /// <summary>
        /// Gets the number of headers held.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets a buffer of one <see cref="Subset"/> per possible layer (<see cref="LayeredSubset.MaxLayers"/>), every
        /// entry empty; whoever fills entries empties them again before the next use. Allocated on first use.
        /// </summary>
        /// <returns>The buffer.</returns>
        public Subset[] LayerSubsets() => layerSubsets ??= new Subset[LayeredSubset.MaxLayers];

        /// <summary>
        /// Starts an instantiation of <paramref name="headers"/> headers: grows the buffers to hold them. Allocates only
        /// when an instantiation has more headers than any before it.
        /// </summary>
        /// <param name="headers">The number of headers.</param>
        public void BeginHeaders(int headers)
        {
            Debug.Assert(count == 0, "the previous instantiation must have been taken or released");
            if (subsets.Length < headers)
            {
                var capacity = Math.Max(headers, 2 * subsets.Length);
                subsets = new LayeredSubset[capacity];
                atoms = new AtomId[capacity];
                ends = new int[capacity];
            }
        }

        /// <summary>
        /// Adds a nonempty header: its atom, its fast constraints (copied from <paramref name="fast"/>) and its rows,
        /// which move to the scratch. O(fast constraints).
        /// </summary>
        /// <param name="atom">The header's atom.</param>
        /// <param name="fast">The header's fast constraints.</param>
        /// <param name="subset">The header's rows; the caller must not use its copy again.</param>
        public void AddHeader(AtomId atom, List<Constraint> fast, LayeredSubset subset)
        {
            Debug.Assert(count < subsets.Length, "BeginHeaders reserves room for every header");
            specConstraints.AddRange(fast);
            atoms[count] = atom;
            ends[count] = specConstraints.Count;
            subsets[count] = subset;
            count++;
        }

        /// <summary>
        /// Moves the headers held into exact-size arrays for a <see cref="PlanInstance"/>, leaving the scratch empty: the
        /// only allocation of an instantiation, made once every header is known to be nonempty. O(headers + constraints).
        /// </summary>
        /// <returns>The header specs and their subsets, which the caller owns.</returns>
        public (JoinHeaderSpec[] Specs, LayeredSubset[] Subsets) TakeHeaders()
        {
            var specs = new JoinHeaderSpec[count];
            var taken = new LayeredSubset[count];
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                var end = ends[i];
                var constraints = end == start ? Array.Empty<Constraint>() : new Constraint[end - start];
                specConstraints.CopyTo(start, constraints, 0, end - start);
                specs[i] = new JoinHeaderSpec(atoms[i], constraints);
                taken[i] = subsets[i];
                subsets[i] = default;
                start = end;
            }

            count = 0;
            specConstraints.Clear();
            return (specs, taken);
        }

        /// <summary>
        /// Ends an instantiation: disposes the subsets of the headers still held (none after <see cref="TakeHeaders"/>)
        /// and clears every list and the per-layer buffer (which an evaluation that threw may have left filled), so the
        /// scratch keeps no rows and no constraints alive. O(headers held + <see cref="LayeredSubset.MaxLayers"/>).
        /// </summary>
        public void ReleaseHeaders()
        {
            for (var i = 0; i < count; i++)
            {
                subsets[i].Dispose(null);
                subsets[i] = default;
            }

            count = 0;
            specConstraints.Clear();
            Fast.Clear();
            Slow.Clear();
            LayerFast.Clear();
            LayerSlow.Clear();
            if (layerSubsets is not null)
                Array.Clear(layerSubsets);
        }

    }

}
