using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A thread-safe union-find over <see cref="uint"/> identifiers supporting concurrent finds and unions with
    /// path compression, and growth on demand when new identifiers appear. Port of egglog's
    /// <c>union_find::concurrent::UnionFind</c> (<c>union-find/src/concurrent</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The algorithm follows egglog, which follows Jayanti and Tarjan's "Concurrent Disjoint Set Union" with
    /// simplifications: link by minimum identifier (a compare-and-swap of a root's slot from itself to the smaller
    /// root), one-try path splitting in finds (a compare-and-swap of each visited slot from its parent to its
    /// grandparent, failures ignored), and a linearizable <see cref="SameSet"/>. Every slot only ever decreases, and
    /// a non-root's parent is always smaller than it, so finds terminate and the canonical identifier of a class is
    /// its minimum member, as in <see cref="UnionFind"/>.
    /// </para>
    /// <para>
    /// Storage differs from egglog. egglog keeps one flat vector behind a read-optimized lock and blocks every
    /// accessor while it reallocates. Here the slots live in power-of-two segments that never move once published:
    /// segment 0 holds identifiers [0, 32) and segment k &gt; 0 holds [32 * 2^(k-1), 32 * 2^k). The directory of
    /// segments has a fixed length, so growth only installs missing segments (under a lock that operations take only
    /// to grow) and an operation reads the directory with no synchronization beyond an acquire load. Capacity still
    /// grows to the next power of two of the identifier requested, egglog's policy (<c>concurrent/buffer.rs:76-85</c>),
    /// with egglog's default of 32. Reading a slot costs a few ALU operations and one extra dependent load over a flat
    /// array, in exchange for wait-free access during growth.
    /// </para>
    /// <para>
    /// <see cref="Reset"/> keeps egglog's contract (<c>concurrent/mod.rs:52-57</c>, <c>concurrent/buffer.rs:91-98</c>):
    /// it waits for the operations in flight, holds off new ones, and re-initializes the slots in place, allocating
    /// nothing. egglog gets the exclusion from its lock's read guard; here every operation brackets its slot accesses
    /// with two plain stores to its thread's counter and one read of a reset flag (see
    /// <see cref="ThreadOperationCounters"/>), with no interlocked instruction. Growth needs no such exclusion, so an
    /// operation grows before it enters the bracket, and nothing inside the bracket can throw or block.
    /// </para>
    /// <para>
    /// <see cref="Find"/> may return different roots for two members of one class if a union lands between the two
    /// calls; use <see cref="SameSet"/> for a linearizable answer. With no concurrent unions, <see cref="Find"/> is
    /// exact.
    /// </para>
    /// <para>
    /// egglog's <c>Clone</c> is shallow (copies share state); a .NET reference already behaves that way. Use
    /// <see cref="DeepCopy"/> for an independent copy.
    /// </para>
    /// </remarks>
    internal sealed class ConcurrentUnionFind
    {

        /// <summary>
        /// The base-2 logarithm of the size of segment 0.
        /// </summary>
        const int BaseShift = 5;

        /// <summary>
        /// The size of segment 0 and of the default capacity: egglog's default of 32 (<c>concurrent/uf.rs:32-36</c>).
        /// </summary>
        const int BaseSize = 1 << BaseShift;

        /// <summary>
        /// The number of directory entries: enough segments to cover identifiers [0, 2^31).
        /// </summary>
        const int SegmentCount = 31 - BaseShift + 1;

        /// <summary>
        /// The largest identifier this union-find can hold, bounded by the size of the last segment a .NET array
        /// can hold.
        /// </summary>
        public const uint MaxId = int.MaxValue;

        /// <summary>
        /// Serializes growth, and growth with the fill of a <see cref="Reset"/>. Finds and unions take it only to
        /// grow, before they enter their bracket.
        /// </summary>
        readonly object growLock = new();

        /// <summary>
        /// Held for the whole of a <see cref="Reset"/>: serializes resets, and is what operations that arrive during
        /// one wait on.
        /// </summary>
        readonly object resetLock = new();

        /// <summary>
        /// The segment directory. Installed segments always form a prefix, are published with a release store and
        /// are never removed, replaced or moved.
        /// </summary>
        readonly uint[]?[] segments;

        /// <summary>
        /// <c>true</c> while a <see cref="Reset"/> drains operations and refills the slots; operations that see it
        /// back off until the reset is done.
        /// </summary>
        bool resetting;

        /// <summary>
        /// Initializes a union-find with egglog's default capacity of 32 identifiers. Constant time.
        /// </summary>
        public ConcurrentUnionFind() :
            this(BaseSize)
        {

        }

        /// <summary>
        /// Initializes a union-find with room for <paramref name="capacity"/> identifiers, each its own class. The
        /// capacity is rounded up to a power of two, at least 32. Linear in the rounded capacity.
        /// </summary>
        /// <param name="capacity">The number of identifiers to make room for.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
        public ConcurrentUnionFind(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);
            segments = new uint[]?[SegmentCount];
            if (capacity > 0)
                Grow(segments, SegmentOf((uint)capacity - 1));
        }

        /// <summary>
        /// Initializes a union-find over an already-filled directory.
        /// </summary>
        /// <param name="segments">The directory, owned by the new instance.</param>
        ConcurrentUnionFind(uint[]?[] segments)
        {
            this.segments = segments;
        }

        /// <summary>
        /// Gets the number of identifiers that currently have a slot: a power of two, or zero. Identifiers beyond it
        /// are roots and get a slot the first time an operation names them. Scans the directory: at most 27 acquire
        /// loads.
        /// </summary>
        public long Capacity
        {
            get
            {
                var k = InstalledSegments(segments);
                return k == 0 ? 0 : (long)BaseSize << (k - 1);
            }
        }

        /// <summary>
        /// Makes every identifier its own class again, keeping the capacity and allocating nothing. Waits for the
        /// operations in flight, and operations that start meanwhile wait for it. Linear in the capacity, plus a
        /// process-wide memory barrier.
        /// </summary>
        /// <remarks>
        /// egglog re-initializes the buffer in place under its lock's write side, which waits for every reader and
        /// holds off new ones (<c>concurrent/buffer.rs:91-98</c>). This does the same with the reset flag and
        /// <see cref="ThreadOperationCounters"/>. Every operation therefore linearizes entirely before or entirely
        /// after the reset.
        /// </remarks>
        public void Reset()
        {
            lock (resetLock)
            {
                // Published before the drain's barrier: an operation either is seen in flight or sees the flag.
                Volatile.Write(ref resetting, true);
                ThreadOperationCounters.WaitForOperations();

                // No operation is inside its bracket now, and none can enter until the flag drops. Growth still can
                // run, so the fill excludes it to see a stable set of segments.
                lock (growLock)
                {
                    var dir = segments;
                    for (var k = 0; k < SegmentCount; k++)
                    {
                        var segment = dir[k];
                        if (segment == null)
                            break;

                        IdentityFill.Fill(segment, SegmentStart(k));
                    }
                }

                // Release: the fill is visible to every operation that sees the flag down.
                Volatile.Write(ref resetting, false);
            }
        }

        /// <summary>
        /// Creates an independent copy: later unions on either do not affect the other. Linear in the capacity.
        /// </summary>
        /// <remarks>
        /// As in egglog (<c>concurrent/buffer.rs:24-39</c>), the copy runs as one operation: a <see cref="Reset"/> is
        /// wholly before or after it, but concurrent unions may or may not be reflected, slot by slot. The copy is a
        /// valid forest over the segments installed when the copy began.
        /// </remarks>
        /// <returns>The copy.</returns>
        public ConcurrentUnionFind DeepCopy()
        {
            var dir = segments;
            var installed = InstalledSegments(dir);
            var copy = new uint[]?[SegmentCount];
            for (var k = 0; k < installed; k++)
                copy[k] = GC.AllocateUninitializedArray<uint>(SegmentSize(k));

            // Allocation is done before entering, so nothing inside the bracket can throw.
            ref var counter = ref ThreadOperationCounters.Current();
            var ticket = Enter(ref counter);
            for (var k = 0; k < installed; k++)
            {
                // Slot by slot with acquire loads, as egglog copies (concurrent/buffer.rs:30-34): a bulk memory copy
                // does not promise that a slot being swapped concurrently is read whole.
                var source = dir[k]!;
                var target = copy[k]!;
                for (var i = 0; i < source.Length; i++)
                    target[i] = Volatile.Read(ref source[i]);
            }

            Exit(ref counter, ticket);
            return new ConcurrentUnionFind(copy);
        }

        /// <summary>
        /// Gets the canonical identifier of <paramref name="id"/>'s class, splitting the path it walks. O(path
        /// length): an acquire load per step and, past the first two steps, a compare-and-swap per step, plus the
        /// bracket (two plain stores to a thread-private cache line and one load of the reset flag).
        /// </summary>
        /// <remarks>
        /// Under concurrent unions the class's root may change right after this returns, so two finds can disagree
        /// for members of one class. <see cref="SameSet"/> gives the ground truth.
        /// </remarks>
        /// <param name="id">The identifier.</param>
        /// <returns>The root of its class.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> exceeds <see cref="MaxId"/>.</exception>
        public uint Find(uint id)
        {
            var dir = segments;
            ref var slot = ref EnsureSlot(dir, id);
            ref var counter = ref ThreadOperationCounters.Current();
            var ticket = Enter(ref counter);
            var root = FindImpl(dir, ref slot, id);
            Exit(ref counter, ticket);
            return root;
        }

        /// <summary>
        /// Gets whether <paramref name="l"/> and <paramref name="r"/> are in the same class. Linearizable. Two finds,
        /// repeated each time a concurrent union moves the left root, plus the bracket.
        /// </summary>
        /// <remarks>
        /// egglog's loop (<c>concurrent/uf.rs:75-94</c>): find both roots; while they differ, if the left root is
        /// still a root at that moment the classes were distinct, otherwise find both again.
        /// </remarks>
        /// <param name="l">An identifier.</param>
        /// <param name="r">Another identifier.</param>
        /// <returns><c>true</c> if they are equivalent.</returns>
        /// <exception cref="ArgumentOutOfRangeException">An identifier exceeds <see cref="MaxId"/>.</exception>
        public bool SameSet(uint l, uint r)
        {
            var dir = segments;
            EnsureCapacity(dir, Math.Max(l, r));
            ref var counter = ref ThreadOperationCounters.Current();
            var ticket = Enter(ref counter);
            var same = SameSetImpl(dir, l, r);
            Exit(ref counter, ticket);
            return same;
        }

        /// <summary>
        /// Merges the classes of <paramref name="l"/> and <paramref name="r"/>, linking the larger root under the
        /// smaller one. Lock-free: retries when another union changes one of the roots first. Two finds and one
        /// compare-and-swap per attempt, plus the bracket.
        /// </summary>
        /// <param name="l">An identifier.</param>
        /// <param name="r">Another identifier.</param>
        /// <returns>
        /// The new root and the root it displaced. If the two were already in one class, its root, twice.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">An identifier exceeds <see cref="MaxId"/>.</exception>
        public (uint Parent, uint Child) Union(uint l, uint r)
        {
            var dir = segments;
            EnsureCapacity(dir, Math.Max(l, r));
            ref var counter = ref ThreadOperationCounters.Current();
            var ticket = Enter(ref counter);
            var result = UnionImpl(dir, l, r);
            Exit(ref counter, ticket);
            return result;
        }

        /// <summary>
        /// Enters an operation's bracket: marks the calling thread as inside an operation, then waits out a reset in
        /// progress. Two plain stores and a load when no reset is running.
        /// </summary>
        /// <param name="counter">The calling thread's counter (<see cref="ThreadOperationCounters.Current"/>).</param>
        /// <returns>The counter's new, odd, value, to pass to <see cref="Exit"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint Enter(ref uint counter)
        {
            var ticket = counter + 1;
            Volatile.Write(ref counter, ticket);
            if (Volatile.Read(ref resetting))
                ticket = WaitForReset(ref counter, ticket);

            return ticket;
        }

        /// <summary>
        /// Leaves an operation's bracket with a release store, so every slot access of the operation happens before
        /// a reset that sees the counter move.
        /// </summary>
        /// <param name="counter">The calling thread's counter.</param>
        /// <param name="ticket">The value <see cref="Enter"/> returned.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Exit(ref uint counter, uint ticket) => Volatile.Write(ref counter, ticket + 1);

        /// <summary>
        /// Backs off from a reset in progress: leaves the bracket, waits for the reset to release its lock, and
        /// enters again, until no reset is running.
        /// </summary>
        /// <param name="counter">The calling thread's counter.</param>
        /// <param name="ticket">The counter's current, odd, value.</param>
        /// <returns>The counter's new, odd, value.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        uint WaitForReset(ref uint counter, uint ticket)
        {
            do
            {
                Volatile.Write(ref counter, ticket + 1);
                lock (resetLock)
                {
                    // A reset holds the lock from raising the flag to lowering it, so acquiring it waits it out.
                }

                ticket += 2;
                Volatile.Write(ref counter, ticket);
            }
            while (Volatile.Read(ref resetting));

            return ticket;
        }

        /// <summary>
        /// The loop of egglog's <c>same_set</c> (<c>concurrent/uf.rs:75-94</c>).
        /// </summary>
        /// <param name="dir">The directory, with slots for both identifiers.</param>
        /// <param name="l">An identifier.</param>
        /// <param name="r">Another identifier.</param>
        /// <returns><c>true</c> if they are equivalent.</returns>
        static bool SameSetImpl(uint[]?[] dir, uint l, uint r)
        {
            l = FindImpl(dir, l);
            r = FindImpl(dir, r);
            while (l != r)
            {
                if (Volatile.Read(ref Slot(dir, l)) == l)
                    return false;

                l = FindImpl(dir, l);
                r = FindImpl(dir, r);
            }

            return true;
        }

        /// <summary>
        /// The loop of egglog's <c>merge</c> (<c>concurrent/uf.rs:99-134</c>).
        /// </summary>
        /// <param name="dir">The directory, with slots for both identifiers.</param>
        /// <param name="l">An identifier.</param>
        /// <param name="r">Another identifier.</param>
        /// <returns>The new root and the root it displaced, or the shared root twice.</returns>
        static (uint Parent, uint Child) UnionImpl(uint[]?[] dir, uint l, uint r)
        {
            while (true)
            {
                l = FindImpl(dir, l);
                r = FindImpl(dir, r);
                if (l == r)
                    return (l, l);

                // Union by min: smaller ids are hypothesized to be better represented in the e-graph, so displacing
                // the larger one perturbs less during rebuilding (concurrent/uf.rs:116-120).
                var parent = Math.Min(l, r);
                var child = Math.Max(l, r);
                if (Interlocked.CompareExchange(ref Slot(dir, child), parent, child) == child)
                    return (parent, child);
            }
        }

        /// <summary>
        /// Walks from <paramref name="id"/> to its root with one-try path splitting.
        /// </summary>
        /// <param name="dir">The directory, with a slot for <paramref name="id"/>.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>A root of <paramref name="id"/>'s class at some point during the call.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint FindImpl(uint[]?[] dir, uint id) => FindImpl(dir, ref Slot(dir, id), id);

        /// <summary>
        /// Walks from <paramref name="id"/>, whose slot is <paramref name="slot"/>, to its root with one-try path
        /// splitting: each visited slot is swung from its parent to its grandparent with a compare-and-swap whose
        /// failure is ignored (egglog's <c>find_impl</c>, <c>concurrent/uf.rs:136-156</c>; egglog leaves the two-try
        /// variant commented out, and so does this). The first step is inlined at the call site so that roots and
        /// children of roots, the common cases, cost no call.
        /// </summary>
        /// <param name="dir">The directory, with a slot for <paramref name="id"/>.</param>
        /// <param name="slot">The slot of <paramref name="id"/>.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>A root of <paramref name="id"/>'s class at some point during the call.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint FindImpl(uint[]?[] dir, ref uint slot, uint id)
        {
            var next = Volatile.Read(ref slot);

            // A root. egglog loads this slot a second time as the "grandparent" and stops when it still matches; one
            // load already shows a root at an instant within the call, which is all a find promises.
            if (next == id)
                return id;

            ref var nextSlot = ref Slot(dir, next);
            var grand = Volatile.Read(ref nextSlot);
            if (next == grand)
                return next;

            return SplitPath(dir, ref slot, next, ref nextSlot, grand);
        }

        /// <summary>
        /// The loop of egglog's <c>find_impl</c>, entered with the slot of the current identifier, its parent
        /// <paramref name="next"/>, the parent's slot and the grandparent <paramref name="grand"/>, which differs from
        /// <paramref name="next"/>.
        /// </summary>
        /// <param name="dir">The directory.</param>
        /// <param name="curSlot">The slot of the current identifier.</param>
        /// <param name="next">Its parent, as loaded.</param>
        /// <param name="nextSlot">The parent's slot.</param>
        /// <param name="grand">The parent's parent, as loaded.</param>
        /// <returns>A root of the class at some point during the call.</returns>
        static uint SplitPath(uint[]?[] dir, ref uint curSlot, uint next, ref uint nextSlot, uint grand)
        {
            ref var cur = ref curSlot;
            ref var nxt = ref nextSlot;
            do
            {
                Interlocked.CompareExchange(ref cur, grand, next);
                cur = ref nxt;
                next = Volatile.Read(ref cur);
                nxt = ref Slot(dir, next);
                grand = Volatile.Read(ref nxt);
            }
            while (next != grand);

            return next;
        }

        /// <summary>
        /// Gets the slot of <paramref name="id"/>, whose segment must be installed in <paramref name="dir"/>.
        /// </summary>
        /// <param name="dir">The directory.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>A reference to the slot.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ref uint Slot(uint[]?[] dir, uint id)
        {
            // log is BaseShift - 1 for segment 0 and log2(id) beyond it. In bounds: id <= MaxId keeps the segment
            // below SegmentCount, its segment is installed (EnsureCapacity covered an id at least this large and
            // installs prefixes; every slot holds an id no larger than its own), and the offset is below the
            // segment's size by construction.
            var log = BitOperations.Log2(id | (BaseSize - 1));
            var segment = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(dir), log - (BaseShift - 1));
            var offset = id - ((1u << log) & ~(uint)(BaseSize - 1));
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(segment!), (nuint)offset);
        }

        /// <summary>
        /// Gets the index of the segment holding <paramref name="id"/>.
        /// </summary>
        /// <param name="id">An identifier no larger than <see cref="MaxId"/>.</param>
        /// <returns>The segment index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int SegmentOf(uint id) => BitOperations.Log2(id | (BaseSize - 1)) - (BaseShift - 1);

        /// <summary>
        /// Gets the first identifier of segment <paramref name="k"/>.
        /// </summary>
        /// <param name="k">The segment index.</param>
        /// <returns>The identifier its first slot holds.</returns>
        static uint SegmentStart(int k) => k == 0 ? 0u : (uint)BaseSize << (k - 1);

        /// <summary>
        /// Gets the number of slots of segment <paramref name="k"/>.
        /// </summary>
        /// <param name="k">The segment index.</param>
        /// <returns>The segment's length.</returns>
        static int SegmentSize(int k) => k == 0 ? BaseSize : BaseSize << (k - 1);

        /// <summary>
        /// Gets the number of installed segments, which form a prefix of <paramref name="dir"/>.
        /// </summary>
        /// <param name="dir">The directory.</param>
        /// <returns>The length of the installed prefix.</returns>
        static int InstalledSegments(uint[]?[] dir)
        {
            var k = 0;
            while (k < SegmentCount && Volatile.Read(ref dir[k]) != null)
                k++;

            return k;
        }

        /// <summary>
        /// Makes sure <paramref name="dir"/> has a slot for every identifier up to <paramref name="id"/>: egglog's
        /// <c>Buffer::with_access</c>, whose fast path here is one acquire load of a directory entry.
        /// </summary>
        /// <param name="dir">The directory.</param>
        /// <param name="id">The largest identifier the caller will touch.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureCapacity(uint[]?[] dir, uint id) => EnsureSlot(dir, id);

        /// <summary>
        /// Makes sure <paramref name="dir"/> has a slot for every identifier up to <paramref name="id"/>, and gets
        /// the slot of <paramref name="id"/>.
        /// </summary>
        /// <param name="dir">The directory.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>A reference to the slot.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref uint EnsureSlot(uint[]?[] dir, uint id)
        {
            if (id > MaxId)
                ThrowIdOutOfRange(id);

            // The same arithmetic as Slot, with the directory entry read with acquire semantics and grown if absent.
            var log = BitOperations.Log2(id | (BaseSize - 1));
            var k = log - (BaseShift - 1);
            var segment = Volatile.Read(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(dir), k)) ?? Grow(dir, k);
            var offset = id - ((1u << log) & ~(uint)(BaseSize - 1));
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(segment), (nuint)offset);
        }

        /// <summary>
        /// Installs every missing segment of <paramref name="dir"/> up to <paramref name="k"/>, in increasing order
        /// so installed segments stay a prefix.
        /// </summary>
        /// <param name="dir">The directory.</param>
        /// <param name="k">The highest segment needed.</param>
        /// <returns>Segment <paramref name="k"/>.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        uint[] Grow(uint[]?[] dir, int k)
        {
            lock (growLock)
            {
                for (var i = 0; i <= k; i++)
                    if (dir[i] == null)
                        Volatile.Write(ref dir[i], NewSegment(i));

                return dir[k]!;
            }
        }

        /// <summary>
        /// Allocates segment <paramref name="k"/> with every slot its own identifier.
        /// </summary>
        /// <param name="k">The segment index.</param>
        /// <returns>The segment.</returns>
        static uint[] NewSegment(int k)
        {
            var segment = GC.AllocateUninitializedArray<uint>(SegmentSize(k));
            IdentityFill.Fill(segment, SegmentStart(k));
            return segment;
        }

        /// <summary>
        /// Throws for an identifier beyond <see cref="MaxId"/>.
        /// </summary>
        /// <param name="id">The identifier.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowIdOutOfRange(uint id)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"Concurrent union-find identifiers cannot exceed {MaxId}.");
        }

    }

}
