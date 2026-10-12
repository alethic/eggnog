namespace Eggnog
{

    /// <summary>
    /// The set primitives of <see cref="Primitives"/> for one element type, made once per <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal static class PrimitiveCache<T> where T : notnull
    {

        /// <summary>
        /// <c>set-of</c>: the singleton set.
        /// </summary>
        public static readonly Primitive<T, ValueSet<T>> Singleton = new("set-of", static v => ValueSet.Of(v), false);

        /// <summary>
        /// <c>set-union</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Union = new("set-union", static (a, b) => a.Union(b), false);

        /// <summary>
        /// <c>set-intersect</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Intersect = new("set-intersect", static (a, b) => a.Intersect(b), false);

        /// <summary>
        /// <c>set-diff</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Except = new("set-diff", static (a, b) => a.Except(b), false);

        /// <summary>
        /// <c>set-insert</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, T, ValueSet<T>> Insert = new("set-insert", static (s, v) => s.Add(v), false);

        /// <summary>
        /// <c>set-remove</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, T, ValueSet<T>> Remove = new("set-remove", static (s, v) => s.Remove(v), false);

        /// <summary>
        /// <c>set-contains</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, T, bool> Contains = new("set-contains", static (s, v) => s.Contains(v), false);

        /// <summary>
        /// <c>set-subset</c>: whether the first set is a subset of the second.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, ValueSet<T>, bool> IsSubsetOf = new("set-subset", static (a, b) => a.IsSubsetOf(b), false);

        /// <summary>
        /// <c>set-empty</c>: whether the set is empty.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, bool> IsEmpty = new("set-empty", static s => s.IsEmpty, false);

        /// <summary>
        /// <c>set-length</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, long> Count = new("set-length", static s => s.Count, false);

    }

}
