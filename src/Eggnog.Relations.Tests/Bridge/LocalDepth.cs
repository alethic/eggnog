namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A context-local value for <see cref="EGraphContextTests"/>: the depth of the context that built it.
    /// </summary>
    sealed class LocalDepth
    {

        public LocalDepth(int depth)
        {
            Depth = depth;
        }

        public int Depth { get; }

    }

}
