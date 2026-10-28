using System;

namespace Eggnog
{

    /// <summary>
    /// The tables of a primitive a host read computes, in egglog's read context of a <c>check</c>: it reads the scope's
    /// view, as the read does, and writes nothing. A key no write interned, or a container of classes no write made, is in
    /// no row, since a read never interns.
    /// </summary>
    internal sealed class ReaderPrimitiveTables : PrimitiveTables
    {

        Exception? refusal;
        bool done;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scope">The scope read.</param>
        /// <param name="primitive">The primitive called.</param>
        public ReaderPrimitiveTables(Scope scope, Primitive primitive)
            : base(scope, PrimitiveAccess.Read, primitive)
        {
        }

        /// <inheritdoc />
        public override Exception? Refusal => refusal;

        /// <summary>
        /// Ends the call: the tables answer nothing after.
        /// </summary>
        public void End() => done = true;

        /// <inheritdoc />
        public override bool TryLookupCell(FunctionHandle table, ReadOnlySpan<uint> keys, out uint value)
        {
            Check(table);
            if (Scope.Engine.TryLookup(table.Function, keys.ToArray(), out value) == false)
                return false;

            if (table.Columns[^1].InvolvesClasses)
                value = Scope.Engine.Find(value);

            return true;
        }

        /// <inheritdoc />
        public override bool TryLookupObject(FunctionHandle table, ReadOnlySpan<object> keys, out object? value)
        {
            value = null;
            Check(table);
            if (TryKeys(table, keys, out var cells) == false || Scope.Engine.TryLookup(table.Function, cells, out var cell) == false)
                return false;

            var output = table.Columns[^1];
            value = output.DecodeObject(output.InvolvesClasses ? Scope.Engine.Find(cell) : cell, Scope);
            return true;
        }

        /// <inheritdoc />
        public override uint MakeCell(ConstructorHandle constructor, ReadOnlySpan<uint> keys) => throw WritesNothing();

        /// <inheritdoc />
        public override void UnionCells(uint a, uint b) => throw WritesNothing();

        /// <inheritdoc />
        protected override bool TryLookupCore<T>(TableHandle table, ReadOnlySpan<object> keys, out T value)
        {
            value = default!;
            Check(table);
            if (table.IsRelation)
                throw Refuse(new ArgumentException($"'{table.Name}' is a relation, which has no value to read; use Contains.", nameof(table)));

            var output = table.Output!;
            if (output.ClrType != typeof(T))
                throw Refuse(new ArgumentException($"'{table.Name}' outputs {output.Name}, not {typeof(T).Name}.", nameof(table)));

            if (TryKeys(table, keys, out var cells) == false || Scope.Engine.TryLookup(table.Function, cells, out var cell) == false)
                return false;

            value = (T)output.DecodeObject(output.InvolvesClasses ? Scope.Engine.Find(cell) : cell, Scope);
            return true;
        }

        /// <inheritdoc />
        protected override bool ContainsCore(RelationHandle relation, ReadOnlySpan<object> keys)
        {
            Check(relation);
            return TryKeys(relation, keys, out var cells) && Scope.Engine.TryLookup(relation.Function, cells, out _);
        }

        /// <inheritdoc />
        protected override long TableSizeCore(TableHandle table)
        {
            Check(table);
            return Scope.Engine.TableSize(table.Function);
        }

        /// <inheritdoc />
        protected override void InsertCore(RelationHandle relation, ReadOnlySpan<object> keys) => throw WritesNothing();

        /// <inheritdoc />
        protected override void SetCore(FunctionHandle function, ReadOnlySpan<object> keys, object value) => throw WritesNothing();

        /// <inheritdoc />
        protected override TSort MakeCore<TSort>(ConstructorHandle constructor, ReadOnlySpan<object> keys) => throw WritesNothing();

        /// <inheritdoc />
        protected override void UnionCore<TSort>(TSort a, TSort b) => throw WritesNothing();

        /// <inheritdoc />
        protected override void RemoveCore(TableHandle table, ReadOnlySpan<object> keys) => throw WritesNothing();

        /// <inheritdoc />
        protected override void SubsumeCore(TableHandle table, ReadOnlySpan<object> keys) => throw WritesNothing();

        /// <inheritdoc />
        protected override Exception Refuse(Exception exception)
        {
            refusal ??= exception;
            return exception;
        }

        /// <summary>
        /// Gets the cells of a read's keys without interning one: <c>false</c> when a value was never interned or a
        /// container of classes was never made.
        /// </summary>
        bool TryKeys(TableHandle table, ReadOnlySpan<object> keys, out uint[] cells)
        {
            var columns = table.KeyArray;
            if (keys.Length != columns.Length)
                throw Refuse(new ArgumentException($"'{table.Name}' takes {columns.Length} keys, not {keys.Length}.", nameof(keys)));

            cells = new uint[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                var key = keys[i] ?? throw Refuse(new ArgumentNullException(nameof(keys), $"Key {i} is null."));
                var type = columns[i];
                if (type.ClrType.IsInstanceOfType(key) == false)
                    throw Refuse(new ArgumentException($"A {ValueStore.DisplayName(key.GetType())} is not a {type.Name}.", nameof(keys)));

                try
                {
                    if (type.Kind == ColumnKind.Sort)
                        cells[i] = Scope.Engine.Find(ClassChecks.Require(Scope, type.ClassOf(key), nameof(keys)));
                    else if (type.HoldsClasses)
                    {
                        if (type.TryEncodeCanonicalObject(key, Scope, Scope.Engine.Find, out cells[i]) == false)
                            return false;
                    }
                    else if (type.TryEncodeExistingObject(key, View, out cells[i]) == false)
                    {
                        return false;
                    }
                }
                catch (ArgumentException refused)
                {
                    throw Refuse(refused);
                }
            }

            return true;
        }

        /// <summary>
        /// Checks that the call has not returned and the table is the read's graph's.
        /// </summary>
        void Check(TableHandle table)
        {
            if (done)
                throw Refuse(new InvalidOperationException("A primitive's tables are valid only during the call."));

            ArgumentNullException.ThrowIfNull(table);
            if (ReferenceEquals(table.Graph, Scope.Graph) == false || table.Index < 0)
                throw Refuse(new ArgumentException($"Table '{table.Name}' belongs to another graph.", nameof(table)));

            if (table.ReadScope is not null)
                throw Refuse(new ArgumentException($"'{table.Name} @{table.ReadScope.Name}' is a scoped view, which a primitive does not use.", nameof(table)));
        }

        /// <summary>
        /// Gets the refusal of a write in a read, which the base refuses first by the read's context.
        /// </summary>
        Exception WritesNothing() => Refuse(new InvalidOperationException("A read writes nothing."));

    }

}
