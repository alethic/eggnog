using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Creates a child: a context that starts as a copy of this one and stores only what is asserted in it
        /// (colored-contexts spec §8.2).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A context that is not frozen is entered (its domain) and rebuilt first, so the child's cursors start on a clean
        /// parent whose union-find is current with its own parent's; the child shares that domain. A frozen context is not
        /// entered, and the child gets a domain of its own, so children of a frozen context may run on different threads
        /// (§8.3). The child is added to this context's children under their lock, and the gate of every ancestor, this
        /// one included, counts it until it is disposed. The first child seals the store's counters (X16).
        /// </para>
        /// <para>
        /// Interim (M9.2): the root's settling of rows written since its last rebuild (§7.6) arrives with the layers that
        /// read them (M9.4).
        /// </para>
        /// </remarks>
        /// <param name="options">The child's options; <see cref="ContextOptions.Default"/> if <c>null</c>.</param>
        /// <returns>The child.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted or contradicted; it is at depth
        /// <see cref="MaxDepth"/>; the call comes from inside another operation of the same thread domain; or the
        /// domain is held by another thread.</exception>
        public Context CreateChild(ContextOptions? options = null)
        {
            ThrowIfDisposed();
            ThrowIfFaulted();
            ThrowIfContradicted();
            var current = Domain;
            if (current.Depth > 0)
                ThrowReentrant(nameof(CreateChild));

            if (Depth + 1 > MaxDepth)
                throw new InvalidOperationException($"A context can be at most {MaxDepth} levels below the root.");

            options ??= ContextOptions.Default;
            if (IsFrozen)
                return AddChild(options);

            using (current.Enter())
            {
                Rebuild();
                return AddChild(options);
            }
        }

        /// <summary>
        /// Disposes the child (§8.2): it leaves its parent's children, every ancestor's gate stops counting it, and its
        /// structures are released. A second call does nothing; every later call to another member throws
        /// <see cref="ObjectDisposedException"/>. The ids it minted leak, which is harmless; its journal needs no reader
        /// tracking.
        /// </summary>
        /// <remarks>
        /// A frozen context is scanned without its domain, so a dispose from inside one of its
        /// <see cref="ScanMerges{TVisitor}"/> visitors is not caught by the domain; it is refused through the context's
        /// count of running scans instead (§8.3), which also refuses a dispose while another thread scans it.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The context is the root; it has live children; the call comes
        /// from inside another operation of the same thread domain, or while a <c>ScanMerges</c> of the context runs; or
        /// the domain is held by another thread.</exception>
        public void Dispose()
        {
            if (disposed)
                return;

            if (IsRoot)
                throw new InvalidOperationException("The root context cannot be disposed.");

            var current = Domain;
            if (current.Depth > 0)
                ThrowReentrant(nameof(Dispose));

            using (current.Enter())
            {
                // The tree and the gates change together, between BeginChange and EndChange, so the checker can tell a
                // consistent reading from one taken half-way (§12.7 item 7).
                for (var i = 0; i < Depth; i++)
                    ancestors[i].gate.BeginChange();

                try
                {
                    lock (children)
                    {
                        if (disposed)
                            return;

                        if (children.Count > 0)
                            throw new InvalidOperationException($"The context has {children.Count} live children; dispose them first.");

                        if (Interlocked.CompareExchange(ref activeScans, ScansClosed, 0) != 0)
                            throw new InvalidOperationException("Dispose cannot be called while a ScanMerges of the context runs, from its visitor or from another thread.");

                        disposed = true;
                    }

                    lock (parent!.children)
                        parent.children.Remove(this);

                    for (var i = 0; i < Depth; i++)
                        ancestors[i].gate.Exit();
                }
                finally
                {
                    for (var i = 0; i < Depth; i++)
                        ancestors[i].gate.EndChange();
                }

                // M9.3-M9.4 release the layers here, and clear the store's container dirty log when the root's gate
                // reaches zero (§8.2).
                overlay!.Release();
                journal!.Release();
                Volatile.Write(ref host, null);
            }
        }

        /// <summary>
        /// Brings the context up to date (§7.1, X10): the one rebuild loop of every level. Rebuilds a parent that is not
        /// frozen first; then, while a pass changes something, runs passes, each in an epoch of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// At the root a pass is egglog's (<see cref="Relations.Database.RebuildPass"/> over every function table of the
        /// store, which also merges writes staged at the root), and the loop runs only when the union-find grew since the
        /// last rebuild or writes are pending (X42), with the numbering of egglog-bridge's <c>rebuild</c>: read the clock,
        /// run the pass, advance the clock, until a pass changes nothing. In a child the first pass catches up with the
        /// parent's union-find (<see cref="CatchUpUnionFind"/>), and every pass merges what is staged in the child.
        /// </para>
        /// <para>
        /// Interim (M9.2, §17): a child's passes do no row work (catch-up of ancestor rows, rewriting its own rows, hiding
        /// and copying ancestor rows that mention its losers), which arrives with its layers (M9.3-M9.4). An exception
        /// escaping a child's rebuild faults it (X12). DEBUG builds check the context's invariants at the end
        /// (<see cref="CheckInvariants"/>).
        /// </para>
        /// </remarks>
        /// <returns>Whether any pass changed anything; <c>false</c> for a frozen, contradicted or clean context.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted; its domain is held by another thread; or
        /// it (or a parent that is not frozen) has work to do and the call comes from inside a write batch's action or a
        /// <c>ScanMerges</c> visitor of the same domain (§8.3), where a rebuild with nothing to do is allowed.</exception>
        public bool Rebuild()
        {
            if (frozen)
                return false;

            ThrowIfDisposed();
            var current = Domain;
            using var scope = current.Enter();
            ThrowIfFaulted();
            if (parent is { IsFrozen: false } p)
                p.Rebuild();

            if (contradicted)
                return false;

            if (parent is null ? journalMark == DisplacedCount && database.Catalog.Notifications.IsEmpty : IsClean)
                return false;

            // A pass inside a running batch or visitor would run an epoch inside its epoch (I3) or change what it reads.
            ThrowIfInCallback(current, nameof(Rebuild));

            var first = true;
            var changed = false;
            try
            {
                while (true)
                {
                    var epoch = BeginEpoch();
                    bool pass;
                    try
                    {
                        pass = false;
                        if (first && parent is not null)
                            pass |= CatchUpUnionFind(epoch) != journal!.Count;

                        pass |= parent is null
                            ? database.RebuildPass(store.UnionFindTable, store.Schema.FunctionTables, epoch)
                            : database.MergeAll(epoch);
                    }
                    finally
                    {
                        EndEpoch();
                    }

                    changed |= pass;
                    first = false;
                    if (pass == false || contradicted)
                        break;
                }
            }
            catch when (IsRoot == false)
            {
                Fault();
                throw;
            }

            journalMark = DisplacedCount;
#if DEBUG
            CheckInvariants();
#endif
            return changed;
        }

        /// <summary>
        /// Makes the context read-only and shareable across threads (§8.1). Its parent must be frozen already.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Rebuilds the context, freezes its union-find table (the root's compresses every path; a child's overlay stops
        /// changing) and seals it; flattens a child's chain of union-find levels into one map for the children created
        /// from now on (§5.4); gives each live child subtree a thread domain of its own, so children of a frozen context
        /// may run concurrently (§8.3); and publishes the frozen flag last, which orders every structure a child reads
        /// before the child's own reads. A second call does nothing. Freezing a context with live children is allowed:
        /// they catch up once.
        /// </para>
        /// <para>
        /// Interim (M9.2, §17): only the union-find is frozen. The storage half (sealing and compacting the row tables,
        /// prebuilding their indexes and views, settling the root) arrives in M9.3-M9.4; meanwhile writes through the
        /// context are refused once it is frozen.
        /// </para>
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The parent is not frozen; the context is faulted or contradicted;
        /// unions are staged in it and were never notified for merging; the call comes from inside another operation of
        /// the same thread domain; or the domain is held by another thread.</exception>
        public void Freeze()
        {
            if (frozen)
                return;

            ThrowIfDisposed();
            var current = Domain;
            if (current.Depth > 0)
                ThrowReentrant(nameof(Freeze));

            using (current.Enter())
            {
                if (frozen)
                    return;

                ThrowIfDisposed();
                ThrowIfFaulted();
                ThrowIfContradicted();
                if (parent is { IsFrozen: false })
                    throw new InvalidOperationException("A context can be frozen only once its parent is.");

                try
                {
                    Rebuild();
                    UnionFindTable.Freeze();
                    if (parent is not null)
                    {
                        flattened = FlattenedUnionFind.Build(finder);
                        Volatile.Write(ref finderForChildren, LayeredUnionFind.Flattened(store.RootUnionFind.UnionFind, flattened));
                    }
                }
                catch when (IsRoot == false)
                {
                    Fault();
                    throw;
                }

                // Each live child subtree gets a domain of its own; this runs on the domain's thread outside any other
                // operation, so no descendant is in use.
                lock (children)
                    foreach (var child in children)
                        child.AssignDomain(new ThreadAffinityGuard());

                frozen = true;
            }
        }

        /// <summary>
        /// Gives this context and every descendant created under it <paramref name="guard"/> as their thread domain:
        /// called when this context's parent freezes. Descendants are not frozen (their ancestor was not), so they all
        /// shared this context's previous domain.
        /// </summary>
        /// <param name="guard">The new domain.</param>
        void AssignDomain(ThreadAffinityGuard guard)
        {
            Volatile.Write(ref domain, guard);
            lock (children)
                foreach (var child in children)
                    child.AssignDomain(guard);
        }

        /// <summary>
        /// Creates the child and links it in: under the child list's lock (which a concurrent dispose of this context
        /// also takes), then the ancestors' gates. The whole change is bracketed by the gates' change marks, so the
        /// invariant checker can tell a consistent reading of the tree and the gates from one taken half-way (§12.7).
        /// </summary>
        Context AddChild(ContextOptions options)
        {
            // The first child seals the counters; under the definition lock, so a counter being added completes first.
            if (store.HasHadChildren == false)
                lock (store.DefinitionLock)
                    store.NoteChildCreated();

            for (var i = 0; i <= Depth; i++)
                ancestors[i].gate.BeginChange();

            try
            {
                Context child;
                lock (children)
                {
                    ThrowIfDisposed();
                    child = new Context(this, options);
                    children.Add(child);
                }

                for (var i = 0; i <= Depth; i++)
                    ancestors[i].gate.Enter();

                return child;
            }
            finally
            {
                for (var i = 0; i <= Depth; i++)
                    ancestors[i].gate.EndChange();
            }
        }

        /// <summary>
        /// Gets the number of live children. A snapshot.
        /// </summary>
        internal int ChildCount
        {
            get
            {
                lock (children)
                    return children.Count;
            }
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowReentrant(string operation)
        {
            throw new InvalidOperationException($"{operation} cannot be called from inside another operation of the same context's thread domain (a write, a rule action or external, a visitor or a sink).");
        }

    }

}
