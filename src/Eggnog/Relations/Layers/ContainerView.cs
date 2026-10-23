using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The containers one context sees: at the root, the root's environments (<see cref="ContainerValues"/>); in a
    /// child, its container layers and its ancestors' over them (<see cref="ContainerLayers"/>). Every read of a
    /// container of classes by id or by content goes through the view of the context it serves, so a child reads the
    /// containers it holds itself where it holds them, and the root's elsewhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A readonly struct of two references, the root's environments and the context's layers, made once per scope
    /// (the library's <c>Scope.ContainerView</c>) or taken from an execution state (<see cref="Of(ExecutionState)"/>).
    /// The default view is the root's: its reads through an environment the caller holds
    /// (<see cref="TryGetContainer{TContainer}(ContainerEnv{TContainer}, uint, out TContainer)"/>,
    /// <see cref="TryGetId{TContainer}(ContainerEnv{TContainer}, in TContainer, out uint)"/>) cost one null test over the
    /// environment's own, and the members that find a type's environment themselves need a view made with the root's
    /// environments.
    /// </para>
    /// <para>
    /// Containers of values live only in the root's environments, under the same id in every context, so any view reads
    /// them alike, and so does the view of a child whose chain holds no layer. In DEBUG, a read of a container of classes
    /// through the root's view while the thread runs an engine call of a child whose chain holds a layer
    /// (<see cref="EnterEngineCall"/>) asserts: that call should have read through the child's view.
    /// </para>
    /// </remarks>
    internal readonly struct ContainerView
    {

#if DEBUG
        /// <summary>
        /// The depth of engine calls the thread runs (<see cref="EnterEngineCall"/>). DEBUG builds only.
        /// </summary>
        [ThreadStatic]
        static int engineCalls;

        /// <summary>
        /// The layers that decide what the outermost engine call the thread runs reads (<see cref="Resolve"/>), or
        /// <c>null</c> when it reads as the root does. DEBUG builds only.
        /// </summary>
        [ThreadStatic]
        static ContainerLayers? callLayers;
#endif

        readonly ContainerValues? values;
        readonly ContainerLayers? layers;

        /// <summary>
        /// Initializes a view.
        /// </summary>
        /// <param name="values">The root's container environments.</param>
        /// <param name="layers">The context's container layers; <c>null</c> at the root.</param>
        public ContainerView(ContainerValues values, ContainerLayers? layers)
        {
            ArgumentNullException.ThrowIfNull(values);
            this.values = values;
            this.layers = layers;
        }

        /// <summary>
        /// Gets the view of the context <paramref name="database"/> belongs to: its layers over the root's environments,
        /// which every context's database shares; the root's view for the root, and for a database of no context.
        /// </summary>
        /// <param name="database">The database.</param>
        /// <returns>The view.</returns>
        /// <exception cref="InvalidOperationException">The database is rebuilding its containers.</exception>
        public static ContainerView Of(Database database)
        {
            ArgumentNullException.ThrowIfNull(database);
            return new ContainerView(database.Containers, database.Owner?.ContainerLayers);
        }

        /// <summary>
        /// Gets the view of the context whose database <paramref name="state"/> runs against
        /// (<see cref="ExecutionState.Containers"/>), which for a query on a frozen scope is the transient child the query
        /// runs in: what rule actions and external functions, a write primitive, a primitive over the tables and the
        /// rendering of a failed call's bindings read containers through. A pure primitive over classes, in a rule or a
        /// merge, a filter and a rule callback read through the view of the scope that owns the call instead, the frozen
        /// scope for such a query, which reads alike since a transient child holds no container layer
        /// (<see cref="AgreesWith(ExecutionState)"/>, asserted in DEBUG).
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <returns>The view.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database, or the database is
        /// rebuilding its containers.</exception>
        public static ContainerView Of(ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            return state.Containers;
        }

        /// <summary>
        /// Gets the root's container environments, which every view reads below its layers; <c>null</c> for the default
        /// view.
        /// </summary>
        public ContainerValues? Values => values;

        /// <summary>
        /// Gets the context's container layers, or <c>null</c> for the root's view.
        /// </summary>
        public ContainerLayers? Layers => layers;

        /// <summary>
        /// Gets whether this is the root's view.
        /// </summary>
        public bool IsRoot => layers is null;

        /// <summary>
        /// Gets the container with id <paramref name="id"/> of the type <paramref name="root"/> holds, in the view: at the
        /// root, <paramref name="root"/>'s own lookup after one null test; in a child, the nearest level whose layer
        /// owns the id, else the root (<see cref="ContainerLayers.TryGetContainer{TContainer}"/>). Thread-safe.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="id">The container id.</param>
        /// <param name="container">The container, or <c>default</c>.</param>
        /// <returns><c>false</c> if no container of the type has the id in the view.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetContainer<TContainer>(ContainerEnv<TContainer> root, uint id, out TContainer container)
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (layers is null)
            {
                var found = root.TryGetContainer(id, out container);
                AssertRootRead(found && container.HoldsIds);
                return found;
            }

            return layers.TryGetContainer(root, id, out container);
        }

        /// <summary>
        /// Gets the container of type <typeparamref name="TContainer"/> with id <paramref name="id"/> in the view: egglog's
        /// <c>get_val</c> (core-relations/src/containers/mod.rs:145-152) through the view, the type's environment found
        /// in the root's.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="id">The container id.</param>
        /// <param name="container">The container, or <c>default</c>.</param>
        /// <returns><c>false</c> if the type is not registered or no container of it has the id in the view.</returns>
        /// <exception cref="InvalidOperationException">The view is the default one, made without the root's
        /// environments.</exception>
        public bool TryGetContainer<TContainer>(uint id, out TContainer container)
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (RequireValues().TryGetEnvironment<TContainer>(out var env))
                return TryGetContainer(env, id, out container);

            container = default;
            return false;
        }

        /// <summary>
        /// Gets the id of <paramref name="container"/> in the view without interning it: at the root,
        /// <paramref name="root"/>'s own lookup after one null test; in a child, the content hashed once and looked up
        /// level by level, a hit hidden where a level above it shadows its id
        /// (<see cref="ContainerLayers.TryGetId{TContainer}"/>). Thread-safe.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="container">The container.</param>
        /// <param name="id">Its id, or zero.</param>
        /// <returns><c>true</c> if the view holds the container.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetId<TContainer>(ContainerEnv<TContainer> root, in TContainer container, out uint id)
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (layers is null)
            {
                AssertRootRead(container.HoldsIds);
                return root.TryGetId(container, out id);
            }

            return layers.TryGetId(root, container, container.ComputeHash(), out id);
        }

        /// <summary>
        /// Gets the id of <paramref name="container"/> in the view without interning it, as
        /// <see cref="TryGetId{TContainer}(ContainerEnv{TContainer}, in TContainer, out uint)"/>, the type's environment
        /// found in the root's.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="container">The container.</param>
        /// <param name="id">Its id, or zero.</param>
        /// <returns><c>true</c> if the type is registered and the view holds the container.</returns>
        /// <exception cref="InvalidOperationException">The view is the default one.</exception>
        public bool TryGetId<TContainer>(in TContainer container, out uint id)
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (RequireValues().TryGetEnvironment<TContainer>(out var env))
                return TryGetId(env, container, out id);

            id = 0;
            return false;
        }

        /// <summary>
        /// Returns the id of <paramref name="container"/>, interning it if it is new: egglog's <c>register_val</c>
        /// (core-relations/src/containers/mod.rs:154-163), which rule actions and external functions call through
        /// their execution state. It interns in the root's environment of the type
        /// (<see cref="ContainerEnv{TContainer}.GetOrInsert"/>), from any context: a container of values everywhere, and
        /// one of classes at a writable root; a child, and a frozen context, are refused one of classes there.
        /// Thread-safe.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="container">The container; it must not change afterwards.</param>
        /// <param name="state">The execution state, which mints a fresh id.</param>
        /// <returns>The container's id.</returns>
        /// <exception cref="InvalidOperationException">The view is the default one, the type is not registered, or
        /// the environment refuses the container (<see cref="ContainerEnv{TContainer}.GetOrInsert"/>).</exception>
        public uint GetOrInsert<TContainer>(in TContainer container, ExecutionState state)
            where TContainer : struct, IContainerValue<TContainer>
        {
            return RequireValues().GetEnvironment<TContainer>().GetOrInsert(container, state);
        }

        /// <summary>
        /// Gets whether <paramref name="id"/> is a container's id in the view, of any registered type: what tells a
        /// container id from a class's. O(types) probes at the root; in a child, O(types × depth) lock-free tests more
        /// (<see cref="ContainerLayers.IsContainerId"/>). Thread-safe.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if a container has the id.</returns>
        /// <exception cref="InvalidOperationException">The view is the default one.</exception>
        public bool IsContainerId(uint id)
        {
            var all = RequireValues();
            if (layers is not null)
                return layers.IsContainerId(all, id);

            var count = all.TypeCount;
            for (var i = 0; i < count; i++)
                if (all.GetEnvironment(ContainerTypeId.FromIndex(i)).ContainsId(id))
                    return true;

            return false;
        }

        /// <summary>
        /// Gets whether this view reads every container as the view of <paramref name="state"/>
        /// (<see cref="Of(ExecutionState)"/>) does: the same layers, or a context with no layer over the other's, as a
        /// transient child's for a query over the frozen scope that owns it. The state's layers are found through its
        /// layering rather than its containers, so that it answers while the state's database rebuilds its containers.
        /// For DEBUG assertions that an engine call reading through its scope's view reads as its execution state does.
        /// </summary>
        /// <param name="state">The engine call's execution state.</param>
        /// <returns>Whether they agree.</returns>
        public bool AgreesWith(ExecutionState state) => Resolve(layers) == Resolve(state.Layers?.Owner.ContainerLayers);

        /// <summary>
        /// Marks the thread as running an engine call of <paramref name="state"/>'s context, a rule's primitive, filter
        /// or callback, or a merge: while the outermost such call runs in a child whose chain holds a layer, a read of a
        /// container of classes through the root's view asserts in DEBUG. Pair with <see cref="ExitEngineCall"/> in a
        /// <c>finally</c>. Nothing in a release build.
        /// </summary>
        /// <param name="state">The engine call's execution state.</param>
        [Conditional("DEBUG")]
        public static void EnterEngineCall(ExecutionState state)
        {
#if DEBUG
            // The state's layering, not its containers: the call may run while its database rebuilds its containers.
            if (engineCalls++ == 0)
                callLayers = Resolve(state.Layers?.Owner.ContainerLayers);
#endif
        }

        /// <summary>
        /// Undoes one <see cref="EnterEngineCall"/>. Nothing in a release build.
        /// </summary>
        /// <param name="state">The engine call's execution state.</param>
        [Conditional("DEBUG")]
        public static void ExitEngineCall(ExecutionState state)
        {
#if DEBUG
            if (--engineCalls == 0)
                callLayers = null;
#endif
        }

        /// <summary>
        /// Gets the layers that decide every read through a view of <paramref name="layers"/>: those layers when they
        /// hold a layer of some type, else the nearest ancestor's that do, else none, as for the root.
        /// </summary>
        static ContainerLayers? Resolve(ContainerLayers? layers)
        {
            while (layers is { IsEmpty: true })
                layers = layers.Parent;

            return layers;
        }

        /// <summary>
        /// Asserts that a read through the root's view that met a container of classes (<paramref name="classes"/>) does
        /// not serve an engine call whose own view would read otherwise. Nothing in a release build.
        /// </summary>
        [Conditional("DEBUG")]
        static void AssertRootRead(bool classes)
        {
#if DEBUG
            Debug.Assert(classes == false || callLayers is null, "a container of classes was read through the root's view in an engine call of a child that holds containers; it reads through the child's view");
#endif
        }

        /// <summary>
        /// Gets the root's environments, which the default view lacks.
        /// </summary>
        ContainerValues RequireValues() => values ?? ThrowNoValues();

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ContainerValues ThrowNoValues()
        {
            throw new InvalidOperationException("The default container view reads only through an environment it is given; take a context's view, which knows the root's environments.");
        }

    }

}
