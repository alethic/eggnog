using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    /// <summary>
    /// The container environments of a database, one per registered container type: egglog's <c>ContainerValues</c>
    /// (core-relations/src/containers/mod.rs:69-257). It registers container types, interns and looks up containers of a
    /// registered type, and rebuilds every environment against the union-find.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Types get dense <see cref="ContainerTypeId"/>s in registration order, as egglog's <c>ContainerIds</c> index its
    /// <c>TypeId</c>s; a process-wide slot per type (<see cref="ContainerTypeSlot{TContainer}"/>) makes finding a type's
    /// environment one array load rather than a hash lookup.
    /// </para>
    /// <para>
    /// <see cref="RebuildAll"/> remembers the union-find's version between calls (egglog's <c>subset_tracker</c>), so the
    /// next rebuild sees only the ids displaced since and can rebuild incrementally.
    /// </para>
    /// <para>
    /// Interning is immediate, global and never rolled back, as in egglog: a container interned while rules run exists
    /// from then on, whatever happens to the rule's other writes.
    /// </para>
    /// <para>
    /// Threading: <see cref="RegisterValue{TContainer}"/>, <see cref="TryGetValue{TContainer}"/>,
    /// <see cref="ForEach{TContainer, TVisitor}"/> and the environment lookups may run on any number of threads at once
    /// (egglog's <c>&amp;self</c>); <see cref="RegisterType{TContainer}"/>, <see cref="RebuildAll"/> and
    /// <see cref="Clone"/> need exclusive access (<c>&amp;mut self</c>).
    /// </para>
    /// </remarks>
    internal sealed class ContainerValues
    {

        readonly ParallelHeuristics parallelism;
        readonly SubsetTracker subsetTracker;
        readonly DenseIdMap<ContainerTypeId, ContainerEnvBase> envs;

        /// <summary>
        /// The type id of each registered type plus one, by process-wide slot; zero for a type not registered here.
        /// </summary>
        int[] bySlot;

        /// <summary>
        /// Whether a registered type holds e-class ids (<see cref="ContainerEnvBase.HoldsIds"/>).
        /// </summary>
        volatile bool hasEqContainers;

        /// <summary>
        /// The eq-container version (<see cref="EqVersion"/>), shared with every environment that holds e-class ids.
        /// </summary>
        readonly StrongBox<long> eqVersion = new();

        /// <summary>
        /// Initializes an instance with no container types. egglog's <c>ContainerValues::new</c>.
        /// </summary>
        /// <param name="parallelism">When rebuilds go parallel; <see cref="ParallelHeuristics.Default"/> if
        /// <c>null</c>.</param>
        public ContainerValues(ParallelHeuristics? parallelism = null)
        {
            this.parallelism = parallelism ?? ParallelHeuristics.Default;
            subsetTracker = new SubsetTracker();
            envs = new DenseIdMap<ContainerTypeId, ContainerEnvBase>();
            bySlot = Array.Empty<int>();
        }

        /// <summary>
        /// Initializes a copy, for <see cref="Clone"/>.
        /// </summary>
        ContainerValues(ContainerValues source)
        {
            parallelism = source.parallelism;
            subsetTracker = source.subsetTracker.Clone();
            envs = new DenseIdMap<ContainerTypeId, ContainerEnvBase>(source.envs.IdCount);
            eqVersion.Value = source.EqVersion;
            foreach (var (id, env) in source.envs)
            {
                var copy = env.Clone();
                if (copy.HoldsIds)
                    copy.EqVersionCounter = eqVersion;

                envs.Insert(id, copy);
            }

            bySlot = (int[])source.bySlot.Clone();
            hasEqContainers = source.hasEqContainers;
        }

        /// <summary>
        /// Gets when rebuilds go parallel.
        /// </summary>
        public ParallelHeuristics Parallelism => parallelism;

        /// <summary>
        /// Gets the number of registered container types; their ids are <c>[0, TypeCount)</c>.
        /// </summary>
        public int TypeCount => envs.IdCount;

        /// <summary>
        /// Gets whether any registered container type holds e-class ids: the quick test of the eq-container guard
        /// (<see cref="EqContainerGuard"/>), which a child's rebuild runs only when it holds. One volatile read.
        /// </summary>
        public bool HasEqContainers => hasEqContainers;

        /// <summary>
        /// Gets the eq-container version: it moves each time an environment that holds e-class ids gains a container
        /// whose content it did not hold before (a new intern at the root, or a content a root container rebuild
        /// produced). A child context records the version its eq-container guard last checked its own displacements
        /// against, and is not clean while the version has moved since (<see cref="Context.IsClean"/>): a new content
        /// may name an id the child displaced, which only a container rebuild in the child could reconcile, and
        /// children never rebuild containers. One volatile read.
        /// </summary>
        public long EqVersion => Volatile.Read(ref eqVersion.Value);

        /// <summary>
        /// Gets whether some container of a type that holds e-class ids holds <paramref name="id"/>: a child that
        /// displaced such an id would need the container rebuilt in the child, which children never do, so the child's
        /// eq-container guard refuses it (<see cref="EqContainerGuard"/>). O(types) probes of exact element indexes;
        /// thread-safe.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if an eq-sort container holds it.</returns>
        public bool AnyEqContainerMentions(uint id)
        {
            if (hasEqContainers == false)
                return false;

            foreach (var (_, env) in envs)
                if (env.HoldsIds && env.AnyContainerMentions(id))
                    return true;

            return false;
        }

        /// <summary>
        /// Registers the container type <typeparamref name="TContainer"/>, whose fresh ids come from
        /// <paramref name="idCounter"/> and whose colliding ids <paramref name="merge"/> resolves, and returns its id.
        /// egglog's <c>register_type</c> (mod.rs:242-256). Registering a type again returns its id and keeps its
        /// environment, counter and merge function. Requires exclusive access.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="idCounter">The counter fresh container ids come from (the bridge's e-class id counter).</param>
        /// <param name="merge">Resolves the ids of containers that become equal (the bridge's
        /// <see cref="UnionContainerMerge"/>).</param>
        /// <param name="holdsIds">Whether the containers hold e-class ids (eq-sort containers, the default); containers
        /// over base values pass <c>false</c> and may then be interned in child contexts
        /// (<see cref="ContainerEnvBase.HoldsIds"/>). Registering again with <c>true</c> a type registered with
        /// <c>false</c> makes its environment hold ids.</param>
        /// <returns>The type's id.</returns>
        public ContainerTypeId RegisterType<TContainer>(CounterId idCounter, ContainerMergeFunction merge, bool holdsIds = true)
            where TContainer : struct, IContainerValue<TContainer>
        {
            ArgumentNullException.ThrowIfNull(merge);

            if (TryGetTypeId<TContainer>(out var existing))
            {
                // A type registered over base values and now over an eq sort: one environment, as egglog's.
                if (holdsIds && envs[existing].HoldsIds == false)
                {
                    envs[existing].MarkHoldsIds(eqVersion);
                    hasEqContainers = true;
                }

                return existing;
            }

            var id = envs.NextId;
            var env = new ContainerEnv<TContainer>(id, merge, idCounter, parallelism, holdsIds);
            if (holdsIds)
            {
                env.EqVersionCounter = eqVersion;
                hasEqContainers = true;
            }

            var slot = ContainerTypeSlot<TContainer>.Index;
            if (slot >= bySlot.Length)
                Array.Resize(ref bySlot, Math.Max(slot + 1, bySlot.Length * 2));

            envs.Insert(id, env);
            bySlot[slot] = id.Index + 1;
            return id;
        }

        /// <summary>
        /// Gets the id of the container type <typeparamref name="TContainer"/>, if registered. O(1).
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if registered.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetTypeId<TContainer>(out ContainerTypeId id)
            where TContainer : struct, IContainerValue<TContainer>
        {
            var slot = ContainerTypeSlot<TContainer>.Index;
            var table = bySlot;
            if ((uint)slot < (uint)table.Length && table[slot] != 0)
            {
                id = new ContainerTypeId(table[slot] - 1);
                return true;
            }

            id = default;
            return false;
        }

        /// <summary>
        /// Gets the environment of <typeparamref name="TContainer"/>, if registered. egglog's <c>get</c>
        /// (mod.rs:129-133). O(1); hot paths should look the environment up once and keep it.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="env">The environment.</param>
        /// <returns><c>true</c> if registered.</returns>
        public bool TryGetEnvironment<TContainer>([MaybeNullWhen(false)] out ContainerEnv<TContainer> env)
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (TryGetTypeId<TContainer>(out var id))
            {
                env = Unsafe.As<ContainerEnv<TContainer>>(envs[id]);
                return true;
            }

            env = null;
            return false;
        }

        /// <summary>
        /// Gets the environment of <typeparamref name="TContainer"/>.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <returns>The environment.</returns>
        /// <exception cref="InvalidOperationException">The type is not registered.</exception>
        public ContainerEnv<TContainer> GetEnvironment<TContainer>()
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (TryGetEnvironment<TContainer>(out var env))
                return env;

            throw new InvalidOperationException($"The container type {typeof(TContainer).Name} must be registered before its values are.");
        }

        /// <summary>
        /// Gets the environment with id <paramref name="id"/>.
        /// </summary>
        /// <param name="id">The type id.</param>
        /// <returns>The environment.</returns>
        /// <exception cref="KeyNotFoundException">No such type.</exception>
        public ContainerEnvBase GetEnvironment(ContainerTypeId id) => envs[id];

        /// <summary>
        /// Interns <paramref name="container"/> and returns its id. egglog's <c>register_val</c> (mod.rs:154-163).
        /// Thread-safe.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="container">The container; it must not change afterwards.</param>
        /// <param name="state">The execution state that mints a fresh id.</param>
        /// <returns>The container's id.</returns>
        /// <exception cref="InvalidOperationException">The type is not registered.</exception>
        public uint RegisterValue<TContainer>(in TContainer container, ExecutionState state)
            where TContainer : struct, IContainerValue<TContainer>
        {
            return GetEnvironment<TContainer>().GetOrInsert(container, state);
        }

        /// <summary>
        /// Gets the container of type <typeparamref name="TContainer"/> with id <paramref name="id"/>. egglog's
        /// <c>get_val</c> (mod.rs:145-152), without its lock guard: the container is immutable. Thread-safe.
        /// </summary>
        /// <typeparam name="TContainer">The container type; the caller must know it.</typeparam>
        /// <param name="id">The container id.</param>
        /// <param name="container">The container, or <c>default</c>.</param>
        /// <returns><c>false</c> if the type is not registered or no container has the id.</returns>
        public bool TryGetValue<TContainer>(uint id, out TContainer container)
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (TryGetEnvironment<TContainer>(out var env))
                return env.TryGetContainer(id, out container);

            container = default;
            return false;
        }

        /// <summary>
        /// Visits every container of type <typeparamref name="TContainer"/> with its id; nothing if the type is not
        /// registered. egglog's <c>for_each</c> (mod.rs:135-143).
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="visitor">Receives each container and id; must not call back into the environment.</param>
        public void ForEach<TContainer, TVisitor>(ref TVisitor visitor)
            where TContainer : struct, IContainerValue<TContainer>
            where TVisitor : struct, IContainerVisitor<TContainer>
        {
            if (TryGetEnvironment<TContainer>(out var env))
                env.ForEach(ref visitor);
        }

        /// <summary>
        /// Rebuilds the contents and ids of every container against the rebuilder <paramref name="table"/> provides (the
        /// union-find), merging containers that become equal, and reports what changed. egglog's <c>rebuild_all</c>
        /// (mod.rs:165-210). Requires exclusive access.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When the rebuilder has a hint column, the table's rows appended since the last call
        /// (<see cref="SubsetTracker"/>) are offered to each environment, which rebuilds incrementally if they are few
        /// enough. Environments rebuild in parallel when <see cref="ParallelHeuristics.ParallelizeInterContainerOp"/>
        /// holds for their number, each with its own clone of <paramref name="state"/>, flushed when it is done; the
        /// summaries combine in type order. Finally the dirty ids are closed upward (<see cref="ExpandDirtyIdClosure"/>).
        /// </para>
        /// <para>
        /// Merges stage unions through the state (or its clones); the caller flushes and merges them, as
        /// <see cref="Database.RebuildContainers"/> does.
        /// </para>
        /// </remarks>
        /// <param name="tableId">The providing table's id.</param>
        /// <param name="table">The providing table.</param>
        /// <param name="state">The state merge functions run in.</param>
        /// <returns>What changed.</returns>
        public ContainerRebuildSummary RebuildAll(TableId tableId, Table table, ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(state);

            var rebuilder = table.GetRebuilder(ReadOnlySpan<ColumnId>.Empty);
            if (rebuilder is null)
                return new ContainerRebuildSummary();

            var hasRecent = rebuilder.HintColumn is not null;
            var recent = hasRecent ? subsetTracker.RecentUpdates(tableId, table) : default;
            try
            {
                SubsetRef? recentRef = hasRecent ? recent.AsRef() : null;
                var summary = parallelism.ParallelizeInterContainerOp(envs.IdCount)
                    ? RebuildParallel(table, rebuilder, recentRef, state)
                    : RebuildSerial(table, rebuilder, recentRef, state);
                ExpandDirtyIdClosure(summary);
                return summary;
            }
            finally
            {
                recent.Dispose();
            }
        }

        /// <summary>
        /// Adds to the dirty ids of <paramref name="summary"/>, until nothing more is added, the ids of the containers of
        /// any type that hold a dirty id. egglog's <c>expand_dirty_id_closure</c> (mod.rs:212-240).
        /// </summary>
        /// <remarks>
        /// A container rebuilt in place changes the meaning of every container holding it, although none of those was
        /// rebuilt: with <c>(p (vec-of (vec-of (w (b)))))</c> and <c>(rewrite (w x) x)</c>, the inner vector rebuilds in
        /// place to <c>vec-of (b)</c>, and without the closure the outer vector's row would not be re-stamped, so a rule
        /// matching <c>(p (vec-of (vec-of (b))))</c> could miss it. With the exact element index the closure contains
        /// only live containers. Cost: the sum of the index fan-out over the closure.
        /// </remarks>
        /// <param name="summary">The summary to close.</param>
        internal void ExpandDirtyIdClosure(ContainerRebuildSummary summary)
        {
            ArgumentNullException.ThrowIfNull(summary);

            if (summary.DirtyCount == 0)
                return;

            var frontier = new List<uint>(summary.DirtyIds.ToArray());
            var seen = new HashSet<uint>(frontier);
            var next = new OrderedValueSet();
            while (frontier.Count > 0)
            {
                next.Clear();
                var span = CollectionsMarshal.AsSpan(frontier);
                foreach (var (_, env) in envs)
                    env.ExtendContainersContaining(span, next);

                frontier.Clear();
                foreach (var value in next.AsSpan())
                {
                    if (seen.Add(value))
                    {
                        summary.NoteDirtyId(value);
                        frontier.Add(value);
                    }
                }
            }
        }

        /// <summary>
        /// Creates an independent copy: environments copied (sharing their immutable containers and merge functions) and
        /// the tracker's versions kept. egglog's <c>Clone</c>. Requires exclusive access.
        /// </summary>
        /// <returns>The copy.</returns>
        public ContainerValues Clone() => new(this);

        /// <summary>
        /// Checks the invariants of every environment (<see cref="ContainerEnvBase.CheckInvariants"/>). For tests and
        /// debugging; requires exclusive access.
        /// </summary>
        public void CheckInvariants()
        {
            foreach (var (_, env) in envs)
                env.CheckInvariants();
        }

        /// <summary>
        /// Rebuilds the environments one after another with the caller's state.
        /// </summary>
        ContainerRebuildSummary RebuildSerial(Table table, Rebuilder rebuilder, SubsetRef? recent, ExecutionState state)
        {
            var summary = new ContainerRebuildSummary();
            foreach (var (_, env) in envs)
                summary.Extend(env.ApplyRebuild(table, rebuilder, recent, state));

            return summary;
        }

        /// <summary>
        /// Rebuilds the environments in parallel, each with a clone of the state: egglog's <c>par_bridge</c> over the
        /// environments (mod.rs:179-195). The summaries combine in type order, so the result does not depend on the
        /// schedule.
        /// </summary>
        ContainerRebuildSummary RebuildParallel(Table table, Rebuilder rebuilder, SubsetRef? recent, ExecutionState state)
        {
            var list = new List<ContainerEnvBase>(envs.IdCount);
            foreach (var (_, env) in envs)
                list.Add(env);

            var summaries = new ContainerRebuildSummary[list.Count];
            try
            {
                Parallel.For(0, list.Count, parallelism.Options, i =>
                {
                    var local = state.Clone(ExecutionStateCloneSite.ContainerRebuild);
                    summaries[i] = list[i].ApplyRebuild(table, rebuilder, recent, local);
                    local.Flush();
                });
            }
            catch (AggregateException e)
            {
                var inner = e.Flatten().InnerExceptions;
                if (inner.Count == 1)
                    ExceptionDispatchInfo.Capture(inner[0]).Throw();

                throw;
            }

            var summary = new ContainerRebuildSummary();
            foreach (var part in summaries)
                summary.Extend(part);

            return summary;
        }

    }

}
