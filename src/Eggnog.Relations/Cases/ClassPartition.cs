using System;
using System.Buffers;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The meet of the live cases' partitions of their parent's ids (colored-contexts spec §11.1, §11.2, I33): the
    /// equalities between parent ids that hold in every live case, which the lift writes into the parent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two parent-canonical ids are equal in every live case exactly when they lie in one group of the seed case and
    /// have the same canonical id in each other case. So each seed group is refined by the members' signatures
    /// <c>(K_j.Find(m))</c> over the other cases j; every run of equal signatures with at least two members is a block of
    /// the meet. Ids in no seed group are alone in the seed, so they are equal to nothing new.
    /// </para>
    /// <para>
    /// Cost: O(Σ group size × (cases − 1) × depth) finds plus a sort per group; scratch arrays come from the shared
    /// array pool, and only the blocks are allocated. Choosing the seed with the fewest groups (the lift does) bounds the
    /// work by the least-merged case.
    /// </para>
    /// </remarks>
    internal static class ClassPartition
    {

        /// <summary>
        /// Appends to <paramref name="blocks"/> the blocks of the meet of the live cases' partitions (§11.1): for each group
        /// of <paramref name="seed"/>, the runs of at least two members whose canonical ids agree in every other live case,
        /// each block ascending. Deterministic: groups in root order, blocks in signature order.
        /// </summary>
        /// <param name="seed">The member map of <paramref name="live"/>[<paramref name="seedIndex"/>].</param>
        /// <param name="live">The live cases, all clean and caught up with their parent.</param>
        /// <param name="seedIndex">The seed case's index in <paramref name="live"/>.</param>
        /// <param name="blocks">Receives the blocks; each is a new array the caller owns.</param>
        /// <returns>The number of blocks appended.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="seedIndex"/> is not an index of
        /// <paramref name="live"/>.</exception>
        /// <exception cref="InvalidOperationException">A case may not be read.</exception>
        public static int Meet(MemberMap seed, IReadOnlyList<EGraphContext> live, int seedIndex, List<uint[]> blocks)
        {
            ArgumentNullException.ThrowIfNull(seed);
            ArgumentNullException.ThrowIfNull(live);
            ArgumentNullException.ThrowIfNull(blocks);
            ArgumentOutOfRangeException.ThrowIfNegative(seedIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(seedIndex, live.Count);

            var width = live.Count - 1;
            var finders = new LayeredUnionFind[width];
            for (int j = 0, k = 0; j < live.Count; j++)
                if (j != seedIndex)
                    finders[k++] = live[j].LiftFinder();

            var before = blocks.Count;
            var largest = 0;
            for (var g = 0; g < seed.GroupCount; g++)
                largest = Math.Max(largest, seed.GroupMembers(g).Length);

            if (largest < 2)
                return 0;

            var pool = ArrayPool<uint>.Shared;
            var members = pool.Rent(largest);
            var signatures = pool.Rent(Math.Max(1, largest * width));
            var order = ArrayPool<int>.Shared.Rent(largest);
            try
            {
                for (var g = 0; g < seed.GroupCount; g++)
                {
                    var group = seed.GroupMembers(g);
                    var n = group.Length;
                    if (n < 2)
                        continue;

                    if (width == 0)
                    {
                        // One live case: everything it merged holds in every live case.
                        blocks.Add(group.ToArray());
                        continue;
                    }

                    group.CopyTo(members);
                    for (var i = 0; i < n; i++)
                    {
                        order[i] = i;
                        for (var j = 0; j < width; j++)
                            signatures[i * width + j] = finders[j].Find(members[i]);
                    }

                    var comparer = new MemberSignatureComparer(signatures, members, width);
                    order.AsSpan(0, n).Sort(comparer);
                    var start = 0;
                    for (var i = 1; i <= n; i++)
                    {
                        if (i < n && comparer.SameSignature(order[start], order[i]))
                            continue;

                        if (i - start >= 2)
                        {
                            var block = new uint[i - start];
                            for (var b = 0; b < block.Length; b++)
                                block[b] = members[order[start + b]];

                            Array.Sort(block);
                            blocks.Add(block);
                        }

                        start = i;
                    }
                }
            }
            finally
            {
                pool.Return(members);
                pool.Return(signatures);
                ArrayPool<int>.Shared.Return(order);
            }

            return blocks.Count - before;
        }

    }

}
