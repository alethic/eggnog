using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Gets the context's union-find table: the root's <see cref="DisplacedTable"/>, or a child's
        /// <see cref="OverlayDisplacedTable"/>, at the store's union-find id of the context's database.
        /// </summary>
        public Table UnionFindTable => overlayTable is { } table ? table : store.RootUnionFind;

        /// <summary>
        /// Gets the context's find: the root's union-find, or the parent's find followed by the child's overlay. Never
        /// writes; exact whenever the context is current with its parent (<see cref="IsUnionFindCurrent"/>).
        /// </summary>
        public LayeredUnionFind UnionFind => finder;

        /// <summary>
        /// Gets the number of ids displaced in the context's union-find table: the root's union rows, or the rows of a
        /// child's journal. Only grows.
        /// </summary>
        public int DisplacedCount => journal is { } j ? j.Count : store.RootUnionFind.UnionFind.Count;

        /// <summary>
        /// Gets the number of ids that are not canonical in the context's view: the root's displaced count, or, in a
        /// child, the ids displaced in its parent's view when it was created plus its journal, which holds exactly the
        /// others Exact when the context is current with its parent (<see cref="IsUnionFindCurrent"/>). O(1).
        /// </summary>
        internal long DisplacedInViewCount => displacedInParentView + DisplacedCount;

        /// <summary>
        /// Gets the ids displaced in the context's union-find table, in row order: the root's union children, or a
        /// child's journal. What a child of this context catches up with. Valid until the next union.
        /// </summary>
        public ReadOnlySpan<uint> DisplacedIds => journal is { } j ? j.Ids : store.RootUnionFind.UnionFind.Children;

        /// <summary>
        /// Gets whether a child has re-keyed its union-find over every displacement its parent has made: its cursor
        /// into the parent's displaced ids is at their end. Always true at the root.
        /// </summary>
        public bool IsUnionFindCurrent => parent is null || unionFindCursor == parent.DisplacedCount;

        /// <summary>
        /// Gets the find this context gives the children it creates: its own chain until it freezes, then the chain
        /// flattened into one map above the root's find. Children created before a freeze keep the explicit chain.
        /// </summary>
        internal LayeredUnionFind FinderForChildren => Volatile.Read(ref finderForChildren);

        /// <summary>
        /// Gets the child's overlay; <c>null</c> at the root.
        /// </summary>
        internal UnionFindOverlay? Overlay => overlay;

        /// <summary>
        /// Gets the child's displaced journal; <c>null</c> at the root.
        /// </summary>
        internal DisplacedJournal? Journal => journal;

        /// <summary>
        /// Gets the counts of what the context's union-find did. At the root,
        /// <see cref="UnionFindStatistics.OwnUnions"/> is its displaced count and the rest are zero.
        /// </summary>
        public UnionFindStatistics UnionFindStatistics => overlayTable is { } table
            ? new UnionFindStatistics(table.OwnUnions, Interlocked.Read(ref importedDisplacements), Interlocked.Read(ref rekeyedMembers), table.ParentIdLosers)
            : new UnionFindStatistics(store.RootUnionFind.UnionFind.Count, 0, 0, 0);

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/> in this context: the minimum of its class.
        /// </summary>
        /// <remarks>
        /// Requires the context to be clean (<see cref="IsClean"/>); a context that is not frozen is entered (its thread
        /// domain), so a find from a second thread while the domain is busy is refused rather than racing a merge. Cost:
        /// the root's find plus one probe per non-empty overlay level, with one hop at a frozen root.
        /// </remarks>
        /// <param name="id">Any id.</param>
        /// <returns>The canonical id.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted, contradicted or not clean, or its domain is
        /// held by another thread.</exception>
        public uint Find(uint id)
        {
            if (frozen)
            {
                ThrowIfDisposed();
                return finder.Find(id);
            }

            using var scope = Domain.Enter();
            ThrowIfNotReadable();
            Debug.Assert(IsUnionFindCurrent);
            return finder.Find(id);
        }

        /// <summary>
        /// Gets whether <paramref name="a"/> and <paramref name="b"/> name one class in this context. Requires the
        /// context to be clean, as <see cref="Find"/> does.
        /// </summary>
        /// <param name="a">An id.</param>
        /// <param name="b">Another id.</param>
        /// <returns><c>true</c> if they are equal here.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted, contradicted or not clean, or its domain is
        /// held by another thread.</exception>
        public bool AreEqual(uint a, uint b)
        {
            if (frozen)
            {
                ThrowIfDisposed();
                return finder.Find(a) == finder.Find(b);
            }

            using var scope = Domain.Enter();
            ThrowIfNotReadable();
            return finder.Find(a) == finder.Find(b);
        }

        /// <summary>
        /// Mints a fresh id: a class with nothing known about it. A child mints from its id blocks, so the id is local
        /// to it (<see cref="IsLocalId"/>); the root mints directly from the store's id counter. Thread-safe.
        /// </summary>
        /// <returns>The id.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen, faulted or contradicted.</exception>
        /// <exception cref="OverflowException">The store has run out of ids: the next one would exceed
        /// <see cref="Relations.UnionFind.MaxId"/>.</exception>
        public uint NewId()
        {
            ThrowIfNotWritable();
            if (idBlocks is { } blocks)
                return blocks.Next();

            var raw = store.Counters.Increment(store.IdCounter);
            if ((ulong)raw > Relations.UnionFind.MaxId)
                throw new OverflowException($"The store has run out of ids: {raw} exceeds the largest id a union-find can hold, {Relations.UnionFind.MaxId}.");

            return (uint)raw;
        }

        /// <summary>
        /// Gets whether <paramref name="id"/> was minted in this context: it lies in one of the context's id blocks.
        /// Exact for every id the context minted, since every term mint in a child goes through its blocks; container
        /// ids are minted on the shared counter and are never local. Lock-free, O(log blocks).
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if the id is local to this context.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is the root, which has no local ids.</exception>
        public bool IsLocalId(uint id)
        {
            ThrowIfDisposed();
            if (idBlocks is not { } blocks)
                throw new InvalidOperationException("The root context has no local ids: every id it mints is visible to all contexts.");

            return blocks.Contains(id);
        }

        /// <summary>
        /// Merges the classes of <paramref name="a"/> and <paramref name="b"/> in this context and its descendants: one
        /// <see cref="Write{TResult}"/> that stages <c>[a, b, epoch]</c> into the union-find table. Rows that mention
        /// the loser are brought up to date by the next <see cref="Rebuild"/>, which the context needs before it is
        /// read again.
        /// </summary>
        /// <param name="a">An id.</param>
        /// <param name="b">Another id.</param>
        /// <returns><c>true</c> if the classes were distinct: the displaced count grew.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen, faulted or contradicted; its domain is
        /// held by another thread; or the call comes from inside a write batch's action or a <c>ScanMerges</c> visitor
        /// of the same domain.</exception>
        public bool Union(uint a, uint b)
        {
            var before = Write((Context: this, A: a, B: b), static (state, args) =>
            {
                var count = args.Context.DisplacedCount;
                Span<uint> row = stackalloc uint[3];
                row[0] = args.A;
                row[1] = args.B;
                row[2] = state.Timestamp;
                state.StageInsert(args.Context.store.UnionFindTable, row);
                return count;
            });

            return DisplacedCount > before;
        }

        /// <summary>
        /// Gets whether <paramref name="id"/>, a displaced id of this child, was displaced by the child itself rather
        /// than inherited from its parent: it is canonical in the parent. Computed at use time, which is always
        /// correct: if the parent later displaces an own loser, that loser's ancestor rows are invisible at the parent
        /// level, so treating it as inherited is right.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> at a child when the id is canonical in the parent; <c>false</c> at the root.</returns>
        internal bool IsOwnDisplacement(uint id) => parentFinder is { } p && p.Find(id) == id;

        /// <summary>
        /// Visits the merges this child made over its parent's ids: for every class of its overlay, each member that is
        /// not local to the child, paired with the class's canonical id; a class's canonical is visited paired with
        /// itself when it is a parent id. Deterministic: slot order, then insertion order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Requires the context to be clean. The visitor may read any context (<see cref="Find"/> included). It runs as
        /// a callback of the context's thread domain: a write to any context of the domain (<see cref="Union"/>,
        /// <see cref="Write{TResult}"/>) and a rebuild that would run a pass are refused before they stage anything, so
        /// the scanned context stays clean and its classes stay as they are; <see cref="CreateChild"/>,
        /// <see cref="Freeze"/> and <see cref="Dispose"/> are refused too. A frozen context is scanned without its
        /// domain, and nothing can change it; disposing it while a scan of it runs (from the visitor or from another
        /// thread) is refused.
        /// </para>
        /// <para>Cost: O(overlay members), plus two interlocked operations per call.</para>
        /// </remarks>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="visitor">The visitor.</param>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is the root, is faulted, contradicted or not clean, or
        /// its domain is held by another thread.</exception>
        internal void ScanMerges<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IMergeVisitor
        {
            ThrowIfDisposed();
            if (overlay is not { } o)
                throw new InvalidOperationException("The root context merged nothing over a parent: it has none.");

            // A context that is not frozen cannot freeze while this thread holds its domain, so the test is stable.
            var current = frozen ? null : Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            if (Interlocked.Increment(ref activeScans) <= 0)
            {
                Interlocked.Decrement(ref activeScans);
                ThrowDisposed();
            }

            current?.EnterCallback();
            try
            {
                VisitMerges(o, ref visitor);
            }
            finally
            {
                current?.ExitCallback();
                Interlocked.Decrement(ref activeScans);
            }
        }

        /// <summary>
        /// The loop of <see cref="ScanMerges{TVisitor}"/>: every member of every merged class that is not local, with its
        /// class's canonical, in slot then insertion order.
        /// </summary>
        void VisitMerges<TVisitor>(UnionFindOverlay o, ref TVisitor visitor)
            where TVisitor : struct, IMergeVisitor
        {
            var blocks = idBlocks!;

            // Defence in depth: writes are refused inside the visitor, so the overlay cannot change under the scan.
            var version = o.Version;
            for (var slot = 0; slot < o.SlotCount; slot++)
            {
                if (o.CountOf(slot) < 2)
                    continue;

                var canonical = o.CanonicalOf(slot);
                var members = o.MembersOf(slot);
                for (var i = 0; i < members.Length; i++)
                {
                    var member = members[i];
                    if (blocks.Contains(member))
                        continue;

                    visitor.Visit(member, canonical);
                    if (o.Version != version)
                        ThrowChangedDuringScan();
                }
            }
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> when the calling thread runs a caller's code inside an
        /// operation of <paramref name="domain"/> (a write batch's action, a <see cref="ScanMerges{TVisitor}"/>
        /// visitor): the operation named by <paramref name="operation"/> would change a context the running operation
        /// depends on. Nothing has been staged or changed when it throws, and no context is faulted.
        /// </summary>
        /// <param name="domain">The domain the calling thread holds.</param>
        /// <param name="operation">The refused operation's name.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ThrowIfInCallback(ThreadAffinityGuard domain, string operation)
        {
            if (domain.InCallback)
                ThrowInCallback(operation);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowInCallback(string operation)
        {
            throw new InvalidOperationException($"{operation} cannot change a context from inside a write batch's action or a ScanMerges visitor of the same thread domain; reads are allowed there.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowChangedDuringScan()
        {
            throw new InvalidOperationException("The context's union-find changed during ScanMerges; a visitor must not write the context.");
        }

    }

}
