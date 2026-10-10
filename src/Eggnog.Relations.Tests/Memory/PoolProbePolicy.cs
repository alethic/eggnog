namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Pool policy for <see cref="PoolProbe"/>: eight bytes each, and always reusable through the interface's default
    /// <see cref="IPoolPolicy{T}.ShouldReuse"/> (egglog's default <c>reuse</c>). Its shared pool holds sixteen probes
    /// per thread.
    /// </summary>
    readonly struct PoolProbePolicy : IPoolPolicy<PoolProbe>
    {

        public static PoolProbe Create() => new();

        public static void Clear(PoolProbe item)
        {
            item.Cleared = true;
            item.Payload = 0;
        }

        public static long Bytes(PoolProbe item) => 8;

        public static long LimitBytes => 16 * 8;

    }

}
