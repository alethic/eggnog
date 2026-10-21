using System;

namespace Eggnog
{

    /// <summary>
    /// The vector primitives of <see cref="Primitives"/> for one element type, made once per <typeparamref name="T"/>:
    /// egglog's <c>vec-*</c> primitives over values (src/sort/vec.rs:114-150). <c>vec-get</c> has no result out of range,
    /// as egglog's; <c>vec-set</c> and <c>vec-remove</c> have none either, as egglog 3.0.0's, where the pin panics
    /// (decisions row 22).
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal static class VecPrimitiveCache<T> where T : notnull
    {

        /// <summary>
        /// <c>vec-empty</c>.
        /// </summary>
        public static readonly PrimitiveN<Vec<T>> Empty = Primitive.Create("vec-empty", Array.Empty<Type>(), static _ => Vec<T>.Empty);

        /// <summary>
        /// <c>vec-of</c>.
        /// </summary>
        public static readonly VariadicPrimitive<T, Vec<T>> Of = new("vec-of", static xs => Vec.From(xs));

        /// <summary>
        /// <c>vec-append</c>.
        /// </summary>
        public static readonly VariadicPrimitive<Vec<T>, Vec<T>> Append = new("vec-append", static xs =>
        {
            var result = Vec<T>.Empty;
            foreach (var x in xs)
                result = result.Append(x);

            return result;
        });

        /// <summary>
        /// <c>vec-push</c>.
        /// </summary>
        public static readonly Primitive<Vec<T>, T, Vec<T>> Push = new("vec-push", static (v, x) => v.Push(x), false);

        /// <summary>
        /// <c>vec-pop</c>: an empty vector stays empty.
        /// </summary>
        public static readonly Primitive<Vec<T>, Vec<T>> Pop = new("vec-pop", static v => v.Pop(), false);

        /// <summary>
        /// <c>vec-length</c>.
        /// </summary>
        public static readonly Primitive<Vec<T>, long> Length = new("vec-length", static v => v.Count, false);

        /// <summary>
        /// <c>vec-contains</c>.
        /// </summary>
        public static readonly Primitive<Vec<T>, T, bool> Contains = new("bool-vec-contains", static (v, x) => v.Contains(x), false)
        {
            FilterName = "vec-contains",
        };

        /// <summary>
        /// <c>vec-not-contains</c>.
        /// </summary>
        public static readonly Primitive<Vec<T>, T, bool> NotContains =
            new("bool-vec-not-contains", static (v, x) => v.Contains(x) == false, false)
        {
            FilterName = "vec-not-contains",
        };

        /// <summary>
        /// <c>vec-get</c>: no result out of range.
        /// </summary>
        public static readonly Primitive<Vec<T>, long, T> Get = BuiltInPrimitive.Partial<Vec<T>, long, T>("vec-get", TryGet);

        /// <summary>
        /// <c>vec-set</c>: no result out of range.
        /// </summary>
        public static readonly Primitive<Vec<T>, long, T, Vec<T>> Set = new(
            "vec-set",
            static (v, i, x) => TrySet(v, i, x, out var result) ? result : throw Primitive.NoResult("vec-set"),
            TrySet);

        /// <summary>
        /// <c>vec-remove</c>: no result out of range.
        /// </summary>
        public static readonly Primitive<Vec<T>, long, Vec<T>> Remove =
            BuiltInPrimitive.Partial<Vec<T>, long, Vec<T>>("vec-remove", TryRemove);

        static bool TryGet(Vec<T> vec, long index, out T result)
        {
            if ((ulong)index < (ulong)vec.Count)
            {
                result = vec[(int)index];
                return true;
            }

            result = default!;
            return false;
        }

        static bool TrySet(Vec<T> vec, long index, T item, out Vec<T> result)
        {
            if ((ulong)index < (ulong)vec.Count)
            {
                result = vec.SetItem((int)index, item);
                return true;
            }

            result = vec;
            return false;
        }

        static bool TryRemove(Vec<T> vec, long index, out Vec<T> result)
        {
            if ((ulong)index < (ulong)vec.Count)
            {
                result = vec.RemoveAt((int)index);
                return true;
            }

            result = vec;
            return false;
        }

    }

}
