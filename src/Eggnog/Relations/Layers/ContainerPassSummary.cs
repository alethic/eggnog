using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// What one child context's container pass did (<see cref="ContainerLayers.RebuildPass"/>): whether a container's
    /// contents or id changed, and the ids whose meaning changed in the context's view, closed upward over the view
    /// (egglog's <c>ContainerRebuildSummary</c>, core-relations/src/containers/mod.rs:76-122, for a child).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dirty ids come in two kinds. Inherited ones are the ancestors' (their container logs' Dirty streams), which
    /// the ancestors closed over their own views and re-stamped their own rows for: the context re-stamps only its own
    /// rows naming them. Found ones are the context's own: the containers its pass rebuilt in place, and every
    /// container of its view that holds a dirty id, transitively; the context re-stamps its own rows and the visible
    /// ancestor rows naming them (<see cref="Restamp"/>, the non-local ones), and logs them for its descendants
    /// (<see cref="Found"/>).
    /// </para>
    /// <para>
    /// Owned and reused by one context's layers (<see cref="ContainerPassScratch"/>), except <see cref="Empty"/>; valid
    /// until their next pass. Not thread-safe.
    /// </para>
    /// </remarks>
    internal sealed class ContainerPassSummary
    {

        /// <summary>
        /// Gets the summary of a pass that did nothing, shared by every context: what a pass returns when no registered
        /// type holds ids, or nothing is new to it, so that such a pass allocates nothing. Never changed, so safe to
        /// read from any thread.
        /// </summary>
        internal static ContainerPassSummary Empty { get; } = new();

        readonly OrderedValueSet dirty = new();
        readonly List<uint> found = new();
        readonly List<uint> restamp = new();
        bool changed;

        /// <summary>
        /// Gets whether the pass claimed an id or changed a container entry of the context's layers, in contents or id:
        /// the context's rebuild then merges the unions the pass staged and runs another pass.
        /// </summary>
        public bool Changed => changed;

        /// <summary>
        /// Gets the number of dirty ids, inherited and found.
        /// </summary>
        public int DirtyCount => dirty.Count;

        /// <summary>
        /// Gets every dirty id, inherited and found, in the order noted: the ids whose rows in the context's own layers
        /// are re-stamped.
        /// </summary>
        public ReadOnlySpan<uint> Dirty => dirty.AsSpan();

        /// <summary>
        /// Gets the dirty ids the pass found itself, in the order noted: what the context's container log's Dirty
        /// stream gains for its descendants.
        /// </summary>
        public ReadOnlySpan<uint> Found => CollectionsMarshal.AsSpan(found);

        /// <summary>
        /// Gets the found dirty ids that are not local to the context: the ids whose visible ancestor rows the context
        /// re-stamps by hiding and copying them (<see cref="ContextTables.RebuildPass"/>).
        /// </summary>
        public ReadOnlySpan<uint> Restamp => CollectionsMarshal.AsSpan(restamp);

        /// <summary>
        /// Forgets everything, keeping the storage.
        /// </summary>
        internal void Clear()
        {
            dirty.Clear();
            found.Clear();
            restamp.Clear();
            changed = false;
        }

        /// <summary>
        /// Records that the pass claimed an id or changed a container entry.
        /// </summary>
        internal void NoteChange() => changed = true;

        /// <summary>
        /// Records an inherited dirty id.
        /// </summary>
        /// <param name="id">The id.</param>
        internal void NoteInherited(uint id) => dirty.Add(id);

        /// <summary>
        /// Records a dirty id the pass found.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="local">Whether it is local to the context, so that no ancestor row names it.</param>
        /// <returns><c>true</c> if the id was not dirty yet.</returns>
        internal bool NoteFound(uint id, bool local)
        {
            if (dirty.Add(id) == false)
                return false;

            found.Add(id);
            if (local == false)
                restamp.Add(id);

            return true;
        }

    }

}
