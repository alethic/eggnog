using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A deliberately simple partition of [0, n) with no parent pointers: every element names its class, every
    /// class lists its members and remembers its minimum, and a union moves the smaller class's members into the
    /// larger one. Obviously correct, O(n log n) over any sequence of unions, and it predicts exactly what a
    /// union-by-min union-find must answer: the canonical identifier of a class is its minimum member.
    /// </summary>
    sealed class PartitionReference
    {

        readonly int[] classOf;
        readonly List<uint>[] members;
        readonly uint[] minimum;

        /// <summary>
        /// Initializes a partition of [0, <paramref name="size"/>) into singletons.
        /// </summary>
        /// <param name="size">The number of elements.</param>
        public PartitionReference(int size)
        {
            classOf = new int[size];
            members = new List<uint>[size];
            minimum = new uint[size];
            for (var i = 0; i < size; i++)
                members[i] = new List<uint>();

            Reset();
        }

        /// <summary>
        /// Gets the number of elements.
        /// </summary>
        public int Size => classOf.Length;

        /// <summary>
        /// Gets the minimum member of <paramref name="x"/>'s class.
        /// </summary>
        /// <param name="x">The element.</param>
        /// <returns>The canonical element.</returns>
        public uint Canonical(uint x) => minimum[classOf[x]];

        /// <summary>
        /// Merges the classes of <paramref name="a"/> and <paramref name="b"/>.
        /// </summary>
        /// <param name="a">An element.</param>
        /// <param name="b">Another element.</param>
        /// <param name="parent">The canonical element of the merged class.</param>
        /// <param name="child">The canonical element that was displaced; equal to <paramref name="parent"/> if none.</param>
        /// <returns>Whether the classes were distinct.</returns>
        public bool Union(uint a, uint b, out uint parent, out uint child)
        {
            var ca = classOf[a];
            var cb = classOf[b];
            parent = Math.Min(minimum[ca], minimum[cb]);
            child = Math.Max(minimum[ca], minimum[cb]);
            if (ca == cb)
                return false;

            if (members[ca].Count < members[cb].Count)
                (ca, cb) = (cb, ca);

            foreach (var m in members[cb])
                classOf[m] = ca;

            members[ca].AddRange(members[cb]);
            members[cb].Clear();
            minimum[ca] = parent;
            return true;
        }

        /// <summary>
        /// Makes every element a singleton again.
        /// </summary>
        public void Reset()
        {
            for (var i = 0; i < classOf.Length; i++)
            {
                classOf[i] = i;
                members[i].Clear();
                members[i].Add((uint)i);
                minimum[i] = (uint)i;
            }
        }

    }

}
