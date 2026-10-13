using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// An insertion-ordered set of atoms with indexmap's <c>swap_remove</c> and <c>pop</c>, as used by egglog's
    /// <c>BucketQueue</c> (an <c>IndexSet&lt;AtomId&gt;</c> per bucket, core-relations/src/free_join/plan.rs:1308).
    /// </summary>
    /// <remarks>
    /// The order matters: <see cref="Pop"/> takes the last element, and <see cref="SwapRemove"/> moves the last element
    /// into the removed one's slot, exactly as indexmap does, so set-cover tie-breaks match egglog's.
    /// </remarks>
    internal sealed class AtomIndexSet
    {

        readonly List<AtomId> items = new();
        readonly Dictionary<AtomId, int> positions = new();

        /// <summary>
        /// Gets the number of atoms.
        /// </summary>
        public int Count => items.Count;

        /// <summary>
        /// Appends <paramref name="atom"/> if absent. O(1).
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <returns><c>true</c> if added.</returns>
        public bool Insert(AtomId atom)
        {
            if (positions.ContainsKey(atom))
                return false;

            positions.Add(atom, items.Count);
            items.Add(atom);
            return true;
        }

        /// <summary>
        /// Removes <paramref name="atom"/> by moving the last element into its slot. O(1).
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <returns><c>true</c> if it was present.</returns>
        public bool SwapRemove(AtomId atom)
        {
            if (positions.Remove(atom, out var index) == false)
                return false;

            var last = items.Count - 1;
            if (index != last)
            {
                var moved = items[last];
                items[index] = moved;
                positions[moved] = index;
            }

            items.RemoveAt(last);
            return true;
        }

        /// <summary>
        /// Removes and returns the last atom. O(1).
        /// </summary>
        /// <param name="atom">The atom removed.</param>
        /// <returns><c>false</c> if the set is empty.</returns>
        public bool Pop(out AtomId atom)
        {
            if (items.Count == 0)
            {
                atom = default;
                return false;
            }

            atom = items[^1];
            items.RemoveAt(items.Count - 1);
            positions.Remove(atom);
            return true;
        }

    }

}
