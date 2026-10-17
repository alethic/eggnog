using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// One context of an <see cref="EGraph"/>: the bridge's side of a storage <see cref="Relations.Context"/>. It runs
    /// rules with semi-naive progress of its own (<see cref="RunRules"/>), stages and flushes host writes
    /// (<see cref="StageRow"/>, <see cref="FlushUpdates"/>), answers reads in its view (<see cref="Find"/>,
    /// <see cref="TryLookup"/>), and creates, freezes and disposes contexts (<see cref="CreateChild"/>,
    /// <see cref="Freeze"/>, <see cref="Dispose"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// State: the context's <see cref="RuleProgress"/> per rule (its watermarks), its staged host writes
    /// (<see cref="HostWrites"/>) and its context-local values (<see cref="GetLocal{T}"/>). Everything else is
    /// storage's: the context's rows, union-find, flags and lifecycle, which the properties forward.
    /// </para>
    /// <para>
    /// Semantics: a child starts as a copy of its parent and costs in proportion to what it touches (context = copy). Its
    /// runs see the parent's rows read-through, and rules may seed their watermarks from an ancestor's progress when
    /// their actions are query-determined. Every Id constant of a rule runs in its canonical form in the context. The
    /// root is egglog's database; the e-graph's egglog-compatible API runs on it.
    /// </para>
    /// <para>
    /// Errors: an exception escaping a child's write, run, rebuild or freeze faults the child (a hide whose copy was
    /// never merged cannot be repaired), after which only <see cref="Dispose"/> is allowed; refusals made before
    /// anything changes do not fault. A failure a panic or merge function recorded is not an exception: the operation
    /// that observes it takes it and throws <see cref="PanicException"/> (<see cref="RunRules"/>,
    /// <see cref="FlushUpdates"/>, <see cref="CreateChild"/>, <see cref="Freeze"/>).
    /// </para>
    /// <para>
    /// Threading: a context that is not frozen and every descendant created under it are one thread domain; every
    /// operation enters it, so a second thread fails fast. Children of a frozen context get their own domains and may run
    /// concurrently; a frozen context is read from any thread.
    /// </para>
    /// </remarks>
    internal sealed partial class EGraphContext : IDisposable
    {

        readonly EGraph owner;
        readonly EGraphContext? parent;
        readonly Context context;
        readonly HostWrites hostWrites = new();

        /// <summary>
        /// Initializes the bridge side of <paramref name="context"/> and makes it the context's host.
        /// </summary>
        /// <param name="owner">The e-graph.</param>
        /// <param name="parent">The parent's bridge side, or <c>null</c> for the root.</param>
        /// <param name="context">The storage context.</param>
        /// <exception cref="InvalidOperationException">The context has another host already.</exception>
        internal EGraphContext(EGraph owner, EGraphContext? parent, Context context)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(context);
            this.owner = owner;
            this.parent = parent;
            this.context = context;
            context.Host = this;
        }

        /// <summary>
        /// Gets the e-graph the context belongs to.
        /// </summary>
        public EGraph Owner => owner;

        /// <summary>
        /// Gets the parent context, or <c>null</c> for the root.
        /// </summary>
        public EGraphContext? Parent => parent;

        /// <summary>
        /// Gets the storage context.
        /// </summary>
        internal Context Context => context;

        /// <summary>
        /// Gets whether this is the root context.
        /// </summary>
        public bool IsRoot => parent is null;

        /// <summary>
        /// Gets the context's depth: 0 for the root.
        /// </summary>
        public int Depth => context.Depth;

        /// <summary>
        /// Gets the context's options.
        /// </summary>
        public ContextOptions Options => context.Options;

        /// <summary>
        /// Gets when the context's own operations (rule sets, rebuilds, merges, index builds) go parallel, and on how
        /// many threads: the root's are the e-graph's; a child's are <see cref="ContextOptions.Parallelism"/> when
        /// given, otherwise <see cref="ParallelHeuristics.Serial"/> for a child of a frozen context (siblings may run
        /// concurrently, <see cref="ConcurrentChildren"/>) and its parent's for a child of a context that is not frozen
        /// (the two never run concurrently). Fixed at creation.
        /// </summary>
        public ParallelHeuristics Parallelism => context.Database.Parallelism;

        /// <summary>
        /// Gets the context's change stamp: it moves when a merge adds or removes rows, a clear empties a table, a hide
        /// is added or a contradiction stops the context, and not for no-ops or redundant unions.
        /// </summary>
        public long Version => context.Version;

        /// <summary>
        /// Gets the clock reading taken when the context was created; 0 for the root.
        /// </summary>
        public uint CreatedAt => context.CreatedAt;

        /// <summary>
        /// Gets whether the context is frozen, read-only and shareable across threads.
        /// </summary>
        public bool IsFrozen => context.IsFrozen;

        /// <summary>
        /// Gets the number of the context's tables that <see cref="Freeze"/> left uncompacted because live descendants
        /// held their row ids (<see cref="Context.CompactionsBlockedAtFreeze"/>).
        /// </summary>
        public int CompactionsBlockedAtFreeze => context.CompactionsBlockedAtFreeze;

        /// <summary>
        /// Gets whether the context is disposed.
        /// </summary>
        public bool IsDisposed => context.IsDisposed;

        /// <summary>
        /// Gets whether an exception escaped one of the child's operations: only
        /// <see cref="Dispose"/> is then allowed. Never true at the root.
        /// </summary>
        public bool IsFaulted => context.IsFaulted;

        /// <summary>
        /// Gets whether a contradiction stopped the context (only with
        /// <see cref="ContextOptions.StopOnContradiction"/>): it then answers only <see cref="IsContradictory"/>.
        /// </summary>
        public bool IsStopped => context.IsContradicted;

        /// <summary>
        /// Gets the number of rows the context stores itself (what the rule layer's row limit counts): its database's
        /// size estimate, which in a child sums its own layers only. O(1).
        /// </summary>
        public long OwnRowCount => context.Database.TotalSizeEstimate;

        /// <summary>
        /// Gets whether the context may be read: its storage context is clean, it has no host writes staged, and the
        /// same holds for every ancestor up to the first frozen one. O(ancestors that are not frozen).
        /// </summary>
        public bool IsClean
        {
            get
            {
                if (context.IsClean == false || hostWrites.IsEmpty == false)
                    return false;

                for (var up = parent; up is not null && up.IsFrozen == false; up = up.parent)
                    if (up.hostWrites.IsEmpty == false)
                        return false;

                return true;
            }
        }

        /// <summary>
        /// Creates a child: a context that starts as a copy of this one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A context that is not frozen is brought up to date first (<see cref="EnsureClean"/>: its host writes and its
        /// ancestors' are flushed, and it is rebuilt), so the child starts from what this context holds; the storage
        /// context then rebuilds and, at the root, settles. The child shares this context's thread domain unless
        /// this context is frozen, in which case it gets its own and may run on another thread.
        /// </para>
        /// <para>
        /// A failure that this context's rebuild or the root's settle recorded during the creation, or that was pending
        /// on it, is taken right after: the new child is disposed and <see cref="PanicException"/> is thrown, as a run
        /// that observed the failure would throw it. This context stays usable.
        /// </para>
        /// </remarks>
        /// <param name="options">The child's options; <see cref="ContextOptions.Default"/> if <c>null</c>.</param>
        /// <returns>The child.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted or stopped; it is at the deepest level; the
        /// call comes from inside another operation of the same thread domain; or the domain is held by another
        /// thread.</exception>
        /// <exception cref="PanicException">A failure was recorded on this context (by the flush, the rebuild or the
        /// settle, or before the call).</exception>
        public EGraphContext CreateChild(ContextOptions? options = null)
        {
            context.ThrowIfDisposed();
            context.ThrowIfFaulted();
            context.ThrowIfContradicted();
            if (context.IsFrozen == false)
            {
                ThrowIfReentrant(nameof(CreateChild));
                EnsureClean();
            }

            var child = context.CreateChild(options);
            if (TakeFailure(out var message))
            {
                child.Dispose();
                throw new PanicException(message);
            }

            return new EGraphContext(owner, this, child);
        }

        /// <summary>
        /// Disposes the child: it leaves its parent, every ancestor stops counting it, and its rows, staged host
        /// writes, progress and locals are released. A second call does nothing.
        /// </summary>
        /// <exception cref="InvalidOperationException">The context is the root; it has live children; the call comes from
        /// inside another operation of the same thread domain or while a scan of the context runs; or the domain is held
        /// by another thread.</exception>
        public void Dispose()
        {
            if (context.IsDisposed)
                return;

            context.Dispose();
            hostWrites.Release();
            ReleaseProgress();
            ReleaseLocals();
        }

        /// <inheritdoc />
        public override string ToString() => $"context at depth {Depth}{(IsFrozen ? ", frozen" : "")}{(IsStopped ? ", stopped" : "")}{(IsFaulted ? ", faulted" : "")}{(IsDisposed ? ", disposed" : "")}";

        /// <summary>
        /// Takes the failure recorded on this context, if any: at the root, the e-graph's panic (its side channel, or the
        /// root database's failure, <see cref="EGraph.TryTakePanic"/>); in a child, the child database's failure.
        /// </summary>
        /// <param name="message">The failure's message.</param>
        /// <returns><c>true</c> if a failure was recorded.</returns>
        internal bool TakeFailure(out string message)
        {
            if (parent is null)
                return owner.TryTakePanic(out message);

            var failure = context.Database.TakeFailure();
            message = failure ?? string.Empty;
            return failure is not null;
        }

        /// <summary>
        /// Throws unless the context may be written, a stopped context aside (its writes and runs are no-ops): not
        /// disposed, frozen or faulted.
        /// </summary>
        void ThrowIfUnwritable()
        {
            context.ThrowIfDisposed();
            if (context.IsFrozen)
                ThrowFrozen();

            context.ThrowIfFaulted();
        }

        /// <summary>
        /// Throws unless the context may be read: disposed → <see cref="ObjectDisposedException"/>; faulted or
        /// stopped → <see cref="InvalidOperationException"/>; not clean (<see cref="IsClean"/>) →
        /// <see cref="InvalidOperationException"/> naming <see cref="EnsureClean"/>. Call inside the domain of a context
        /// that is not frozen.
        /// </summary>
        void ThrowIfNotReadable()
        {
            context.ThrowIfDisposed();
            context.ThrowIfFaulted();
            context.ThrowIfContradicted();
            if (IsClean == false)
                ThrowNotClean();
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> when the calling thread is inside an operation of the context's
        /// thread domain (a write batch's action, a rule's action or external, a visitor): a structural call there would
        /// change what the running operation captured.
        /// </summary>
        void ThrowIfReentrant(string operation)
        {
            if (context.Domain.Depth > 0)
                throw new InvalidOperationException($"{operation} cannot be called from inside another operation of the same context's thread domain (a write, a rule action or external, a visitor).");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowFrozen()
        {
            throw new InvalidOperationException("A frozen context cannot be written.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNotClean()
        {
            throw new InvalidOperationException("The context has host writes staged, or pending changes, or an ancestor has changed since it was brought up to date; call EnsureClean() first.");
        }

    }

}
