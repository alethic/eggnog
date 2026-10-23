using System;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>multiset-union-values</c> for one sort, made once per <typeparamref name="TSort"/>: the write primitive
    /// of a multiset of an eq sort (src/sort/multiset.rs:198-208, 785-832).
    /// </summary>
    /// <typeparam name="TSort">The sort.</typeparam>
    internal static class MultiSetUnionValuesCache<TSort> where TSort : struct, ISort<TSort>
    {

        /// <summary>
        /// <c>multiset-union-values</c>: unions every element of a multiset with its first, the element of least cell,
        /// and returns that; no result for an empty multiset.
        /// </summary>
        public static readonly Primitive<MultiSet<TSort>, TSort> UnionValues = new("multiset-union-values", Write);

        static (bool Found, uint Cell) Write(ColumnType multiset, uint cell, Action<uint, uint> union, Relations.ContainerView view)
        {
            var elements = ((MultiSetColumnType<TSort>)multiset).ElementCells(cell, view);
            if (elements.Length == 0)
                return (false, 0);

            for (var i = 1; i < elements.Length; i++)
                union(elements[0], elements[i]);

            return (true, elements[0]);
        }

    }

}
