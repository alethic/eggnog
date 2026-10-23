using System;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>vec-union</c> for one sort, made once per <typeparamref name="TSort"/>: the write primitive of a vector
    /// of an eq sort (src/sort/vec.rs:140-148, 285-340).
    /// </summary>
    /// <typeparam name="TSort">The sort.</typeparam>
    internal static class VecUnionCache<TSort> where TSort : struct, ISort<TSort>
    {

        /// <summary>
        /// <c>vec-union</c>: unions the classes of two vectors of one length, element by element, and returns the first;
        /// no result for two lengths.
        /// </summary>
        public static readonly Primitive<Vec<TSort>, Vec<TSort>, Vec<TSort>> Union = new("vec-union", Write);

        static (bool Found, uint Cell) Write(ColumnType vector, uint left, uint right, Action<uint, uint> union, Relations.ContainerView view)
        {
            var type = (VecColumnType<TSort>)vector;
            var a = type.ElementCells(left, view);
            var b = type.ElementCells(right, view);
            if (a.Length != b.Length)
                return (false, 0);

            for (var i = 0; i < a.Length; i++)
                union(a[i], b[i]);

            return (true, left);
        }

    }

}
