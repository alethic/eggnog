using System.Threading;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's <c>impl Clear for Dropper</c>: clearing marks the object cleared and stops it asking to be reused; it
    /// weighs nothing. Counts creations, the C# stand-in for egglog's drop counter.
    /// </summary>
    readonly struct PoolDropperPolicy : IPoolPolicy<PoolDropper>
    {

        static int created;

        /// <summary>
        /// Gets the number of objects created so far.
        /// </summary>
        public static int Created => Volatile.Read(ref created);

        public static PoolDropper Create()
        {
            Interlocked.Increment(ref created);
            return new PoolDropper();
        }

        public static void Clear(PoolDropper item)
        {
            item.Cleared = true;
            item.Reuse = false;
        }

        public static bool ShouldReuse(PoolDropper item) => item.Reuse;

        public static long Bytes(PoolDropper item) => 0;

        public static long LimitBytes => long.MaxValue;

    }

}
