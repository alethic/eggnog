using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Creates a child: a context that starts as a copy of this one and stores only what is asserted in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A context that is not frozen is entered (its domain) and rebuilt first, and the root also settles the rows
        /// written since its last rebuild (<see cref="Settle"/>), so the child's cursors start on a clean parent whose
        /// rows are canonical and whose union-find is current with its own parent's; the child shares that domain. A
        /// frozen context is not entered, and the child gets a domain of its own, so children of a frozen context may
        /// run on different threads. The child is added to this context's children under their lock, and the gate of
        /// every ancestor, this one included, counts it until it is disposed. Before the child takes its cursors, the
        /// container log of every ancestor that is not frozen is activated (<see cref="Relations.ContainerLog"/>), so
        /// they start at its end. The first child seals the store's counters.
        /// </para>
        /// <para>
        /// A child created with <see cref="ContextOptions.StopOnContradiction"/> where a contradiction row is already
        /// visible starts stopped. DEBUG builds check that a root that is not frozen is settled (every root row
        /// canonical in the root's view) before the child is created.
        /// </para>
        /// <para>
        /// A context that is not frozen also brings its rebuild indexes, and those of its ancestors that are not
        /// frozen, up to date (<see cref="RefreshRebuildIndexesForChildren"/>), so the child never builds an ancestor's
        /// whole index on its own path: its hide-and-copy of the ancestor rows that name its losers probes those
        /// indexes and is never a scan. This is child-creation work of the parent.
        /// </para>
        /// <para>
        /// Like <see cref="Write{TResult}(Func{ExecutionState, TResult})"/> and <see cref="Rebuild"/>, the rebuild and
        /// the settle record a failure a merge function reports (<see cref="Relations.Database.Failure"/>) and do not
        /// throw: the failure stays on this context for its caller to take (the bridge takes it right after), and the
        /// child, which has a failure slot of its own, is created.
        /// </para>
        /// </remarks>
        /// <param name="options">The child's options; <see cref="ContextOptions.Default"/> if <c>null</c>.</param>
        /// <returns>The child.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted or contradicted; its parent (or an ancestor
        /// that is not frozen) was stopped by a contradiction; it is at depth <see cref="MaxDepth"/>; the call comes from
        /// inside another operation of the same thread domain; or the domain is held by another thread.</exception>
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
                if (IsRoot)
                {
                    Settle();
                    AssertRootSettled();
                }

                RefreshRebuildIndexesForChildren();
                return AddChild(options);
            }
        }

        /// <summary>
        /// Disposes the child: it leaves its parent's children, every ancestor's gate stops counting it, and its
        /// structures are released. A second call does nothing; every later call to another member throws
        /// <see cref="ObjectDisposedException"/>. The ids it minted leak, which is harmless; its journal needs no
        /// reader tracking. The container log of every ancestor whose gate reaches zero is cleared and deactivated
        /// (<see cref="Relations.ContainerLog.Clear"/>): no cursor into it is left.
        /// </summary>
        /// <remarks>
        /// A frozen context is scanned without its domain, so a dispose from inside one of its
        /// <see cref="ScanMerges{TVisitor}"/> visitors is not caught by the domain; it is refused through the context's
        /// count of running scans instead, which also refuses a dispose while another thread scans it. A faulted
        /// context may be disposed.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The context is the root; it has live children; the call comes
        /// from inside another operation of the same thread domain, or while a scan of the context runs; or the domain is
        /// held by another thread.</exception>
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
                // consistent reading from one taken half-way.
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
                            throw new InvalidOperationException("Dispose cannot be called while a scan of the context runs (ScanMerges, ScanOwn or ScanAncestorsMentioning), from its visitor or from another thread.");

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

                // An ancestor whose gate reached zero has no descendant left to read its container log. Under a frozen
                // ancestor another thread may create a child meanwhile; it takes no cursor into a frozen level.
                for (var i = 0; i < Depth; i++)
                    if (ancestors[i].gate.HasLiveDescendants == false)
                        ancestors[i].ContainerLog.Clear();

                tables!.Release();
                containerLayers!.Release();
                overlay!.Release();
                journal!.Release();
                Volatile.Write(ref host, null);
            }
        }

        /// <summary>
        /// Brings the context up to date: the one rebuild loop of every level. Rebuilds a parent that is not frozen
        /// first; then, while a pass changes something, runs passes, each in an epoch of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// At the root a pass is egglog's (<see cref="Relations.Database.RebuildPass"/> over every function table of
        /// the store, which also merges writes staged at the root), and the loop runs only when the union-find grew
        /// since the last rebuild or writes are pending, with the numbering of egglog-bridge's <c>rebuild</c>: read the
        /// clock, run the pass, advance the clock, until a pass changes nothing.
        /// </para>
        /// <para>
        /// In a child the first pass catches up: it re-keys the union-find over the parent's new displacements
        /// (<see cref="CatchUpUnionFind"/>), absorbs the rows the unsettled ancestors appended since its cursors
        /// (<see cref="ContextTables.CatchUpRows"/>), reads the ids of the containers they rebuilt in place from the
        /// Dirty streams of their container logs (<see cref="ContextTables.ReadDirty"/>), and catches up with the
        /// containers of classes they gained (<see cref="ContainerLayers.CatchUp"/>): it settles each that equals one
        /// the child holds under another id, merging the unions that stages into the union-find at once, and queues
        /// for the pass the ids to claim and the contents to adopt. Every pass then runs the container step
        /// (<see cref="RebuildContainerLayers"/>): the container pass claims and rebuilds, in the child's container
        /// layers, the containers of classes its displacements since the last pass concern, and what the catch-up
        /// queued; the unions their merges staged are merged into the union-find, and nothing else is merged before
        /// the tables' pass; and the own rows naming a dirty container are re-stamped. In the first pass, when the
        /// catch-up staged copies of ancestor rows, the step repeats until a container pass changes nothing, so that
        /// nested containers the ancestors made are merged with the child's equal ones before the copies merge. Then
        /// it rewrites the own rows that name an id displaced since the last pass, hides and copies the ancestor rows
        /// that name the child's own new losers or a container its pass changed in place, and merges
        /// (<see cref="ContextTables.RebuildPass"/>): containers first, as egglog's rebuild loop runs them
        /// (egglog-bridge/src/lib.rs:666-681). A contradiction that stops the child ends the loop at once.
        /// </para>
        /// <para>
        /// The loop ends by committing the child's cursors, sealing its hidden sets and, when it caught up with an
        /// ancestor, dropping its cached views (<see cref="EndPasses"/>). An exception escaping a child's pass faults
        /// it; a clock that has run out, which a pass finds before its work (<see cref="BeginEpoch"/>), does not. When
        /// the clock runs out before a later pass, the loop's end runs first, so what the passes before it did is
        /// committed, sealed and visible, and the next rebuild that has an epoch goes on from there: the context is
        /// clean if those passes left nothing to do, and otherwise stays unclean. DEBUG builds check the context's
        /// invariants at the end, and after a clock that ran out if the context is clean (<see cref="CheckInvariants"/>).
        /// </para>
        /// <para>
        /// Before any pass, a child refuses to catch up with a parent that a contradiction stopped: such a parent is
        /// never clean again. A child that stops on contradictions stops too; any other child is refused, unfaulted
        /// (<see cref="StopIfParentStopped"/>). And when the container log of an unsettled ancestor gained an eq-sort
        /// container since the child last read it, the pre-loop scan runs, even when nothing else is to be done: it
        /// consumes the containers that do not concern the child and keeps the others for the first pass's catch-up
        /// (<see cref="ContainerLayers.ScanAdds"/>), so a child that no new container concerns stays clean and takes no
        /// epoch.
        /// </para>
        /// </remarks>
        /// <returns>Whether any pass changed anything; <c>false</c> for a frozen, contradicted or clean context.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted; its domain is held by another thread;
        /// its parent (or an ancestor that is not frozen) was stopped by a contradiction and it does not stop on
        /// contradictions; or it (or a parent that is not frozen) has work to do and the call comes from inside a write
        /// batch's action or a visitor of the same domain, where a rebuild with nothing to do is allowed.</exception>
        /// <exception cref="OverflowException">The store's clock has run out (<see cref="Store.ReadClock"/>); the
        /// context is not faulted.</exception>
        public bool Rebuild()
        {
            if (frozen)
                return false;

            ThrowIfDisposed();
            var current = Domain;
            using var scope = current.Enter();
            ThrowIfFaulted();
            lastRebuildPasses = 0;
            if (parent is { IsFrozen: false } p)
            {
                p.Rebuild();
                StopIfParentStopped(p);
            }

            if (contradicted)
                return false;

            // An unsettled ancestor's container log gained an eq-sort container since the child last read it; the scan
            // changes nothing a reader sees, so it runs even inside a callback, before the clean test it is part of.
            if (parent is not null && tables!.HasNewAddedEntries)
                containerLayers!.ScanAdds();

            if (parent is null ? journalMark == DisplacedCount && database.Catalog.Notifications.IsEmpty : IsClean)
                return false;

            // A pass inside a running batch or visitor would run an epoch inside its epoch or change what it reads.
            ThrowIfInCallback(current, nameof(Rebuild));

            var first = true;
            var changed = false;
            var caughtUp = false;
            while (true)
            {
                // Each pass reads the clock before its work and outside the fault: a clock that has run out throws
                // here, before the pass changes anything, so it leaves the context unfaulted. Each pass takes the
                // journal from where the last one stopped (ContextTables.RebuildPass), so the passes that already ran
                // are ended as the loop ends them, and a later rebuild goes on from there.
                uint epoch;
                try
                {
                    epoch = BeginEpoch();
                }
                catch (OverflowException) when (first == false)
                {
                    EndPasses(caughtUp);
#if DEBUG
                    if (contradicted == false && IsClean)
                        CheckInvariants();
#endif
                    throw;
                }

                bool pass;
                try
                {
                    pass = false;
                    uint[]? inherited = null;
                    if (first && parent is not null)
                    {
                        var importFrom = unionFindCursor;
                        pass |= CatchUpUnionFind(epoch) != journal!.Count;
                        caughtUp |= tables!.CatchUpRows(epoch);
                        pass |= caughtUp;
                        inherited = tables.ReadDirty();

                        // The containers of classes the ancestors gained: collisions settle now, and their unions merge
                        // before the container pass, which claims and adopts what the catch-up queued. Only the
                        // union-find merges here (see RebuildContainerLayers).
                        if (contradicted == false && containerLayers!.CatchUp(epoch, parent.DisplacedIds.Slice(importFrom, unionFindCursor - importFrom)))
                        {
                            database.MergeTable(store.UnionFindTable, epoch);
                            pass = true;
                        }
                    }

                    if (parent is null)
                    {
                        pass |= database.RebuildPass(store.UnionFindTable, store.Schema.FunctionTables, epoch);
                    }
                    else
                    {
                        // A first pass whose catch-up staged row copies settles its containers before they merge.
                        var restamp = RebuildContainerLayers(epoch, inherited, first && caughtUp, ref pass);
                        pass |= tables!.RebuildPass(epoch, restamp);
                    }
                }
                catch when (IsRoot == false)
                {
                    Fault();
                    throw;
                }
                finally
                {
                    EndEpoch();
                }

                changed |= pass;
                first = false;
                lastRebuildPasses++;
                if (pass == false || contradicted)
                    break;
            }

            journalMark = DisplacedCount;
            EndPasses(caughtUp);

#if DEBUG
            // A contradiction stopped the merge with writes pending: the context answers only IsContradictory.
            if (contradicted == false)
                CheckInvariants();
#endif
            return changed;
        }

        /// <summary>
        /// Ends the passes of a <see cref="Rebuild"/> in a child: commits its cursors to what the first pass's catch-up
        /// read, seals the hidden sets the passes grew, and drops its cached views, every one when it caught up with an
        /// ancestor or a cursor moved, else the ones the passes made stale. Nothing at the root. Domain thread only.
        /// </summary>
        /// <param name="caughtUp">Whether the first pass copied and hid ancestor rows.</param>
        void EndPasses(bool caughtUp)
        {
            if (tables is not { } t)
                return;

            caughtUp |= t.CommitCursors();
            t.SealHidden();
            t.InvalidateViews(caughtUp);
        }

        /// <summary>
        /// Gets the number of passes the context's last <see cref="Rebuild"/> ran: 0 when it found nothing to do (or
        /// the context is frozen or stopped), otherwise every pass, the last one, which changed nothing, included. The
        /// bridge reports it as <c>IterationReport.RebuildPasses</c>. Domain thread only.
        /// </summary>
        internal int LastRebuildPasses => lastRebuildPasses;

        /// <summary>
        /// Makes the context read-only and shareable across threads. Its parent must be frozen already.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Rebuilds the context (the root also settles, <see cref="Settle"/>), then freezes its database
        /// (<see cref="Relations.Database.Freeze"/>): every table freezes (the root's union-find compresses every path,
        /// a child's overlay stops changing, each sorted-writes table compacts where its gate allows and builds its
        /// rebuild index), the indexes of <paramref name="warmup"/> are built, the index catalogs refresh serially from
        /// now on, and staging into any table throws. Then it builds every view: a child folds every hidden set into
        /// its views' frozen parts (<see cref="ContextTables.Freeze"/>) and freezes its container layers
        /// (<see cref="ContainerLayers.Freeze"/>), read without locks from then on, and the root freezes its root views
        /// (<see cref="LayeredSchema.FreezeRoot"/>); either drains its container log, which never grows again
        /// (<see cref="Relations.ContainerLog.Freeze"/>). It flattens a child's chain of union-find levels into one map
        /// for the children created from now on; gives each live child subtree a thread domain of its own, so children
        /// of a frozen context may run concurrently; and publishes the frozen flag last, which orders every structure a
        /// child reads before the child's own reads. A second call does nothing. Freezing a context with live children
        /// is allowed: they catch up once, and get their own domains.
        /// </para>
        /// <para>
        /// An exception escaping a child's rebuild or freeze proper faults it, except a clock that has run out before a
        /// rebuild pass changed anything (<see cref="Rebuild"/>). A failure recorded and not taken, before
        /// the call or by its own rebuild or settle, refuses the freeze without faulting: the context stays consistent
        /// and unfrozen, and its caller may take the failure and freeze again. So does a contradiction its rebuild
        /// found.
        /// </para>
        /// </remarks>
        /// <param name="warmup">The cached indexes to build before the context is shared, or <c>null</c>.</param>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The parent is not frozen; the context is faulted or contradicted
        /// (or its rebuild stopped it); a failure was recorded and not taken, before the call or by its rebuild or
        /// settle; writes are staged in it and were never notified for merging; the call comes from inside another
        /// operation of the same thread domain; or the domain is held by another thread.</exception>
        /// <exception cref="OverflowException">The store's clock has run out; the context is not faulted.</exception>
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

                // A failure a write or a merge reported and nobody took would be shared with every descendant, which
                // could never take it.
                ThrowIfFailureNotTaken();

                // The rebuild faults a child itself, except where the clock has run out before a pass changed
                // anything; only the root settles, and the root is never faulted.
                Rebuild();
                if (parent is null)
                {
                    Settle();
                    AssertRootSettled();
                }

                // The same for a failure the rebuild or the settle just recorded (a merge conflict found by a copy, a
                // fold or a rewrite): the bridge orders it so too (rebuild, check the failure, freeze). A recorded
                // failure is not an exception: the context stays consistent and unfrozen, and is not faulted. A
                // contradiction the rebuild found stops the context, which is then not frozen either.
                ThrowIfContradicted();
                ThrowIfFailureNotTaken();

                try
                {
                    database.Freeze(warmup);
                    if (tables is { } t)
                    {
                        t.Freeze();
                        containerLayers!.Freeze();
                        flattened = FlattenedUnionFind.Build(finder);
                        Volatile.Write(ref finderForChildren, LayeredUnionFind.Flattened(store.RootUnionFind.UnionFind, flattened));
                    }
                    else
                    {
                        store.Schema.FreezeRoot();
                        store.ContainerLog.Freeze();
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
        /// Settles the root before it is shared: rewrites, at an epoch of its own, the rows written since each table
        /// last rebuilt that name a non-canonical id (a raw write may have left them stale), merges, and rebuilds if
        /// the merge staged unions, so every root row is canonical in the root's view when a child or another thread
        /// reads it. Called by <see cref="CreateChild"/> and <see cref="Freeze"/> at the root, after its rebuild;
        /// nothing elsewhere. Root programs that never create a child or freeze never settle, so their timestamps are
        /// egglog's.
        /// </summary>
        /// <remarks>
        /// Rows before a table's rebuild mark were canonical at the mark and are kept canonical by the journal-driven
        /// rebuilds since; rows after it are exactly the rows a raw write could have left stale. A root whose tables hold no
        /// row written since their last rebuild returns at once, without an epoch. Cost: O(tables) plus O(rows written since
        /// the last rebuild) (<see cref="SortedWritesTable.SettleAppends"/>).
        /// </remarks>
        internal void Settle()
        {
            if (parent is not null || frozen)
                return;

            var uf = store.UnionFindTable;
            var any = false;
            foreach (var id in store.Schema.FunctionTables)
            {
                if (store.Schema[id]!.Root.HasRowsSinceRebuild(uf))
                {
                    any = true;
                    break;
                }
            }

            if (any == false)
                return;

            var epoch = BeginEpoch();
            try
            {
                database.RunWithTakenOut(uf, store.Schema.FunctionTables, new SettleTableOperation(uf, store.RootUnionFind, epoch));
                database.MergeAll(epoch);

                // The merge appended the canonical rewrites; the marks move past them, so the next settle does not read
                // them again. A union the merge staged reaches each table's next rebuild as its delta, which still
                // finds every row naming its loser through the rebuild index.
                foreach (var id in store.Schema.FunctionTables)
                    store.Schema[id]!.Root.MarkRebuilt(uf);
            }
            finally
            {
                EndEpoch();
            }

            // The settle's merge may have staged unions (congruence among rewritten rows).
            Rebuild();
        }

        /// <summary>
        /// Called by a child after it rebuilt <paramref name="p"/>, its parent that is not frozen: refuses to catch up
        /// with a parent a contradiction stopped. Such a parent answers only <c>IsContradictory</c> and may have
        /// stopped its merge with writes pending, so it is neither clean nor ever will be, and a child catches up only
        /// with a frozen or clean parent. A child that stops on contradictions stops too: the contradiction row is
        /// visible in its parent, so in it. Any other child is refused, without faulting; it can only be disposed.
        /// </summary>
        /// <param name="p">The parent, just rebuilt.</param>
        /// <exception cref="InvalidOperationException">The parent is stopped and this context does not stop on
        /// contradictions.</exception>
        void StopIfParentStopped(Context p)
        {
            if (p.contradicted == false || contradicted)
                return;

            if (options.StopOnContradiction)
            {
                tables!.SetContradicted();
                return;
            }

            throw new InvalidOperationException("The parent context was stopped by a contradiction and answers only IsContradictory, so this context cannot catch up with it; dispose it.");
        }

        /// <summary>
        /// Brings the rebuild indexes of this context and of its ancestors that are not frozen up to date before a
        /// child is created from it: the child's hide-and-copy of the ancestor rows that name its losers, and its lift
        /// reads, probe them, and would otherwise build an ancestor's whole index on their own path, O(ancestor rows),
        /// the first time. Frozen ancestors built theirs at their freeze. Afterwards a child refreshes an ancestor's
        /// index only from the rows the ancestor appended since, which its catch-up reads anyway.
        /// </summary>
        /// <remarks>
        /// Cost, as child-creation work of the parent: O(tables of each context that is not frozen) plus, per shared table
        /// with rebuild columns, the rows appended since its index's last refresh (all of its rows the first time, or after
        /// a compaction between child lifetimes).
        /// </remarks>
        void RefreshRebuildIndexesForChildren()
        {
            for (var level = Depth; level >= 0 && ancestors[level].frozen == false; level--)
            {
                var context = ancestors[level];
                if (context.tables is { } t)
                {
                    t.RefreshOwnRebuildIndexes();
                    continue;
                }

                foreach (var id in store.Schema.FunctionTables)
                    if (store.Schema[id] is { IsContextPrivate: false } s && s.RebuildColumns.Length > 0)
                        s.Root.SharedRebuildIndex();
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
        /// also takes), after activating the container log of every ancestor that is not frozen, so the child's cursors
        /// start at their end, then the ancestors' gates. The whole change is bracketed by the gates' change marks, so
        /// the invariant checker can tell a consistent reading of the tree and the gates from one taken half-way. A
        /// child whose construction throws leaves no log active that no gate counts a descendant for.
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

                    // Ancestors that are not frozen share this context's domain, which the caller holds, so no
                    // descendant of theirs is created or disposed meanwhile.
                    for (var i = 0; i <= Depth; i++)
                        if (ancestors[i].frozen == false)
                            ancestors[i].ContainerLog.Activate();

                    try
                    {
                        child = new Context(this, options);
                    }
                    catch
                    {
                        for (var i = 0; i <= Depth; i++)
                            if (ancestors[i].frozen == false && ancestors[i].gate.HasLiveDescendants == false)
                                ancestors[i].ContainerLog.Clear();

                        throw;
                    }

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
        /// Gets the live children, as a snapshot taken under their lock: for the bridge's tree check.
        /// </summary>
        /// <returns>A new array.</returns>
        internal Context[] SnapshotChildren()
        {
            lock (children)
                return children.ToArray();
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

        /// <summary>
        /// Throws when the context's database holds a failure that was recorded and not taken: freezing would share it
        /// with every descendant, none of which could take it.
        /// </summary>
        void ThrowIfFailureNotTaken()
        {
            if (database.Failure is { } failure)
                throw new InvalidOperationException($"The context recorded a failure that was not taken ({failure}); take it (Database.TakeFailure) before freezing.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowReentrant(string operation)
        {
            throw new InvalidOperationException($"{operation} cannot be called from inside another operation of the same context's thread domain (a write, a rule action or external, a visitor or a sink).");
        }

    }

}
