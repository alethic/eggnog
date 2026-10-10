using System;
using System.Collections;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A collection whose enumerator yields 1, 2, ... and then throws <see cref="NotSupportedException"/>, to check that
    /// <see cref="ParallelVecWriter{T}.WriteContents(IReadOnlyCollection{T})"/> releases its read lock when the
    /// enumeration fails.
    /// </summary>
    sealed class ParallelVecWriterThrowingCollection : IReadOnlyCollection<int>
    {

        readonly int yieldedBeforeThrowing;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="claimedCount">The count to report.</param>
        /// <param name="yieldedBeforeThrowing">How many items to yield before throwing.</param>
        public ParallelVecWriterThrowingCollection(int claimedCount, int yieldedBeforeThrowing)
        {
            Count = claimedCount;
            this.yieldedBeforeThrowing = yieldedBeforeThrowing;
        }

        /// <inheritdoc />
        public int Count { get; }

        /// <inheritdoc />
        public IEnumerator<int> GetEnumerator()
        {
            for (var i = 0; i < yieldedBeforeThrowing; i++)
                yield return i + 1;

            throw new NotSupportedException("The enumeration failed.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    }

}
