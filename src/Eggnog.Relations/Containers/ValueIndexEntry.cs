using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The slot of <see cref="ContainerValueIndex"/> for one element value: the multiset of ids of the containers that
    /// hold it, egglog's <c>IndexSet&lt;Value&gt;</c> under one <c>val_index</c> key.
    /// </summary>
    /// <remarks>
    /// Most values sit in one container, so a single id and its count live inline and allocate nothing; a second
    /// distinct id moves the multiset into <see cref="Many"/>, and it moves back inline when one id remains. Counts
    /// are occurrences: a container holding the value twice counts twice, and two containers sharing an id count
    /// separately. An entry exists only while its multiset is nonempty.
    /// </remarks>
    internal struct ValueIndexEntry
    {

        /// <summary>
        /// The element value.
        /// </summary>
        public uint Element;

        /// <summary>
        /// The only container id, when <see cref="Many"/> is <c>null</c>.
        /// </summary>
        public uint Id;

        /// <summary>
        /// The occurrences of <see cref="Id"/>, at least one, when <see cref="Many"/> is <c>null</c>.
        /// </summary>
        public int Count;

        /// <summary>
        /// The container ids with their occurrence counts (all positive, at least two ids), or <c>null</c> when the
        /// multiset is inline.
        /// </summary>
        public Dictionary<uint, int>? Many;

    }

}
