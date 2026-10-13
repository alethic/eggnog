namespace Eggnog.Relations
{

    /// <summary>
    /// A pool seen without its element type, so that a <see cref="PoolSet"/> can release every pool it holds. Cold
    /// path only.
    /// </summary>
    internal interface IPool
    {

        /// <summary>
        /// Drops every object the pool holds, on every thread, and releases the pool's own bookkeeping storage.
        /// </summary>
        void Clear();

    }

}
