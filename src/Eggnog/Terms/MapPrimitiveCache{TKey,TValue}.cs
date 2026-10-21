using System;

namespace Eggnog
{

    /// <summary>
    /// The map primitives of <see cref="Primitives"/> for one key and value type, made once per pair of types: egglog's
    /// <c>map-*</c> primitives over values (src/sort/map.rs:139-155). <c>map-get</c> has no result for a missing key, as
    /// egglog's.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    internal static class MapPrimitiveCache<TKey, TValue>
        where TKey : notnull
        where TValue : notnull
    {

        /// <summary>
        /// <c>map-empty</c>.
        /// </summary>
        public static readonly PrimitiveN<Map<TKey, TValue>> Empty =
            Primitive.Create("map-empty", Array.Empty<Type>(), static _ => Map<TKey, TValue>.Empty);

        /// <summary>
        /// <c>map-insert</c>: a key's value set, replacing any it had.
        /// </summary>
        public static readonly Primitive<Map<TKey, TValue>, TKey, TValue, Map<TKey, TValue>> Insert =
            new("map-insert", static (m, k, v) => m.SetItem(k, v), false);

        /// <summary>
        /// <c>map-get</c>: no result for a missing key.
        /// </summary>
        public static readonly Primitive<Map<TKey, TValue>, TKey, TValue> Get =
            BuiltInPrimitive.Partial<Map<TKey, TValue>, TKey, TValue>("map-get", TryGet);

        /// <summary>
        /// <c>map-remove</c>.
        /// </summary>
        public static readonly Primitive<Map<TKey, TValue>, TKey, Map<TKey, TValue>> Remove =
            new("map-remove", static (m, k) => m.Remove(k), false);

        /// <summary>
        /// <c>map-length</c>.
        /// </summary>
        public static readonly Primitive<Map<TKey, TValue>, long> Length = new("map-length", static m => m.Count, false);

        /// <summary>
        /// <c>map-contains</c>.
        /// </summary>
        public static readonly Primitive<Map<TKey, TValue>, TKey, bool> Contains =
            new("bool-map-contains", static (m, k) => m.ContainsKey(k), false)
        {
            FilterName = "map-contains",
        };

        /// <summary>
        /// <c>map-not-contains</c>.
        /// </summary>
        public static readonly Primitive<Map<TKey, TValue>, TKey, bool> NotContains =
            new("bool-map-not-contains", static (m, k) => m.ContainsKey(k) == false, false)
        {
            FilterName = "map-not-contains",
        };

        static bool TryGet(Map<TKey, TValue> map, TKey key, out TValue value) => map.TryGetValue(key, out value!);

    }

}
