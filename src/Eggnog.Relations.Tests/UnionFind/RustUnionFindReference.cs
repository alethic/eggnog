using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A line-for-line transliteration of egglog's sequential union-find (<c>union-find/src/lib.rs</c>) over a
    /// growable list, used to check that the port leaves the parent array in exactly the same state after every
    /// operation, not just the same partition.
    /// </summary>
    sealed class RustUnionFindReference
    {

        readonly List<uint> parents = new();

        /// <summary>
        /// Gets the parent array.
        /// </summary>
        public IReadOnlyList<uint> Parents => parents;

        /// <summary>
        /// <c>reset</c>: every element points to itself.
        /// </summary>
        public void Reset()
        {
            for (var i = 0; i < parents.Count; i++)
                parents[i] = (uint)i;
        }

        /// <summary>
        /// <c>reserve</c>: push identity entries up to <paramref name="v"/>.
        /// </summary>
        /// <param name="v">The element.</param>
        public void Reserve(uint v)
        {
            if (v >= parents.Count)
                for (var i = (uint)parents.Count; i <= v; i++)
                    parents.Add(i);
        }

        /// <summary>
        /// <c>union</c>: reserve both, find both, link the larger root under the smaller.
        /// </summary>
        /// <param name="a">An element.</param>
        /// <param name="b">Another element.</param>
        /// <returns>The parent and child roots.</returns>
        public (uint Parent, uint Child) Union(uint a, uint b)
        {
            Reserve(a);
            Reserve(b);
            a = Find(a);
            b = Find(b);
            if (a != b)
            {
                var parent = Math.Min(a, b);
                var child = Math.Max(a, b);
                parents[(int)child] = parent;
                return (parent, child);
            }

            return (a, a);
        }

        /// <summary>
        /// <c>find</c>: reserve, then walk with path halving.
        /// </summary>
        /// <param name="id">The element.</param>
        /// <returns>The root.</returns>
        public uint Find(uint id)
        {
            Reserve(id);
            var cur = id;
            while (true)
            {
                var parent = parents[(int)cur];
                if (cur == parent)
                    break;

                var grand = parents[(int)parent];
                parents[(int)cur] = grand;
                cur = grand;
            }

            return cur;
        }

        /// <summary>
        /// <c>find_naive</c>: walk without writing; unreserved elements are roots.
        /// </summary>
        /// <param name="id">The element.</param>
        /// <returns>The root.</returns>
        public uint FindNaive(uint id)
        {
            if (parents.Count <= id)
                return id;

            var cur = id;
            while (true)
            {
                var parent = parents[(int)cur];
                if (cur == parent)
                    break;

                cur = parent;
            }

            return cur;
        }

    }

}
