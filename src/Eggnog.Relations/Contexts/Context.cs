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
    /// thread; a second thread entering the domain is refused. <see cref="Dispose"/> drops a child once it has no live
    /// children; the root is never disposed. Depth is limited to 31 (X36). Every ancestor's
    /// <see cref="Gate"/> counts its live descendants.
    /// </para>
    /// <para>
    /// This milestone (M9.1) builds the skeleton: the store, the child database, the clock and the ids. A child has no
    /// layers yet and refuses writes (<see cref="NotSupportedException"/>); its union-find, row layers, rebuild,
    /// freeze and reads arrive in M9.2–M9.4.
    /// </para>
    /// </remarks>
    internal sealed partial class Context : IDisposable
    {

        /// <summary>
        /// The deepest context: <c>LayeredSubset</c> keeps a 32-bit layer mask, one bit per level (X36).
        /// </summary>
        public const int MaxDepth = 31;

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
        /// The thread domain; reassigned (on the domain's own thread) when an ancestor freezes, so read volatile.
        /// </summary>
        ThreadAffinityGuard domain;

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

            // Set later by Freeze (M9.2) and by the contradiction stop (M9.3).
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

            // M9.1 interim (§17): a placeholder union-find that is never written; the overlay table arrives in M9.2.
            database = new Database(store.Root.Database, this, new DisplacedTable(), idBlocks, parallelism);
            domain = parent.IsFrozen ? new ThreadAffinityGuard() : parent.Domain;

            // Read after every cursor is initialized (M9.2-M9.4 add the cursors before this point), so anything the
            // parent wrote at an earlier epoch is already part of the child's starting view.
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
        static void ThrowDisposed()
        {
            throw new ObjectDisposedException(nameof(Context));
        }

    }

}
