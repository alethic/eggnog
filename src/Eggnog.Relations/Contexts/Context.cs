using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// One view of a <see cref="Store"/>: its parent's view plus what is asserted in it (colored-contexts spec §1,
    /// §3.3). The root context is egglog's database; a child context is a copy of its parent that stores only what
    /// differs, and costs in proportion to what it touches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each context owns one <see cref="Relations.Database"/> (X1). The root's is egglog's, unchanged. A child's has
    /// its own catalog, notification list, pending queues and size estimate, and shares the store's counters, external
    /// functions, dependency graph and container environments with the root by reference (§3.4).
    /// </para>
    /// <para>
    /// Lifecycle (§8). <see cref="CreateChild"/> makes a child; children of a context that is not frozen share its
    /// thread domain (<see cref="Domain"/>, §8.3), so a non-frozen context and everything below it behave as one
    /// thread; a second thread entering the domain is refused. Inside an operation that runs a caller's code (a write
    /// batch's action, a <see cref="ScanMerges{TVisitor}"/> visitor), reads are allowed, but writes to a context of the
    /// same domain, rebuilds with work to do, <see cref="CreateChild"/>, <see cref="Freeze"/> and <see cref="Dispose"/>
    /// are refused before they change anything (§8.3). <see cref="Dispose"/> drops a child once it has no live children;
    /// the root is never disposed. Depth is limited to 31 (X36). Every ancestor's <see cref="Gate"/> counts its live
    /// descendants.
    /// </para>
    /// <para>
    /// Union-find (§5, M9.2). The root's union-find is egglog's <see cref="DisplacedTable"/>; a child's is an
    /// <see cref="OverlayDisplacedTable"/>: a <see cref="UnionFindOverlay"/> of the classes it merged over its parent's
    /// canonical ids, and a <see cref="DisplacedJournal"/> of the ids that stopped being canonical in it.
    /// <c>find_C = overlay_C ∘ find_P</c> (<see cref="UnionFind"/>). When the parent's union-find grows, the child
    /// re-keys its overlay eagerly at its next write or rebuild (<see cref="CatchUpUnionFind"/>), through one cursor into
    /// the parent's displaced ids.
    /// </para>
    /// <para>
    /// Interim (M9.2, §17): writes reach only the union-find in a child (its other tables have no layers before M9.3);
    /// <see cref="Rebuild"/> runs the union-find catch-up and the merge in a child, and egglog's rebuild pass at the root;
    /// <see cref="Freeze"/> freezes the union-find, flattens it for the children and reassigns their domains. Row layers,
    /// row catch-up, settling and the storage half of freezing arrive in M9.3–M9.4.
    /// </para>
    /// </remarks>
    internal sealed partial class Context : IDisposable
    {

        /// <summary>
        /// The deepest context: <c>LayeredSubset</c> keeps a 32-bit layer mask, one bit per level (X36).
        /// </summary>
        public const int MaxDepth = 31;

        /// <summary>
        /// The value <see cref="activeScans"/> takes when <see cref="Dispose"/> claims the context: any scan that starts
        /// afterwards sees a negative count and refuses to run.
        /// </summary>
        const int ScansClosed = int.MinValue;

        readonly Store store;
        readonly Context? parent;
        readonly Context[] ancestors;
        readonly ContextOptions options;
        readonly Database database;
        readonly DescendantGate gate = new();
        readonly List<Context> children = new();
        readonly IdBlockAllocator? idBlocks;
        readonly uint createdAt;

        /// <summary>
        /// The context's find: the root's union-find, or the parent's find extended with <see cref="overlay"/>.
        /// </summary>
        readonly LayeredUnionFind finder;

        /// <summary>
        /// The parent's find as it was handed to this child at creation; <c>null</c> at the root.
        /// </summary>
        readonly LayeredUnionFind? parentFinder;

        /// <summary>
        /// The child's union-find table, overlay and journal; <c>null</c> at the root.
        /// </summary>
        readonly OverlayDisplacedTable? overlayTable;

        readonly UnionFindOverlay? overlay;
        readonly DisplacedJournal? journal;

        /// <summary>
        /// The parent's displaced count when the child was created: the first parent displacement it catches up with.
        /// </summary>
        readonly int parentDisplacedAtCreation;

        /// <summary>
        /// The find this context gives the children it creates: its own chain until it freezes, then its flattened
        /// form (§5.4, §8.1). Written before the frozen flag, read after it.
        /// </summary>
        LayeredUnionFind finderForChildren;

        /// <summary>
        /// The frozen chain flattened at freeze, for the invariant checker; <c>null</c> otherwise.
        /// </summary>
        FlattenedUnionFind? flattened;

        /// <summary>
        /// How far into the parent's displaced ids the child has caught up (X7); a child is uf-current when this equals
        /// the parent's <see cref="DisplacedCount"/>.
        /// </summary>
        int unionFindCursor;

        /// <summary>
        /// The displaced count at the end of the last rebuild: rows past it are the next pass's delta, and a context is
        /// clean only when nothing lies past it (§7.5).
        /// </summary>
        int journalMark;

        long importedDisplacements;
        long rekeyedMembers;

        /// <summary>
        /// The thread domain; reassigned (on the domain's own thread) when an ancestor freezes, so read volatile.
        /// </summary>
        ThreadAffinityGuard domain;

        /// <summary>
        /// The number of <see cref="ScanMerges{TVisitor}"/> calls running on the context, or <see cref="ScansClosed"/>
        /// once <see cref="Dispose"/> has claimed it. A frozen context is scanned without its domain, so this, not the
        /// domain, is what refuses a dispose from inside a visitor (§8.3) or during another thread's scan.
        /// </summary>
        int activeScans;

        uint semiNaiveFloor;
        volatile bool frozen;
        volatile bool disposed;
        volatile bool faulted;
        volatile bool contradicted;
        object? host;

        /// <summary>
        /// Initializes the root context of <paramref name="store"/> over <paramref name="database"/>, the store's
        /// egglog database, and attaches the database to it.
        /// </summary>
        /// <param name="store">The store.</param>
        /// <param name="database">The root database, whose union-find and counters the store has laid out.</param>
        internal Context(Store store, Database database)
        {
            this.store = store;
            this.database = database;
            parent = null;
            ancestors = [this];
            options = ContextOptions.Default;
            domain = new ThreadAffinityGuard();
            createdAt = 0;
            finder = new LayeredUnionFind(store.RootUnionFind.UnionFind);
            finderForChildren = finder;
            journalMark = store.RootUnionFind.UnionFind.Count;

            // Set later by Freeze and by the contradiction stop (M9.3).
            frozen = false;
            contradicted = false;
            database.AttachRoot(this, store.ClockCounter);
        }

        /// <summary>
        /// Initializes a child of <paramref name="parent"/>. Called by <see cref="CreateChild"/>, which holds the
        /// parent's child list and, when the parent is not frozen, its domain.
        /// </summary>
        /// <param name="parent">The parent.</param>
        /// <param name="options">The child's options.</param>
        Context(Context parent, ContextOptions options)
        {
            store = parent.store;
            this.parent = parent;
            this.options = options;
            ancestors = new Context[parent.ancestors.Length + 1];
            Array.Copy(parent.ancestors, ancestors, parent.ancestors.Length);
            ancestors[parent.ancestors.Length] = this;

            // §10.9: a child of a frozen context runs serially (siblings may run concurrently); a child of a context
            // that is not frozen never runs concurrently with it, so it inherits its heuristics.
            var parallelism = options.Parallelism ?? (parent.IsFrozen ? ParallelHeuristics.Serial : parent.database.Parallelism);
            idBlocks = new IdBlockAllocator(store.Counters, store.IdCounter);

            // §5: the parent is current with its own parent here (CreateChild rebuilt it, or it is frozen), so its find
            // is exact and the child starts with an empty overlay and journal over it.
            parentFinder = parent.FinderForChildren;
            overlay = new UnionFindOverlay();
            journal = new DisplacedJournal();
            overlayTable = new OverlayDisplacedTable(parentFinder, overlay, journal, parent.UnionFindTable, idBlocks);
            finder = overlayTable.Finder;
            finderForChildren = finder;
            unionFindCursor = parent.DisplacedCount;
            parentDisplacedAtCreation = unionFindCursor;
            database = new Database(store.Root.Database, this, overlayTable, idBlocks, parallelism);
            domain = parent.IsFrozen ? new ThreadAffinityGuard() : parent.Domain;

            // Read after every cursor is initialized (the union-find cursor above; M9.3-M9.4 add the row cursors before
            // this point), so anything the parent wrote at an earlier epoch is already part of the child's starting view.
            createdAt = store.ReadClock();
            semiNaiveFloor = createdAt;
        }

        /// <summary>
        /// Gets the store.
        /// </summary>
        public Store Store => store;

        /// <summary>
        /// Gets the parent context, or <c>null</c> for the root.
        /// </summary>
        public Context? Parent => parent;

        /// <summary>
        /// Gets the context's depth: 0 for the root.
        /// </summary>
        public int Depth => ancestors.Length - 1;

        /// <summary>
        /// Gets the chain from the root to this context, indexed by depth: <c>Ancestors[Depth]</c> is this context.
        /// </summary>
        public ReadOnlySpan<Context> Ancestors => ancestors;

        /// <summary>
        /// Gets the context's options.
        /// </summary>
        public ContextOptions Options => options;

        /// <summary>
        /// Gets the context's database.
        /// </summary>
        public Database Database => database;

        /// <summary>
        /// Gets the gate counting the context's live descendants (§6.8).
        /// </summary>
        public DescendantGate Gate => gate;

        /// <summary>
        /// Gets the context's thread domain (§8.3): shared with its parent unless the parent was frozen when this
        /// context was created (or froze since).
        /// </summary>
        public ThreadAffinityGuard Domain => Volatile.Read(ref domain);

        /// <summary>
        /// Gets the clock reading taken when the context was created; 0 for the root. Every row this context or a
        /// descendant writes carries a timestamp at least this large (I19).
        /// </summary>
        public uint CreatedAt => createdAt;

        /// <summary>
        /// Gets the context's change stamp (rule-layer D16, X30): it moves when a merge adds or removes rows or a clear
        /// empties a table, and not for no-ops or redundant unions.
        /// </summary>
        public long Version => database.ChangeStamp;

        /// <summary>
        /// Gets whether this is the root context.
        /// </summary>
        public bool IsRoot => parent is null;

        /// <summary>
        /// Gets whether the context is frozen, and so read-only and shareable across threads.
        /// </summary>
        public bool IsFrozen => frozen;

        /// <summary>
        /// Gets whether the context has been disposed.
        /// </summary>
        public bool IsDisposed => disposed;

        /// <summary>
        /// Gets whether an exception escaped one of the child's operations (X12); only <see cref="Dispose"/> is then
        /// allowed.
        /// </summary>
        public bool IsFaulted => faulted;

        /// <summary>
        /// Gets whether a contradiction stopped the context (§6.9; only with
        /// <see cref="ContextOptions.StopOnContradiction"/>).
        /// </summary>
        public bool IsContradicted => contradicted;

        /// <summary>
        /// Gets whether the context may be read (§7.5): not disposed or faulted, and either frozen or caught up with
        /// everything — nothing merged since its last rebuild is left unrebuilt, and, in a child, no write is staged and
        /// unmerged, its union-find is current with its parent's, and its parent is clean too. The recursion stops at the
        /// first frozen ancestor, so the cost is O(ancestors that are not frozen) (D16).
        /// </summary>
        /// <remarks>
        /// At the root, writes staged and not merged do not make it unclean: they are invisible until merged, as in egglog
        /// (X42). Interim (M9.2): a child's row term (<c>!Tables.NeedsCatchUp</c>) arrives with its layers (M9.3-M9.4).
        /// </remarks>
        public bool IsClean
        {
            get
            {
                if (disposed || faulted)
                    return false;

                if (frozen)
                    return true;

                if (parent is null)
                    return journalMark == DisplacedCount;

                return journalMark == journal!.Count
                    && database.Catalog.Notifications.IsEmpty
                    && unionFindCursor == parent.DisplacedCount
                    && parent.IsClean;
            }
        }

        /// <summary>
        /// Gets or sets the object that wraps this context in the bridge (its <c>EGraphContext</c>). Set once.
        /// </summary>
        /// <exception cref="InvalidOperationException">A different host is set already.</exception>
        public object? Host
        {
            get => Volatile.Read(ref host);
            set
            {
                if (Interlocked.CompareExchange(ref host, value, null) is { } existing && ReferenceEquals(existing, value) == false)
                    throw new InvalidOperationException("The context already has a host.");
            }
        }

        /// <summary>
        /// Gets the context's id blocks, or <c>null</c> at the root.
        /// </summary>
        internal IdBlockAllocator? IdBlocks => idBlocks;

        /// <summary>
        /// Throws <see cref="ObjectDisposedException"/> once the context is disposed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ThrowIfDisposed()
        {
            if (disposed)
                ThrowDisposed();
        }

        /// <summary>
        /// Throws unless the context may be written: not disposed, frozen, faulted or contradicted.
        /// </summary>
        internal void ThrowIfNotWritable()
        {
            ThrowIfDisposed();
            if (frozen)
                throw new InvalidOperationException("A frozen context cannot be written.");

            ThrowIfFaulted();
            ThrowIfContradicted();
        }

        /// <summary>
        /// Throws unless the context may be read (§7.5): disposed → <see cref="ObjectDisposedException"/>; faulted or
        /// contradicted → <see cref="InvalidOperationException"/>; not clean → <see cref="InvalidOperationException"/>
        /// naming <see cref="Rebuild"/>.
        /// </summary>
        internal void ThrowIfNotReadable()
        {
            ThrowIfDisposed();
            ThrowIfFaulted();
            ThrowIfContradicted();
            if (IsClean == false)
                ThrowNotClean();
        }

        /// <summary>
        /// Marks a child faulted: an exception escaped one of its operations and may have left a hide without its copy
        /// (X12), so every later call except <see cref="Dispose"/> throws. The root keeps <see cref="Relations.Database"/>'s
        /// own exception guarantee instead and is never faulted.
        /// </summary>
        internal void Fault()
        {
            if (IsRoot == false)
                faulted = true;
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> once the context is faulted (X12).
        /// </summary>
        internal void ThrowIfFaulted()
        {
            if (faulted)
                throw new InvalidOperationException("An operation of this context failed and left it inconsistent; it can only be disposed.");
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> once a contradiction stopped the context (§6.9).
        /// </summary>
        internal void ThrowIfContradicted()
        {
            if (contradicted)
                throw new InvalidOperationException("The context is contradicted; only IsContradictory may be read.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNotClean()
        {
            throw new InvalidOperationException("The context has pending changes, or an ancestor has changed since it was rebuilt; call Rebuild() first.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowDisposed()
        {
            throw new ObjectDisposedException(nameof(Context));
        }

    }

}
