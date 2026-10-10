using System.Collections;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A collection whose <see cref="Count"/> disagrees with what it yields: the analogue of the
    /// <c>BadExactSizeIterator</c> in egglog's concurrency/src/tests.rs:461-489.
    /// </summary>
    sealed class ParallelVecWriterLyingCollection : IReadOnlyCollection<int>
    {

        readonly int[] items;

        public ParallelVecWriterLyingCollection(int claimedCount, params int[] items)
        {
            Count = claimedCount;
            this.items = items;
        }

        public int Count { get; }

        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    }

}
