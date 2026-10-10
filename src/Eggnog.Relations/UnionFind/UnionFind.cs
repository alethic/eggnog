using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A single-threaded union-find over <see cref="uint"/> identifiers that links by minimum identifier, compresses
    /// with path halving in <see cref="Find"/>, and answers read-only queries with <see cref="FindNaive"/>.
    /// Port of egglog's <c>union_find::UnionFind</c> (<c>union-find/src/lib.rs</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Union by min: when two classes merge, the larger root is linked under the smaller one. The heuristic gives no
    /// rank-style bound (a find can walk an O(n) chain), but it means the canonical identifier of a class is always
    /// its minimum member, which the rest of the engine relies on (egglog's <c>UnionId</c> merge returns
    /// <c>min(old, new)</c> to agree with it), and it perturbs the larger, younger identifiers that fewer rows
    /// mention.
    /// </para>
    /// <para>
    /// Invariant: for every reserved identifier <c>i</c>, <c>parents[i] &lt;= i</c>, with equality exactly at roots.
    /// Union writes a smaller root into a larger one and halving replaces a parent by a grandparent, which is
    /// smaller still, so the invariant holds by construction. It is what makes every array access below in bounds
    /// once the starting identifier is.
    /// </para>
    /// <para>
    /// Threading: <see cref="FindNaive"/> and the read-only members never write and may run on many threads at once,
    /// as long as no thread calls a mutating member (<see cref="Find"/>, <see cref="Union"/>,
    /// <see cref="Reserve"/>, <see cref="Reset"/>) at the same time. This is egglog's <c>&amp;self</c> /
    /// <c>&amp;mut self</c> split.
    /// </para>
    /// </remarks>
    internal sealed class UnionFind
    {

        /// <summary>
        /// The parent of each identifier. Slots at <see cref="idCount"/> and beyond, up to the array's length, always
        /// hold their own index, so they read as roots exactly like identifiers that were never reserved.
        /// </summary>
        uint[] parents;

        /// <summary>
        /// One more than the highest identifier reserved: the length of egglog's <c>parents</c> vector.
        /// </summary>
        int idCount;

        /// <summary>
        /// Initializes an empty union-find in which every identifier is its own class. Constant time; allocates no
        /// slots until an identifier is reserved.
        /// </summary>
        public UnionFind()
        {
            parents = Array.Empty<uint>();
        }

        /// <summary>
        /// Initializes a copy of <paramref name="parents"/>, which holds exactly the reserved identifiers.
        /// </summary>
        /// <param name="parents">The parent array, owned by the new instance.</param>
        UnionFind(uint[] parents)
        {
            this.parents = parents;
            idCount = parents.Length;
        }

        /// <summary>
        /// Gets the largest identifier this union-find can reserve, bounded by the maximum length of a .NET array.
        /// Constant time.
        /// </summary>
        public static uint MaxId => (uint)Array.MaxLength - 1;

        /// <summary>
        /// Gets one more than the highest identifier reserved so far, explicitly or by <see cref="Find"/> and
        /// <see cref="Union"/>. Constant time.
        /// </summary>
        public int IdCount => idCount;

        /// <summary>
        /// Gets the parent of every reserved identifier, indexed by identifier. Roots are their own parents. Constant
        /// time; nothing is copied.
        /// </summary>
        /// <remarks>
        /// The view aliases internal storage: it is valid until the next mutating call, which may compress paths in
        /// place or replace the array when it grows. Bulk canonicalizers may walk it directly instead of calling
        /// <see cref="FindNaive"/> per value.
        /// </remarks>
        public ReadOnlySpan<uint> Parents => new(parents, 0, idCount);

        /// <summary>
        /// Makes every reserved identifier its own parent again, keeping <see cref="IdCount"/> and the allocated
        /// storage. Linear in <see cref="IdCount"/>.
        /// </summary>
        public void Reset()
        {
            IdentityFill.Fill(parents.AsSpan(0, idCount), 0);
        }

        /// <summary>
        /// Reserves space for <paramref name="id"/>, making every identifier up to it a member of the union-find.
        /// Amortized constant time per identifier added.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> exceeds <see cref="MaxId"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reserve(uint id)
        {
            if (id >= (uint)idCount)
                Grow(id);
        }

        /// <summary>
        /// Merges the classes of <paramref name="a"/> and <paramref name="b"/>, linking the larger root under the
        /// smaller one.
        /// </summary>
        /// <remarks>
        /// Reserves both identifiers, then finds each root with path halving, <paramref name="a"/> first, exactly as
        /// egglog does. Costs two finds and one store.
        /// </remarks>
        /// <param name="a">An identifier.</param>
        /// <param name="b">Another identifier.</param>
        /// <returns>
        /// The new root and the root it displaced. If the two were already in one class, its root, twice.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">An identifier exceeds <see cref="MaxId"/>.</exception>
        public (uint Parent, uint Child) Union(uint a, uint b)
        {
            Reserve(Math.Max(a, b));
            var p = parents;
            a = Halve(p, a);
            b = Halve(p, b);
            if (a == b)
                return (a, a);

            var parent = Math.Min(a, b);
            var child = Math.Max(a, b);
            p[child] = parent;
            return (parent, child);
        }

        /// <summary>
        /// Finds the root of <paramref name="id"/>'s class, reserving <paramref name="id"/> and halving the path it
        /// walks: each visited identifier is pointed at its grandparent. Amortized cheap; O(path length) worst case.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <returns>The canonical identifier of its class, the minimum member.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> exceeds <see cref="MaxId"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Find(uint id)
        {
            Reserve(id);
            return Halve(parents, id);
        }

        /// <summary>
        /// Finds the root of <paramref name="id"/>'s class without writing anything: no path compression and no
        /// reservation. An identifier that was never reserved is its own root. O(path length).
        /// </summary>
        /// <remarks>
        /// This is the read path the engine uses while rules run, from many threads at once.
        /// </remarks>
        /// <param name="id">The identifier.</param>
        /// <returns>The canonical identifier of its class, the minimum member.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint FindNaive(uint id)
        {
            var p = parents;

            // egglog tests against the reserved length; slots between it and the array length are identity, so
            // testing against the array length gives the same answer and doubles as the bounds check.
            if (id >= (uint)p.Length)
                return id;

            // In bounds: id < p.Length, and every parent is at most its index (see the class remarks).
            ref var p0 = ref MemoryMarshal.GetArrayDataReference(p);
            uint parent;
            while ((parent = Unsafe.Add(ref p0, (nuint)id)) != id)
                id = parent;

            return id;
        }

        /// <summary>
        /// Creates an independent deep copy holding the same partition and the same parent links.
        /// Linear in <see cref="IdCount"/>.
        /// </summary>
        /// <returns>The copy.</returns>
        public UnionFind Clone()
        {
            return new UnionFind(idCount == 0 ? Array.Empty<uint>() : parents.AsSpan(0, idCount).ToArray());
        }

        /// <summary>
        /// Walks from <paramref name="id"/> to its root, pointing each visited identifier at its grandparent
        /// (egglog's <c>find</c> loop, <c>union-find/src/lib.rs:71-84</c>).
        /// </summary>
        /// <param name="parents">The parent array.</param>
        /// <param name="id">A reserved identifier.</param>
        /// <returns>The root.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Halve(uint[] parents, uint id)
        {
            Debug.Assert(id < (uint)parents.Length);

            // In bounds: id is reserved, and every parent is at most its index (see the class remarks).
            ref var p0 = ref MemoryMarshal.GetArrayDataReference(parents);
            while (true)
            {
                var parent = Unsafe.Add(ref p0, (nuint)id);
                if (parent == id)
                    return id;

                var grand = Unsafe.Add(ref p0, (nuint)parent);
                Unsafe.Add(ref p0, (nuint)id) = grand;
                id = grand;
            }
        }

        /// <summary>
        /// Reserves identifiers up to <paramref name="id"/>, growing the array geometrically and filling new slots
        /// with their own index.
        /// </summary>
        /// <param name="id">An identifier at or beyond <see cref="IdCount"/>.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow(uint id)
        {
            if (id > MaxId)
                throw new ArgumentOutOfRangeException(nameof(id), id, $"Union-find identifiers cannot exceed {MaxId}.");

            var needed = (int)id + 1;
            if (needed > parents.Length)
            {
                var capacity = (int)Math.Min(Array.MaxLength, Math.Max(needed, Math.Max(16L, parents.Length * 2L)));
                var grown = GC.AllocateUninitializedArray<uint>(capacity);
                parents.AsSpan(0, idCount).CopyTo(grown);
                IdentityFill.Fill(grown.AsSpan(idCount), (uint)idCount);
                parents = grown;
            }

            idCount = needed;
        }

    }

}
