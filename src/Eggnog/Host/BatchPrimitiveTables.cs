using System;

namespace Eggnog
{

    /// <summary>
    /// The tables of a primitive a host write computes, in egglog's full context of a top-level action, over the write's
    /// batch (<see cref="HostBatch"/>): its reads see the batch's earlier facts, as egglog's next command sees the last
    /// one's, and not what the fact being planned stages, as egglog's lookup does not; it gets or makes a term's class at
    /// once, as egglog's lookup of a constructor in an action does; and its other writes and its unions are held back until
    /// the end of the fact, as egglog stages a command's writes, then planned as the facts they are, its deletions at the
    /// fact's write point, before the fact's own writes, as egglog's flush applies removals before inserts. A write's
    /// table is checked when it is made; the batch's other checks apply when it plans the write, as to any of its facts.
    /// </summary>
    internal sealed class BatchPrimitiveTables : PrimitiveTables
    {

        readonly HostBatch batch;
        Exception? refusal;
        bool done;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="batch">The write's batch.</param>
        /// <param name="scope">The scope written.</param>
        /// <param name="primitive">The primitive called.</param>
        public BatchPrimitiveTables(HostBatch batch, Scope scope, Primitive primitive)
            : base(scope, PrimitiveAccess.Full, primitive)
        {
            this.batch = batch;
        }

        /// <inheritdoc />
        public override Exception? Refusal => refusal;

        /// <summary>
        /// Ends the call: the tables answer nothing after.
        /// </summary>
        public void End() => done = true;

        /// <inheritdoc />
        /// <remarks>A class the batch made is usable in its later calls, so a primitive may return one.</remarks>
        public override uint EncodeResult<T>(ColumnType<T> type, T value) => batch.EncodeForPrimitive(type, value!);

        /// <inheritdoc />
        protected override bool TryLookupCore<T>(TableHandle table, ReadOnlySpan<object> keys, out T value)
        {
            Check();
            try
            {
                return batch.TryLookupForPrimitive(table, keys, out value);
            }
            catch (Exception refused) when (refused is ArgumentException or InvalidOperationException)
            {
                throw Refuse(refused);
            }
        }

        /// <inheritdoc />
        protected override bool ContainsCore(RelationHandle relation, ReadOnlySpan<object> keys)
        {
            Check();
            try
            {
                return batch.ContainsForPrimitive(relation, keys);
            }
            catch (Exception refused) when (refused is ArgumentException or InvalidOperationException)
            {
                throw Refuse(refused);
            }
        }

        /// <inheritdoc />
        protected override long TableSizeCore(TableHandle table)
        {
            Check();
            try
            {
                return batch.TableSizeForPrimitive(table);
            }
            catch (Exception refused) when (refused is ArgumentException or InvalidOperationException)
            {
                throw Refuse(refused);
            }
        }

        /// <inheritdoc />
        protected override void InsertCore(RelationHandle relation, ReadOnlySpan<object> keys)
        {
            Check();
            var arguments = ArgumentsOf(relation, keys);
            Run(() => batch.CheckWritableForPrimitive(relation));
            var fact = Refusing(() => (Fact)new RelationFact(relation, arguments));
            batch.HoldWrite(() => batch.PlanHeldFact(fact));
        }

        /// <inheritdoc />
        protected override void SetCore(FunctionHandle function, ReadOnlySpan<object> keys, object value)
        {
            Check();
            if (value is null)
                throw Refuse(new ArgumentNullException(nameof(value)));

            var arguments = ArgumentsOf(function, keys);
            Run(() => batch.CheckWritableForPrimitive(function));
            var fact = Refusing(() => (Fact)new EqualityFact(
                new UntypedApplicationTerm(function, arguments),
                BoxedConstantTerm.Convert([value], [function.Output!.ClrType], function.Name)[0]));
            batch.HoldWrite(() => batch.PlanHeldFact(fact));
        }

        /// <inheritdoc />
        protected override TSort MakeCore<TSort>(ConstructorHandle constructor, ReadOnlySpan<object> keys)
        {
            Check();
            try
            {
                return batch.MakeForPrimitive<TSort>(constructor, keys);
            }
            catch (Exception refused) when (refused is ArgumentException or InvalidOperationException)
            {
                throw Refuse(refused);
            }
        }

        /// <inheritdoc />
        protected override void UnionCore<TSort>(TSort a, TSort b)
        {
            Check();
            Run(() => batch.UnionForPrimitive(a, b));
        }

        /// <inheritdoc />
        protected override void RemoveCore(TableHandle table, ReadOnlySpan<object> keys)
        {
            Check();
            var arguments = ArgumentsOf(table, keys);
            Run(() => batch.CheckRemovableForPrimitive(table));
            batch.HoldRemove(table, arguments);
        }

        /// <inheritdoc />
        protected override void SubsumeCore(TableHandle table, ReadOnlySpan<object> keys)
        {
            Check();
            try
            {
                // egglog's subsume reads the row when it is called, before the fact's deletions apply.
                ArgumentNullException.ThrowIfNull(table);
                batch.HoldWrite(batch.SubsumeForPrimitive(table, keys));
            }
            catch (Exception refused) when (refused is ArgumentException or InvalidOperationException)
            {
                throw Refuse(refused);
            }
        }

        /// <inheritdoc />
        protected override Exception Refuse(Exception exception)
        {
            refusal ??= exception;
            return exception;
        }

        /// <summary>
        /// Converts the keys of a write to its terms, refusing a count or a type that does not match.
        /// </summary>
        Term[] ArgumentsOf(TableHandle table, ReadOnlySpan<object> keys)
        {
            try
            {
                if (ReferenceEquals(table.Graph, Scope.Graph) == false)
                    throw new ArgumentException($"Table '{table.Name}' belongs to another graph.", nameof(table));

                return table.ToArguments(keys);
            }
            catch (ArgumentException refused)
            {
                throw Refuse(refused);
            }
        }

        /// <summary>
        /// Runs an action, recording what it refuses.
        /// </summary>
        void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception refused) when (refused is ArgumentException or InvalidOperationException)
            {
                throw Refuse(refused);
            }
        }

        /// <summary>
        /// Gets a value, recording what it refuses.
        /// </summary>
        T Refusing<T>(Func<T> get)
        {
            try
            {
                return get();
            }
            catch (ArgumentException refused)
            {
                throw Refuse(refused);
            }
        }

        /// <summary>
        /// Checks that the call has not returned.
        /// </summary>
        void Check()
        {
            if (done)
                throw Refuse(new InvalidOperationException("A primitive's tables are valid only during the call."));
        }

    }

}
