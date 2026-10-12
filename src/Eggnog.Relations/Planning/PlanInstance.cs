using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One run of a <see cref="PlanTemplate"/>: the template, the action id of the run, and the header subsets evaluated
    /// against the current tables. The per-run half of egglog's <c>Plan</c> (its <c>header: Vec&lt;JoinHeader&gt;</c>),
    /// produced by <see cref="PlanTemplate.InstantiateLayered{TEvaluator}"/>.
    /// </summary>
    /// <remarks>
    /// Owns the subsets: <see cref="Dispose"/> returns their pooled buffers. Header <c>i</c> is
    /// <see cref="Headers"/>[i] with rows <see cref="HeaderSubsets"/>[i], one subset per layer of the table's view
    /// (colored-contexts spec §9.4; every root header lies in layer 0); the executor intersects each atom's root row set
    /// with every subset of that atom before the join. Valid only while the tables are not modified.
    /// </remarks>
    internal sealed class PlanInstance : IDisposable
    {

        /// <summary>
        /// Initializes a new instance. The arrays are owned by the new instance.
        /// </summary>
        /// <param name="template">The template.</param>
        /// <param name="action">The action id of this run.</param>
        /// <param name="headers">The header specs, as re-evaluated.</param>
        /// <param name="headerSubsets">The subsets, one per header.</param>
        public PlanInstance(PlanTemplate template, ActionId action, JoinHeaderSpec[] headers, LayeredSubset[] headerSubsets)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(headers);
            ArgumentNullException.ThrowIfNull(headerSubsets);
            if (headers.Length != headerSubsets.Length)
                throw new ArgumentException("Each header needs one subset.", nameof(headerSubsets));

            Template = template;
            Action = action;
            Headers = headers;
            HeaderSubsets = headerSubsets;
        }

        /// <summary>
        /// Gets the template.
        /// </summary>
        public PlanTemplate Template { get; }

        /// <summary>
        /// Gets the action id of this run.
        /// </summary>
        public ActionId Action { get; }

        /// <summary>
        /// Gets the header specs: the extra constraints first, then the template's headers. Callers must not modify
        /// the array.
        /// </summary>
        public JoinHeaderSpec[] Headers { get; }

        /// <summary>
        /// Gets the header subsets, one per header and all non-empty, each per layer of the table's view. Elements may be
        /// read in place but must not be moved out.
        /// </summary>
        public LayeredSubset[] HeaderSubsets { get; }

        /// <summary>
        /// Returns the subsets' pooled buffers. Idempotent.
        /// </summary>
        public void Dispose()
        {
            for (var i = 0; i < HeaderSubsets.Length; i++)
                HeaderSubsets[i].Dispose(null);
        }

    }

}
