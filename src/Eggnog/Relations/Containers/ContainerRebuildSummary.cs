using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// What a container rebuild did: egglog's <c>ContainerRebuildSummary</c> (core-relations/src/containers/mod.rs:76-122).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Changed"/> means some container entry changed, because its contents or its id were rewritten. The
    /// bridge keeps rebuilding while it is set.
    /// </para>
    /// <para>
    /// <see cref="DirtyIds"/> is narrower: the ids of containers whose meaning changed while their id stayed the same,
    /// closed upward under "is held by a container" (<see cref="ContainerValues.RebuildAll"/>). An ordinary table rebuild
    /// already gives rows that mention a renamed id a fresh timestamp; rows that mention a dirty id get none, so the
    /// bridge re-stamps them with <see cref="Database.RefreshRowsForValues"/>. For example <c>l(vec-of(w(k(b))))</c> can
    /// rebuild to <c>l(vec-of(k(b)))</c> without the vector's id changing: the row is newly matchable, and seminaive
    /// evaluation would miss it unless it is re-stamped.
    /// </para>
    /// <para>
    /// The dirty ids keep the order they were noted in. Not thread-safe: parallel rebuild tasks fill their own summaries,
    /// which are then combined with <see cref="Extend"/>.
    /// </para>
    /// </remarks>
    internal sealed class ContainerRebuildSummary
    {

        readonly OrderedValueSet dirtyIds;
        bool changed;

        /// <summary>
        /// Initializes a summary of a rebuild that changed nothing.
        /// </summary>
        public ContainerRebuildSummary()
        {
            dirtyIds = new OrderedValueSet();
        }

        /// <summary>
        /// Gets whether any container entry changed. egglog's <c>changed</c>.
        /// </summary>
        public bool Changed => changed;

        /// <summary>
        /// Gets the ids of containers whose parent rows need re-stamping, in the order they were noted. egglog's
        /// <c>dirty_ids</c>. Valid until the summary next changes.
        /// </summary>
        public ReadOnlySpan<uint> DirtyIds => dirtyIds.AsSpan();

        /// <summary>
        /// Gets the number of dirty ids.
        /// </summary>
        public int DirtyCount => dirtyIds.Count;

        /// <summary>
        /// Gets whether <paramref name="id"/> is a dirty id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if noted dirty.</returns>
        public bool IsDirty(uint id) => dirtyIds.Contains(id);

        /// <summary>
        /// Records that some entry changed. egglog's <c>note_change</c>.
        /// </summary>
        public void NoteChange() => changed = true;

        /// <summary>
        /// Records that the container <paramref name="id"/> changed meaning under the same id, which is also a change.
        /// egglog's <c>note_dirty_id</c>.
        /// </summary>
        /// <param name="id">The container id.</param>
        /// <returns><c>true</c> if the id was not dirty yet.</returns>
        public bool NoteDirtyId(uint id)
        {
            changed = true;
            return dirtyIds.Add(id);
        }

        /// <summary>
        /// Forgets every change and dirty id, keeping the storage: a child's container passes reuse one summary
        /// (<see cref="ContainerLayers.RebuildPass"/>).
        /// </summary>
        internal void Clear()
        {
            dirtyIds.Clear();
            changed = false;
        }

        /// <summary>
        /// Folds <paramref name="other"/> into this summary: the changes combine and its dirty ids are appended. egglog's
        /// <c>extend</c>.
        /// </summary>
        /// <param name="other">Another summary; not modified.</param>
        public void Extend(ContainerRebuildSummary other)
        {
            ArgumentNullException.ThrowIfNull(other);

            changed |= other.changed;
            dirtyIds.AddRange(other.dirtyIds.AsSpan());
        }

    }

}
