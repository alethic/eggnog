namespace Eggnog.Relations
{

    /// <summary>
    /// What a child context makes of an Added entry of an unsettled ancestor's container log, a container of classes
    /// that appeared at that level after the child last caught up with it (<see cref="ContainerLayers.ScanAdds"/>,
    /// <see cref="ContainerLayers.CatchUp"/>): the classes of the design's catch-up, checked in this order.
    /// </summary>
    internal enum ContainerCatchUpClass : byte
    {

        /// <summary>
        /// A level strictly between the ancestor and the child shadows the entry's id in its layer of the entry's type:
        /// that level claimed the id, catches up with the entry itself and logs what it does with it, which the child
        /// reads from that level's log. Skipped.
        /// </summary>
        HiddenAbove,

        /// <summary>
        /// No container with the id and content hash is stored at the ancestor any more: it was rebuilt, renamed or
        /// merged away since, and its successor has an Added entry of its own. Skipped.
        /// </summary>
        Gone,

        /// <summary>
        /// The child shadows the entry's id in its layer of the entry's type: the ancestor put content on an id the
        /// child owns, which the child's view hides. Content renamed onto the id, or reinserted under it, from an id
        /// the child does not shadow is adopted: its rebuild goes into the child's layer under the id's canonical id,
        /// also where a level in between shadows that other id, which holds no copy of it under this one. Content from
        /// an id the child shadows too, which the child's own copy follows onto the id, is left alone, and so is a
        /// content the ancestor rebuilt in place, which the child's copy stands for, unless content also arrived under
        /// the id in the same catch-up, when both are adopted (<see cref="ContainerLayers.CatchUp"/>).
        /// </summary>
        Adopt,

        /// <summary>
        /// The child sees the entry, and its id or an element of a container it names is not canonical in the child:
        /// the child claims the id, so that its container pass rebuilds the copy.
        /// </summary>
        Candidate,

        /// <summary>
        /// The child sees the entry, canonical, and its own layer of the type holds a content the entry names under
        /// another id: the view would hold the content twice. The child settles it at once, merging the two ids.
        /// </summary>
        Collision,

        /// <summary>
        /// None of the above: the child sees the entry as it is. Consumed.
        /// </summary>
        Inert,

    }

}
