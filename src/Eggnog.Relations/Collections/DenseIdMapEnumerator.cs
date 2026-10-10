using System.Collections;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Enumerates the present entries of a <see cref="DenseIdMap{TKey, TValue}"/> in ascending identifier order.
    /// </summary>
    /// <typeparam name="TKey">The identifier type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    public struct DenseIdMapEnumerator<TKey, TValue> : IEnumerator<KeyValuePair<TKey, TValue>>
        where TKey : struct, INumericId<TKey>
    {

        readonly TValue[] values;
        readonly bool[] present;
        readonly int idCount;
        int index;

        internal DenseIdMapEnumerator(TValue[] values, bool[] present, int idCount)
        {
            this.values = values;
            this.present = present;
            this.idCount = idCount;
            index = -1;
        }

        /// <inheritdoc />
        public readonly KeyValuePair<TKey, TValue> Current => new(TKey.FromIndex(index), values[index]);

        readonly object IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext()
        {
            while (++index < idCount)
                if (present[index])
                    return true;

            return false;
        }

        /// <inheritdoc />
        public void Reset() => index = -1;

        /// <inheritdoc />
        public readonly void Dispose()
        {
        }

    }

}
