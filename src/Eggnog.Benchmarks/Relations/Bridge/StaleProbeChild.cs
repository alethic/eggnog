namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The child a <see cref="StaleProbe"/> run keeps beside the root.
    /// </summary>
    internal enum StaleProbeChild
    {

        /// <summary>
        /// No child: the root compacts as egglog's does.
        /// </summary>
        None,

        /// <summary>
        /// One child of the root, created before the first iteration and held, unused, to the end: its cursors stay
        /// where the root was when it was created.
        /// </summary>
        Held,

        /// <summary>
        /// One child of the root, created before the first iteration and caught up after every iteration: its cursors
        /// move the way a split step moves a case's, but it hides nothing.
        /// </summary>
        CaughtUp,

    }

}
