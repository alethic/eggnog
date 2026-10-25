using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// One view of a <see cref="Store"/>: its parent's view plus what is asserted in it. The root context is egglog's
    /// database; a child context is a copy of its parent that stores only what differs, and costs in proportion to what
    /// it touches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each context owns one <see cref="Relations.Database"/>. The root's is egglog's, unchanged. A child's has its own
    /// catalog, notification list, pending queues and size estimate, and shares the store's counters, external
    /// functions, dependency graph and container environments with the root by reference. Over those environments a
    /// child keeps container layers of its own (<see cref="ContainerLayers"/>), which it and its descendants read
    /// containers through (<see cref="ContainerView"/>): the copies of its ancestors' containers of classes that its
    /// rebuild claimed and rebuilt, where its own unions displaced an id they hold, or its catch-up claimed, merged or
    /// adopted after a writable ancestor gained them.
    /// </para>
    /// <para>
    /// Lifecycle. <see cref="CreateChild"/> makes a child; children of a context that is not frozen share its thread
    /// domain (<see cref="Domain"/>), so a non-frozen context and everything below it behave as one thread; a second
    /// thread entering the domain is refused. Inside an operation that runs a caller's code (a write batch's action, a
    /// <see cref="ScanMerges{TVisitor}"/> visitor), reads are allowed, but writes to a context of the same domain,
    /// rebuilds with work to do, <see cref="CreateChild"/>, <see cref="Freeze"/> and <see cref="Dispose"/> are refused
    /// before they change anything. <see cref="Dispose"/> drops a child once it has no live children; the root is never
    /// disposed. Depth is limited to <see cref="MaxDepth"/>. Every ancestor's <see cref="Gate"/> counts its live
    /// descendants.
    /// </para>
    /// <para>
    /// Union-find. The root's union-find is egglog's <see cref="DisplacedTable"/>; a child's is an
    /// <see cref="OverlayDisplacedTable"/>: a <see cref="UnionFindOverlay"/> of the classes it merged over its parent's
    /// canonical ids, and a <see cref="DisplacedJournal"/> of the ids that stopped being canonical in it.
    /// <c>find_C = overlay_C ∘ find_P</c> (<see cref="UnionFind"/>). When the parent's union-find grows, the child
    /// re-keys its overlay eagerly at its next write or rebuild (<see cref="CatchUpUnionFind"/>), through one cursor
    /// into the parent's displaced ids.
    /// </para>
    /// <para>
    /// Tables. A child stores only what it writes, in lazily created layers (<see cref="Tables"/>), and hides the
    /// ancestor rows it replaces or deletes; it reads every table through a <see cref="TableView"/>
    /// (<see cref="GetView"/>): its ancestors' layers minus what it hides, plus its own. Writes are canonicalized and
    /// stamped when they merge.
    /// </para>
    /// <para>
    /// Rebuild. <see cref="Rebuild"/> is the one rebuild loop of every level: egglog's passes at the root; in a child,
    /// catch-up with its ancestors (union-find re-key, ancestor appends, the ancestors' dirty container ids and new
    /// containers of classes) in the first pass, then per pass its container pass over its container layers, the
    /// rewrite of its own rows, the hide-and-copy of ancestor rows naming its own losers (losers only) or a container
    /// its pass changed in place, and the merge. Before the root is shared (a child created from it, or a freeze) it
    /// settles the rows written since its last rebuild (<see cref="Settle"/>).
    /// </para>
    /// </remarks>
    internal sealed partial class Context : IDisposable
    {

        /// <summary>
        /// The deepest context: <c>LayeredSubset</c> keeps a 32-bit layer mask, one bit per level.
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
        /// The child's layering; <c>null</c> at the root.
        /// </summary>
        readonly ContextTables? tables;

        /// <summary>
        /// The child's container layers, read through its container view; <c>null</c> at the root.
        /// </summary>
        readonly ContainerLayers? containerLayers;

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
        /// The number of ids displaced in the parent's view when the child was created: those it inherits rather than
        /// journals; 0 at the root. <see cref="DisplacedInViewCount"/> adds the journal to it.
        /// </summary>
        readonly long displacedInParentView;

        /// <summary>
        /// The find this context gives the children it creates: its own chain until it freezes, then its flattened
        /// form. Written before the frozen flag, read after it.
        /// </summary>
        LayeredUnionFind finderForChildren;

        /// <summary>
        /// The frozen chain flattened at freeze, for the invariant checker; <c>null</c> otherwise.
        /// </summary>
        FlattenedUnionFind? flattened;

        /// <summary>
        /// How far into the parent's displaced ids the child has caught up; a child is uf-current when this equals the
        /// parent's <see cref="DisplacedCount"/>.
        /// </summary>
        int unionFindCursor;

        /// <summary>
        /// The displaced count at the end of the last rebuild: rows past it are the next pass's delta, and a context is
        /// clean only when nothing lies past it.
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
        /// domain, is what refuses a dispose from inside a visitor or during another thread's scan.
        /// </summary>
        int activeScans;

        uint semiNaiveFloor;

        /// <summary>
        /// The number of passes the last <see cref="Rebuild"/> ran (<see cref="LastRebuildPasses"/>). Domain thread only.
        /// </summary>
        int lastRebuildPasses;

        /// <summary>
        /// The largest epoch the context captured (<see cref="BeginEpoch"/>) or ran rules at (<see cref="UseEpoch"/>):
        /// with <see cref="semiNaiveFloor"/>, the floor of a rule run's timestamp. Domain thread only.
        /// </summary>
        uint lastEpoch;
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

            // Set later by Freeze and by the contradiction stop.
            frozen = false;
            contradicted = false;
            database.AttachRoot(this, store.IdCounter, store.ClockCounter);
            database.RetractionGuard = store.Schema.RootGuard;
            store.Schema.RootGuard.AttachRoot(this);
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

            // A child of a frozen context runs serially (siblings may run concurrently); a child of a context
            // that is not frozen never runs concurrently with it, so it inherits its heuristics.
            var parallelism = options.Parallelism ?? (parent.IsFrozen ? ParallelHeuristics.Serial : parent.database.Parallelism);
            idBlocks = new IdBlockAllocator(store.Counters, store.IdCounter);

            // The parent is current with its own parent here (CreateChild rebuilt it, or it is frozen), so its find
            // is exact and the child starts with an empty overlay and journal over it.
            parentFinder = parent.FinderForChildren;
            overlay = new UnionFindOverlay();
            journal = new DisplacedJournal();
            overlayTable = new OverlayDisplacedTable(parentFinder, overlay, journal, parent.UnionFindTable, idBlocks);
            finder = overlayTable.Finder;
            finderForChildren = finder;
            unionFindCursor = parent.DisplacedCount;
            parentDisplacedAtCreation = unionFindCursor;
            displacedInParentView = parent.DisplacedInViewCount;
            database = new Database(store.Root.Database, this, overlayTable, idBlocks, parallelism);
            domain = parent.IsFrozen ? new ThreadAffinityGuard() : parent.Domain;

            // No layer yet; cursors at the ancestors' current versions and at the end of their container logs (which
            // CreateChild activated), the leading frozen ancestors settled.
            tables = new ContextTables(store.Schema, this);
            database.AttachLayers(tables);

            // No container layer yet either; the child reads containers through its own and its ancestors' layers, and
            // its container pass fills its own.
            containerLayers = new ContainerLayers(this, parent.containerLayers, idBlocks, parallelism);

            // Read after every cursor is initialized (the union-find cursor and the row cursors above), so anything the
            // parent wrote at an earlier epoch is already part of the child's starting view.
            createdAt = store.ReadClock();
            semiNaiveFloor = createdAt;

            // A case created where a contradiction is already visible starts stopped.
            if (options.StopOnContradiction && store.Schema.Contradiction is { } contradiction && tables.GetView(contradiction).FindRow(ReadOnlySpan<uint>.Empty, out _) >= 0)
                tables.SetContradicted();
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
        /// Gets the child's layering: its layers, hidden sets, views and cursors; <c>null</c> at the root.
        /// </summary>
        public ContextTables? Tables => tables;

        /// <summary>
        /// Gets the child's container layers: per container type, the containers it holds itself and the ancestor ids it
        /// shadows, which its container view reads over its ancestors' (<see cref="ContainerView"/>); <c>null</c> at the
        /// root.
        /// </summary>
        public ContainerLayers? ContainerLayers => containerLayers;

        /// <summary>
        /// Gets the context's container log, from which its live descendants learn of the containers of classes that
        /// appear at its level (<see cref="Relations.ContainerLog"/>): the store's at the root
        /// (<see cref="Store.ContainerLog"/>), the container layers' in a child (<see cref="ContainerLayers.Log"/>).
        /// </summary>
        internal ContainerLog ContainerLog => containerLayers?.Log ?? store.ContainerLog;

        /// <summary>
        /// Gets the gate counting the context's live descendants.
        /// </summary>
        public DescendantGate Gate => gate;

        /// <summary>
        /// Gets the context's thread domain: shared with its parent unless the parent was frozen when this context was
        /// created (or froze since).
        /// </summary>
        public ThreadAffinityGuard Domain => Volatile.Read(ref domain);

        /// <summary>
        /// Gets the clock reading taken when the context was created; 0 for the root. Every row this context or a
        /// descendant writes carries a timestamp at least this large.
        /// </summary>
        public uint CreatedAt => createdAt;

        /// <summary>
        /// Gets the context's change stamp: it moves when a merge adds or removes rows or a clear empties a table, and
        /// not for no-ops or redundant unions.
        /// </summary>
        public long Version => database.ChangeStamp;

        /// <summary>
        /// Gets a stamp of the rows the context's views show: the sum of <see cref="Version"/> over the context and its
        /// ancestors. Each only grows, so the sum moves whenever any of them merges rows in or out (a compaction
        /// included), hides, clears or stops, and only then. A rule set records it when its first header is evaluated,
        /// and a child's run refuses the rule set once it moved (<see cref="Database.RunRuleSet"/>). O(depth).
        /// </summary>
        internal long ViewStamp
        {
            get
            {
                long stamp = 0;
                foreach (var context in ancestors)
                    stamp += context.database.ChangeStamp;

                return stamp;
            }
        }

        /// <summary>
        /// Gets whether this is the root context.
        /// </summary>
        public bool IsRoot => parent is null;

        /// <summary>
        /// Gets whether the context is frozen, and so read-only and shareable across threads.
        /// </summary>
        public bool IsFrozen => frozen;

        /// <summary>
        /// Gets the number of the context's tables that its <see cref="Freeze"/> left uncompacted because live
        /// descendants held their row ids; they keep their tombstones for good. 0 until the freeze completes, and for a
        /// context frozen without live descendants.
        /// </summary>
        /// <remarks>
        /// The database publishes the count before the rest of the freeze ends, so it is read only once the frozen
        /// flag, written last, is set.
        /// </remarks>
        public int CompactionsBlockedAtFreeze => frozen ? database.CompactionsBlockedAtFreeze : 0;

        /// <summary>
        /// Gets whether the context has been disposed.
        /// </summary>
        public bool IsDisposed => disposed;

        /// <summary>
        /// Gets whether an exception escaped one of the child's operations; only <see cref="Dispose"/> is then allowed,
        /// because a hide whose copy was never merged cannot be repaired.
        /// </summary>
        public bool IsFaulted => faulted;

        /// <summary>
        /// Gets whether a contradiction stopped the context (only with
        /// <see cref="ContextOptions.StopOnContradiction"/>).
        /// </summary>
        public bool IsContradicted => contradicted;

        /// <summary>
        /// Gets whether the context may be read: not disposed or faulted, and either frozen or caught up with
        /// everything — nothing merged since its last rebuild is left unrebuilt, and, in a child, no write is staged
        /// and unmerged, its union-find is current with its parent's, and its parent is clean too. The recursion stops
        /// at the first frozen ancestor, so the cost is O(ancestors that are not frozen).
        /// </summary>
        /// <remarks>
        /// At the root, writes staged and not merged do not make it unclean: they are invisible until merged, as in
        /// egglog. A child is not clean while its container pass has not consumed its journal
        /// (<see cref="ContainerLayers.ContainerMark"/>), which a rebuild cut short by the clock can leave behind, or
        /// while an ancestor that is not settled changed since it last caught up
        /// (<see cref="ContextTables.NeedsCatchUp"/>), which includes, while the child has a displacement or a
        /// container layer, an eq-sort container in the ancestor's container log that the pre-loop scan of its rebuild
        /// has not read (<see cref="Relations.ContainerLog"/>, <see cref="ContainerLayers.ScanAdds"/>); nor while the
        /// scan keeps such containers for the rebuild's catch-up, which claims, adopts or merges them
        /// (<see cref="ContainerLayers.HasPending"/>).
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
                    && containerLayers!.ContainerMark == journalMark
                    && containerLayers.HasPending == false
                    && database.Catalog.Notifications.IsEmpty
                    && unionFindCursor == parent.DisplacedCount
                    && tables!.NeedsCatchUp == false
                    && parent.IsClean;
            }
        }

        /// <summary>
        /// Gets whether the context's views show what it sees now: <see cref="IsClean"/> without its term for writes
        /// staged and not merged, at every level of the chain. Staged writes are invisible until merged (as at a root),
        /// so they never make a view stale; a pending own rebuild (of its rows or its container layers), or an ancestor
        /// change not caught up with (an eq-sort container the pre-loop scan of its rebuild has not read, or keeps for
        /// the catch-up, included), do. What rule building, planning and a rule run in a child require
        /// (<see cref="ThrowIfViewNotCurrent"/>). O(unfrozen ancestors).
        /// </summary>
        internal bool IsViewCurrent
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
                    && containerLayers!.ContainerMark == journalMark
                    && containerLayers.HasPending == false
                    && unionFindCursor == parent.DisplacedCount
                    && tables!.NeedsCatchUp == false
                    && parent.IsViewCurrent;
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
        /// Gets or sets the displaced count at the end of the last rebuild pass: the journal rows past it are the next
        /// pass's delta, which <see cref="ContextTables.RebuildPass"/> consumes. Domain thread only.
        /// </summary>
        internal int JournalMark
        {
            get => journalMark;
            set => journalMark = value;
        }

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
        /// Throws unless the context's views may be read by rule building, planning or a rule run: disposed →
        /// <see cref="ObjectDisposedException"/>; faulted or contradicted → <see cref="InvalidOperationException"/>;
        /// views not current (<see cref="IsViewCurrent"/>) → <see cref="InvalidOperationException"/> naming
        /// <see cref="Rebuild"/>.
        /// </summary>
        internal void ThrowIfViewNotCurrent()
        {
            ThrowIfDisposed();
            ThrowIfFaulted();
            ThrowIfContradicted();
            if (IsViewCurrent == false)
                ThrowNotClean();

            Debug.Assert(tables is null || tables.UnsealedCount == 0, "a context's views were read with a hidden set not sealed");
        }

        /// <summary>
        /// Throws unless the context may be read: disposed → <see cref="ObjectDisposedException"/>; faulted or
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

            Debug.Assert(tables is null || tables.UnsealedCount == 0, "a context was read with a hidden set not sealed");
        }

        /// <summary>
        /// Sets the sticky contradiction stop; <see cref="ContextTables.SetContradicted"/> decides when.
        /// </summary>
        internal void MarkContradicted() => contradicted = true;

        /// <summary>
        /// Marks a child faulted: an exception escaped one of its operations and may have left a hide without its copy,
        /// which cannot be repaired, so every later call except <see cref="Dispose"/> throws. The root keeps
        /// <see cref="Relations.Database"/>'s own exception guarantee instead and is never faulted.
        /// </summary>
        internal void Fault()
        {
            if (IsRoot == false)
                faulted = true;
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> once the context is faulted.
        /// </summary>
        internal void ThrowIfFaulted()
        {
            if (faulted)
                throw new InvalidOperationException("An operation of this context failed and left it inconsistent; it can only be disposed.");
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> once a contradiction stopped the context.
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
