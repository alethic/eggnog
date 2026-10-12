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
        /// A child created with <see cref="ContextOptions.StopOnContradiction"/> where a contradiction row is already
        /// visible starts stopped (§6.9). Interim (M9.3): the root's settling of rows written since its last rebuild
        /// (§7.6) arrives with row catch-up (M9.4).
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

                // M9.4 clears the store's container dirty log here when the root's gate reaches zero (§8.2).
                tables!.Release();
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
        /// The loop ends by committing the child's cursors, sealing its hidden sets and, when it caught up with an ancestor,
        /// dropping its cached views (§7.1). An exception escaping a child's rebuild faults it (X12). DEBUG builds check the
        /// context's invariants at the end (<see cref="CheckInvariants"/>).
        /// </para>
        /// <para>
        /// Interim (M9.3, §17): a child's passes do no row work (catch-up of ancestor rows, rewriting its own rows, hiding
        /// and copying ancestor rows that mention its losers), which arrives in M9.4. A rebuild that would need any of it
        /// is refused with <see cref="NotSupportedException"/> before anything changes, so the child is not faulted: an
        /// ancestor appended or replaced rows since the child last caught up, or the child's union-find changes while it
        /// holds rows of a table with id columns, or while an ancestor row it sees names one of its new losers
        /// (<see cref="ThrowIfRowWorkPending"/>). A child whose ancestor changed only its union-find, and that holds nothing
        /// the change could make stale, catches up.
        /// </para>
        /// </remarks>
        /// <returns>Whether any pass changed anything; <c>false</c> for a frozen, contradicted or clean context.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted; its domain is held by another thread; or
        /// it (or a parent that is not frozen) has work to do and the call comes from inside a write batch's action or a
        /// <c>ScanMerges</c> visitor of the same domain (§8.3), where a rebuild with nothing to do is allowed.</exception>
        /// <exception cref="NotSupportedException">Interim (M9.3): the rebuild would need row work
        /// (<see cref="ThrowIfRowWorkPending"/>).</exception>
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

            // Interim (M9.3): refuse row work before anything changes; an ancestor that changed only its union-find needs
            // none when the child holds no rows the change could make stale.
            ThrowIfRowWorkPending();
            var caughtUp = tables is { NeedsCatchUp: true };

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
            if (tables is { } t)
            {
                caughtUp |= t.CommitCursors();
                t.SealHidden();
                t.InvalidateViews(caughtUp);
            }

#if DEBUG
            // A contradiction stopped the merge with writes pending: the context answers only IsContradictory (§6.9).
            if (contradicted == false)
                CheckInvariants();
#endif
            return changed;
        }

        /// <summary>
        /// Makes the context read-only and shareable across threads (§8.1). Its parent must be frozen already.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Rebuilds the context, then freezes its database (<see cref="Relations.Database.Freeze"/>): every table freezes
        /// (the root's union-find compresses every path, a child's overlay stops changing, each sorted-writes table
        /// compacts where its gate allows and builds its rebuild index), the indexes of <paramref name="warmup"/> are built,
        /// the index catalogs refresh serially from now on, and staging into any table throws. Then it builds every view:
        /// a child folds every hidden set into its views' frozen parts (<see cref="ContextTables.Freeze"/>), the root
        /// freezes its root views (<see cref="LayeredSchema.FreezeRoot"/>). It flattens a child's chain of union-find levels
        /// into one map for the children created from now on (§5.4); gives each live child subtree a thread domain of its
        /// own, so children of a frozen context may run concurrently (§8.3); and publishes the frozen flag last, which
        /// orders every structure a child reads before the child's own reads. A second call does nothing. Freezing a
        /// context with live children is allowed.
        /// </para>
        /// <para>
        /// Interim (M9.3, §17): the root's settling of the rows written since its last rebuild (§7.6) arrives in M9.4. A
        /// child whose rebuild would need row work is refused with <see cref="NotSupportedException"/> before anything
        /// changes, and is not faulted (<see cref="ThrowIfRowWorkPending"/>).
        /// </para>
        /// </remarks>
        /// <param name="warmup">The cached indexes to build before the context is shared, or <c>null</c>.</param>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The parent is not frozen; the context is faulted or contradicted;
        /// writes are staged in it and were never notified for merging; the call comes from inside another operation of
        /// the same thread domain; or the domain is held by another thread.</exception>
        /// <exception cref="NotSupportedException">Interim (M9.3): the child's rebuild would need row work.</exception>
        public void Freeze(FreezeWarmup? warmup = null)
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

                // Interim (M9.3): a rebuild that would need row work is refused before anything changes, as a direct
                // Rebuild refuses it, so the refusal does not fault the child here either.
                if (parent is not null && IsClean == false)
                    ThrowIfRowWorkPending();

                try
                {
                    Rebuild();
                    database.Freeze(warmup);
                    if (tables is { } t)
                    {
                        t.Freeze();
                        flattened = FlattenedUnionFind.Build(finder);
                        Volatile.Write(ref finderForChildren, LayeredUnionFind.Flattened(store.RootUnionFind.UnionFind, flattened));
                    }
                    else
                    {
                        store.Schema.FreezeRoot();
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
