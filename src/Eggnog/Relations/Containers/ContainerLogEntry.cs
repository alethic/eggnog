using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One Added entry of a <see cref="ContainerLog"/>: a container of classes appeared in a level's environment of type
    /// <see cref="Type"/> under <see cref="Id"/>, interned, reinserted by a rebuild or renamed there. The content hash
    /// names the entry among those that share the id (two containers share one after a rebuild renamed one onto the
    /// other's id); an entry that has gone since resolves to nothing, and its successor has an entry of its own.
    /// </summary>
    /// <remarks>
    /// Entries order by type, then id, then hash: a reader sorts what it drained, since the parallel tasks of a level
    /// buffer theirs in no fixed order.
    /// </remarks>
    /// <param name="Type">The container type's id in the root's <see cref="ContainerValues"/>.</param>
    /// <param name="Id">The id the container appeared under.</param>
    /// <param name="Hash">Its content hash (<see cref="IContainerValue{TSelf}.ComputeHash"/>).</param>
    internal readonly record struct ContainerLogEntry(ContainerTypeId Type, uint Id, ulong Hash) : IComparable<ContainerLogEntry>
    {

        /// <inheritdoc />
        public int CompareTo(ContainerLogEntry other)
        {
            var byType = Type.Index.CompareTo(other.Type.Index);
            if (byType != 0)
                return byType;

            var byId = Id.CompareTo(other.Id);
            return byId != 0 ? byId : Hash.CompareTo(other.Hash);
        }

    }

}
