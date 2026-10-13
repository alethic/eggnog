using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The classes a case merged over its parent's ids, as the lift reads them (colored-contexts spec §11.1, L2, G1(a),
    /// X29, I32): for every class of the case's union-find overlay with at least one member that is not local to the
    /// case, the class's canonical id in the case (its <em>group root</em>) and those members, ascending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Meaning (I32): <see cref="Members"/>(<c>c</c>) is exactly the set of ids <c>m</c> that are canonical in the
    /// parent, not local to the case, and have <c>Find_K(m) = c</c>. A group's list includes its root when the root is a
    /// parent id. For a case-canonical id in no group the list is <c>[c]</c> when <c>c</c> is a parent id and empty when
    /// it is local: a local class with no parent member cannot be named in the parent.
    /// </para>
    /// <para>
    /// Representation: the roots ascending, and the members of all groups in one array, group by group; a lookup is a
    /// binary search over the roots. Immutable once built, so any number of threads may read it. Built from the overlay's
    /// slots in O(members × log members); <see cref="Version"/> is the overlay's version at the build, which
    /// <see cref="EGraphContext.GetMemberMap"/> compares to reuse the map.
    /// </para>
    /// </remarks>
    internal sealed class MemberMap
    {

        readonly uint[] roots;
        readonly int[] starts;
        readonly uint[] members;
        readonly IdBlockAllocator localIds;
        readonly int version;

        /// <summary>
        /// Initializes a map over sorted roots, their member ranges and the members.
        /// </summary>
        MemberMap(uint[] roots, int[] starts, uint[] members, IdBlockAllocator localIds, int version)
        {
            this.roots = roots;
            this.starts = starts;
            this.members = members;
            this.localIds = localIds;
            this.version = version;
        }

        /// <summary>
        /// Gets the overlay version the map was built at.
        /// </summary>
        public int Version => version;

        /// <summary>
        /// Gets the number of groups: classes of the case with at least one member that is not local.
        /// </summary>
        public int GroupCount => roots.Length;

        /// <summary>
        /// Gets the groups' roots (their canonical ids in the case), ascending.
        /// </summary>
        public ReadOnlySpan<uint> GroupRoots => roots;

        /// <summary>
        /// Gets the number of members over all groups.
        /// </summary>
        public int MemberCount => members.Length;

        /// <summary>
        /// Gets the members of group <paramref name="group"/> (an index into <see cref="GroupRoots"/>), ascending.
        /// </summary>
        /// <param name="group">The group's index.</param>
        /// <returns>Its members that are not local to the case.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
        public ReadOnlySpan<uint> GroupMembers(int group)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(group);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(group, roots.Length);
            return new ReadOnlySpan<uint>(members, starts[group], starts[group + 1] - starts[group]);
        }

        /// <summary>
        /// Gets the parent ids the case-canonical id <paramref name="caseCanonical"/> stands for (I32): its group's
        /// members; else <c>[c]</c> if it is not local to the case; else nothing. O(log groups).
        /// </summary>
        /// <remarks>
        /// The one-element answer is a span over the argument itself, so it lives as long as the variable passed in.
        /// </remarks>
        /// <param name="caseCanonical">An id canonical in the case.</param>
        /// <returns>The members, ascending.</returns>
        public ReadOnlySpan<uint> Members(in uint caseCanonical)
        {
            var group = IndexOf(caseCanonical);
            if (group >= 0)
                return new ReadOnlySpan<uint>(members, starts[group], starts[group + 1] - starts[group]);

            return localIds.Contains(caseCanonical) ? ReadOnlySpan<uint>.Empty : new ReadOnlySpan<uint>(in caseCanonical);
        }

        /// <summary>
        /// Gets the index of the group whose root is <paramref name="root"/>, or -1. O(log groups).
        /// </summary>
        /// <param name="root">A case-canonical id.</param>
        /// <returns>The group's index, or -1.</returns>
        public int IndexOf(uint root) => Array.BinarySearch(roots, root) is var i and >= 0 ? i : -1;

        /// <summary>
        /// Gets whether <paramref name="id"/> is local to the case the map was built for.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if the case minted it.</returns>
        public bool IsLocal(uint id) => localIds.Contains(id);

        /// <summary>
        /// Builds the member map of the child <paramref name="k"/> afresh (§11.1), without its cache. Requires the child to
        /// be clean.
        /// </summary>
        /// <param name="k">A child context.</param>
        /// <returns>The map.</returns>
        /// <exception cref="InvalidOperationException">The context is the root, or may not be read.</exception>
        public static MemberMap Build(EGraphContext k)
        {
            ArgumentNullException.ThrowIfNull(k);
            return k.BuildMemberMap();
        }

        /// <summary>
        /// Builds the map of <paramref name="overlay"/>'s classes, leaving out the members <paramref name="localIds"/>
        /// holds. The overlay must be current with its parent (its members canonical there, I6) and must not change
        /// meanwhile.
        /// </summary>
        /// <param name="overlay">The case's overlay.</param>
        /// <param name="localIds">The case's id blocks.</param>
        /// <returns>The map.</returns>
        internal static MemberMap Build(UnionFindOverlay overlay, IdBlockAllocator localIds)
        {
            ArgumentNullException.ThrowIfNull(overlay);
            ArgumentNullException.ThrowIfNull(localIds);
            var version = overlay.Version;
            var slotCount = overlay.SlotCount;

            // Count the groups and their members that are not local.
            var groupCount = 0;
            var memberCount = 0;
            for (var slot = 0; slot < slotCount; slot++)
            {
                if (overlay.CountOf(slot) < 2)
                    continue;

                var kept = 0;
                foreach (var m in overlay.MembersOf(slot))
                    if (localIds.Contains(m) == false)
                        kept++;

                if (kept == 0)
                    continue;

                groupCount++;
                memberCount += kept;
            }

            if (groupCount == 0)
                return new MemberMap(Array.Empty<uint>(), [0], Array.Empty<uint>(), localIds, version);

            // Roots ascending: sort (root, slot) pairs, then lay the members out group by group.
            var order = new ulong[groupCount];
            var g = 0;
            for (var slot = 0; slot < slotCount; slot++)
            {
                if (overlay.CountOf(slot) < 2)
                    continue;

                var any = false;
                foreach (var m in overlay.MembersOf(slot))
                {
                    if (localIds.Contains(m) == false)
                    {
                        any = true;
                        break;
                    }
                }

                if (any)
                    order[g++] = ((ulong)overlay.CanonicalOf(slot) << 32) | (uint)slot;
            }

            Array.Sort(order);
            var roots = new uint[groupCount];
            var starts = new int[groupCount + 1];
            var members = new uint[memberCount];
            var at = 0;
            for (var i = 0; i < groupCount; i++)
            {
                roots[i] = (uint)(order[i] >> 32);
                starts[i] = at;
                var start = at;
                foreach (var m in overlay.MembersOf((int)(uint)order[i]))
                    if (localIds.Contains(m) == false)
                        members[at++] = m;

                Array.Sort(members, start, at - start);
            }

            starts[groupCount] = at;
            return new MemberMap(roots, starts, members, localIds, version);
        }

    }

}
