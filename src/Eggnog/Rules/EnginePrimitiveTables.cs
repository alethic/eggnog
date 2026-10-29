using System;
using System.Collections.Concurrent;

namespace Eggnog
{

    /// <summary>
    /// The tables of a primitive an engine call runs, in a rule's body or head or in a merge, over the call's
    /// <c>ExecutionState</c> (<see cref="EngineTables"/>): its reads see what the run sees, and its writes are staged with
    /// the iteration's other writes, as egglog's state wrappers give a primitive the run's own state.
    /// </summary>
    internal sealed class EnginePrimitiveTables : PrimitiveTables
    {

        readonly EngineTables tables;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scope">The scope the call runs in.</param>
        /// <param name="state">The engine's state for the call.</param>
        /// <param name="context">The call site's context.</param>
        /// <param name="primitive">The primitive called.</param>
        /// <param name="actions">The table actions of the call site, made once per table.</param>
        /// <param name="canonical">Whether classes are read and written canonical through the state; not for a merge's
        /// primitive, which takes them as they are, as egglog's does.</param>
        public EnginePrimitiveTables(Scope scope, Relations.ExecutionState state, PrimitiveAccess context, Primitive primitive,
            ConcurrentDictionary<Relations.FunctionId, Relations.TableAction> actions, bool canonical = true)
            : base(scope, context, primitive)
        {
            tables = new EngineTables(scope, state, actions, "A primitive", canonical);
            ScopeRuntime.AssertReadsAsState(scope, state);
        }

        /// <inheritdoc />
        public override Exception? Refusal => tables.Refusal;

        /// <inheritdoc />
        /// <remarks>The view of the context the call's execution state runs in
        /// (<see cref="Relations.ExecutionState.Containers"/>), which for a query on a frozen scope is the transient child
        /// the query runs in; taken from the state on each read, a few field loads, and refused once the call has
        /// returned.</remarks>
        public override Relations.ContainerView View => tables.State.Containers;

        /// <summary>
        /// Ends the call: the tables answer nothing after.
        /// </summary>
        public void End() => tables.End();

        /// <inheritdoc />
        public override bool TryLookupCell(FunctionHandle table, ReadOnlySpan<uint> keys, out uint value) => tables.TryLookupCell(table, keys, out value);

        /// <inheritdoc />
        public override bool TryLookupObject(FunctionHandle table, ReadOnlySpan<object> keys, out object? value) => tables.TryLookupObject(table, keys, out value);

        /// <inheritdoc />
        public override uint MakeCell(ConstructorHandle constructor, ReadOnlySpan<uint> keys) => tables.MakeCell(constructor, keys);

        /// <inheritdoc />
        public override void UnionCells(uint a, uint b) => tables.UnionCells(a, b);

        /// <inheritdoc />
        protected override bool TryLookupCore<T>(TableHandle table, ReadOnlySpan<object> keys, out T value) => tables.TryLookup(table, keys, out value);

        /// <inheritdoc />
        protected override bool ContainsCore(RelationHandle relation, ReadOnlySpan<object> keys) => tables.Contains(relation, keys);

        /// <inheritdoc />
        protected override long TableSizeCore(TableHandle table) => tables.TableSize(table);

        /// <inheritdoc />
        protected override void InsertCore(RelationHandle relation, ReadOnlySpan<object> keys) => tables.Insert(relation, keys);

        /// <inheritdoc />
        protected override void SetCore(FunctionHandle function, ReadOnlySpan<object> keys, object value) => tables.Set(function, keys, value);

        /// <inheritdoc />
        protected override TSort MakeCore<TSort>(ConstructorHandle constructor, ReadOnlySpan<object> keys) => tables.Make<TSort>(constructor, keys);

        /// <inheritdoc />
        protected override void UnionCore<TSort>(TSort a, TSort b) => tables.Union(a, b);

        /// <inheritdoc />
        protected override void RemoveCore(TableHandle table, ReadOnlySpan<object> keys) => tables.Remove(table, keys);

        /// <inheritdoc />
        protected override void SubsumeCore(TableHandle table, ReadOnlySpan<object> keys) => tables.Subsume(table, keys);

        /// <inheritdoc />
        protected override Exception Refuse(Exception exception) => tables.Refuse(exception);

    }

}
