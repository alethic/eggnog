using System;

namespace Eggnog
{

    /// <summary>
    /// One engine context with the rule layer's state for it: where facts are written, rules run, reads answered and
    /// terms extracted. In R1 every graph has one scope, its <see cref="EGraph.Root"/>; child scopes, frozen scopes and
    /// cases follow in later milestones.
    /// </summary>
    /// <remarks>
    /// Not thread-safe (D47): every call except the identity members and <see cref="Dispose"/> enters the scope's thread
    /// domain for the whole call, and a second thread gets <see cref="InvalidOperationException"/> instead of interleaving.
    /// After a <see cref="RuleEvaluationException"/> from a run or a write the scope is faulted (D18), and every later call
    /// except <see cref="Dispose"/> throws; a faulted root makes its graph unusable.
    /// </remarks>
    public sealed partial class Scope : IDisposable
    {

        volatile Exception? failure;
        volatile bool disposed;
        HostBatch? openBatch;

        /// <summary>
        /// Initializes the root scope of <paramref name="graph"/>.
        /// </summary>
        /// <param name="graph">The graph.</param>
        internal Scope(EGraph graph)
        {
            Graph = graph;
            IsRoot = true;
            Path = "root";
            Origin = graph.RootOrigin;
            Guard = new ScopeGuard();
            Runtime = new ScopeRuntime { Owner = this };
            Engine = new RootEngineScope(graph, graph.Engine);
        }

        /// <summary>
        /// Gets the graph the scope belongs to.
        /// </summary>
        public EGraph Graph { get; }

        /// <summary>
        /// Gets whether this is the graph's root.
        /// </summary>
        public bool IsRoot { get; }

        /// <summary>
        /// Gets whether the scope is faulted: set after a <see cref="RuleEvaluationException"/> from a run or a write
        /// (D18).
        /// </summary>
        public bool IsFaulted => failure is not null;

        /// <summary>
        /// Gets what faulted the scope; <c>null</c> while it is not faulted. Every later call except
        /// <see cref="Dispose"/> throws <see cref="InvalidOperationException"/> with this as its inner exception.
        /// </summary>
        public Exception? Failure => failure;

        /// <summary>
        /// Gets whether <see cref="Dispose"/> has been called (D40). Never true for the root.
        /// </summary>
        public bool IsDisposed => disposed;

        /// <summary>
        /// Gets the engine's change stamp (X30): it moves on every effective change to this scope's own layer. Queries,
        /// reads and writes that change nothing never move it.
        /// </summary>
        public long Version => Engine.Version;

        /// <summary>
        /// Gets the scope's path, for messages: <c>root</c>.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Disposes the scope (D40): never throws. It makes the scope unusable at once and invalidates the class values it
        /// returned; a second call does nothing; on the root it does nothing, because the root lives as long as the graph.
        /// </summary>
        public void Dispose()
        {
            if (IsRoot || disposed)
                return;

            disposed = true;
            Origin.Disposed = true;
            TryRelease();
        }

        /// <summary>
        /// Gets the scope's path.
        /// </summary>
        /// <returns>The path.</returns>
        public override string ToString() => Path;

        /// <summary>
        /// Gets the engine side of the scope.
        /// </summary>
        internal EngineScope Engine { get; }

        /// <summary>
        /// Gets the buffers external functions write during this scope's engine calls.
        /// </summary>
        internal ScopeRuntime Runtime { get; }

        /// <summary>
        /// Gets the guard of the scope's thread domain (D47).
        /// </summary>
        internal ScopeGuard Guard { get; }

        /// <summary>
        /// Gets the origin of the class values the scope returns (D39).
        /// </summary>
        internal ScopeOrigin Origin { get; }

        /// <summary>
        /// Faults the scope (D18); the first cause is kept.
        /// </summary>
        /// <param name="cause">What faulted it.</param>
        internal void Fault(Exception cause)
        {
            failure ??= cause;
        }

        /// <summary>
        /// Throws when the scope is disposed or faulted.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted; the cause is the inner exception.</exception>
        internal void ThrowIfNotUsable()
        {
            if (disposed)
                throw new ObjectDisposedException(Path, $"Scope '{Path}' is disposed.");

            if (failure is { } cause)
                throw new InvalidOperationException($"Scope '{Path}' is faulted: {cause.Message}", cause);
        }

        /// <summary>
        /// Throws when the scope may not be written or run: inside a run observer (R1b), or frozen (R2).
        /// </summary>
        /// <exception cref="InvalidOperationException">The scope may not be written.</exception>
        internal void ThrowIfNotWritable()
        {
            if (Guard.InObserver)
                throw new InvalidOperationException("Not allowed inside a run observer");
        }

        /// <summary>
        /// Releases the scope's engine state once nothing defers it (D40). The root is never released, so in R1 this does
        /// nothing; R2 frees a released scope's local rules and context.
        /// </summary>
        internal void TryRelease()
        {

        }

        /// <summary>
        /// Enters the scope for a call: refuses user delegates, takes the guard, and checks the scope is usable and has no
        /// open <see cref="Write"/> batch.
        /// </summary>
        /// <exception cref="InvalidOperationException">The call is not allowed now.</exception>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        void Enter()
        {
            EngineCallMarker.ThrowIfActive();
            Guard.Enter(this);
            try
            {
                ThrowIfNotUsable();
                if (openBatch is not null)
                    throw new InvalidOperationException($"Scope '{Path}' has a Write batch open; make its calls through the ScopeWriter.");
            }
            catch
            {
                Guard.Exit();
                throw;
            }
        }

        /// <summary>
        /// Enters the scope for a read (§6.6.3): as <see cref="Enter"/>, then brings the scope current (a no-op at the R1
        /// root, which every flush and run leaves rebuilt).
        /// </summary>
        void EnterRead()
        {
            Enter();
            try
            {
                Engine.EnsureClean();
            }
            catch
            {
                Guard.Exit();
                throw;
            }
        }

        /// <summary>
        /// Enters the scope for a write or a run: as <see cref="Enter"/>, and the scope must be writable.
        /// </summary>
        void EnterWrite()
        {
            Enter();
            try
            {
                ThrowIfNotWritable();
                Engine.EnsureClean();
            }
            catch
            {
                Guard.Exit();
                throw;
            }
        }

    }

}
