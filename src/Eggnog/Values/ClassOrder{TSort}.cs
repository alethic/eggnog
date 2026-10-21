using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The order of the classes of a sort in a map's keys or a multiset's elements: by their cells, as egglog's
    /// <c>BTreeMap</c> orders values by their representations. A class's cell is the one it had when its container was
    /// made; a container of classes holds them canonical.
    /// </summary>
    /// <typeparam name="TSort">The sort.</typeparam>
    internal sealed class ClassOrder<TSort> : IComparer<TSort> where TSort : struct, ISort<TSort>
    {

        /// <summary>
        /// Gets the order.
        /// </summary>
        public static ClassOrder<TSort> Instance { get; } = new();

        ClassOrder()
        {
        }

        /// <inheritdoc />
        public int Compare(TSort x, TSort y) => x.Id.Cell.CompareTo(y.Id.Cell);

    }

}
