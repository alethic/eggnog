using System;

namespace Eggnog
{

    /// <summary>
    /// The tables a primitive's call site offers it, egglog's state wrappers (src/exec_state.rs): a read primitive reads
    /// through it, a write primitive writes, and a full one does both, each where its call site's context
    /// (<see cref="Context"/>) allows it. A definition refuses a primitive its call site's context does not allow
    /// (<see cref="Primitive.Allows"/>), and each read and write is checked again here against both, so a read primitive
    /// that casts its tables to <see cref="ITableWriter"/> writes nothing, as egglog's read state has no write methods.
    /// Valid only during the call.
    /// </summary>
    internal abstract class PrimitiveTables : ITables
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scope">The scope the call runs in.</param>
        /// <param name="context">The call site's context.</param>
        /// <param name="primitive">The primitive called.</param>
        protected PrimitiveTables(Scope scope, PrimitiveAccess context, Primitive primitive)
        {
            Scope = scope;
            Context = context;
            Gate = primitive;
        }

        /// <summary>
        /// Gets or sets the primitive whose access the reads and writes are checked against: the primitive called, or
        /// the one a function value it applies runs (<see cref="FnApply"/>).
        /// </summary>
        public Primitive Gate { get; set; }

        /// <summary>
        /// Gets the scope the call runs in: a class argument has its origin, and a class result must be usable there.
        /// </summary>
        public Scope Scope { get; }

        /// <summary>
        /// Gets the call site's context: what its primitives may do to the tables.
        /// </summary>
        public PrimitiveAccess Context { get; }

        /// <summary>
        /// Gets the first read or write refused, which fails the call whether or not the primitive caught it.
        /// </summary>
        public abstract Exception? Refusal { get; }

        /// <summary>
        /// Encodes a primitive's result where the call runs, a class checked usable there: in a rule or a read, its
        /// scope's (<see cref="ColumnType{T}.EncodeIn"/>).
        /// </summary>
        /// <typeparam name="T">The result's type.</typeparam>
        /// <param name="type">The result's column type.</param>
        /// <param name="value">The result.</param>
        /// <returns>The cell.</returns>
        public virtual uint EncodeResult<T>(ColumnType<T> type, T value) => type.EncodeIn(value, Scope);

        /// <summary>
        /// Reads the output of a function's row at keys' cells, as a function value's application does where the context
        /// reads (<see cref="FnApply"/>): the class canonical.
        /// </summary>
        /// <param name="table">The function.</param>
        /// <param name="keys">The keys' cells.</param>
        /// <param name="value">The output's cell, when there is a row.</param>
        /// <returns>Whether there is a row.</returns>
        public abstract bool TryLookupCell(FunctionHandle table, ReadOnlySpan<uint> keys, out uint value);

        /// <summary>
        /// Reads the output of a row at keys as <see cref="TryLookupCell"/> does, over values: for a host read.
        /// </summary>
        /// <param name="table">The function.</param>
        /// <param name="keys">The keys.</param>
        /// <param name="value">The output, when there is a row.</param>
        /// <returns>Whether there is a row.</returns>
        public abstract bool TryLookupObject(FunctionHandle table, ReadOnlySpan<object> keys, out object? value);

        /// <summary>
        /// Gets or makes the class of a constructor's term at keys' cells, as a function value's application does where
        /// the context writes (<see cref="FnApply"/>).
        /// </summary>
        /// <param name="constructor">The constructor.</param>
        /// <param name="keys">The keys' cells.</param>
        /// <returns>The class's cell.</returns>
        public abstract uint MakeCell(ConstructorHandle constructor, ReadOnlySpan<uint> keys);

        /// <summary>
        /// Unions two class cells where the call runs, as egglog's write state does for a write primitive a function
        /// value applies.
        /// </summary>
        /// <param name="a">One class's cell.</param>
        /// <param name="b">The other's.</param>
        public abstract void UnionCells(uint a, uint b);

        /// <inheritdoc />
        public bool TryLookup<T>(TableHandle table, ReadOnlySpan<object> keys, out T value)
        {
            Reads();
            return TryLookupCore(table, keys, out value);
        }

        /// <inheritdoc />
        public bool Contains(RelationHandle relation, params ReadOnlySpan<object> keys)
        {
            Reads();
            return ContainsCore(relation, keys);
        }

        /// <inheritdoc />
        public long TableSize(TableHandle table)
        {
            Reads();
            return TableSizeCore(table);
        }

        /// <inheritdoc />
        public void Insert(RelationHandle relation, params ReadOnlySpan<object> keys)
        {
            Writes();
            InsertCore(relation, keys);
        }

        /// <inheritdoc />
        public void Set(FunctionHandle function, ReadOnlySpan<object> keys, object value)
        {
            Writes();
            SetCore(function, keys, value);
        }

        /// <inheritdoc />
        public TSort Make<TSort>(ConstructorHandle constructor, params ReadOnlySpan<object> keys) where TSort : struct, ISort<TSort>
        {
            Writes();
            return MakeCore<TSort>(constructor, keys);
        }

        /// <inheritdoc />
        public void Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>
        {
            Writes();
            UnionCore(a, b);
        }

        /// <inheritdoc />
        public void Remove(TableHandle table, params ReadOnlySpan<object> keys)
        {
            Writes();
            RemoveCore(table, keys);
        }

        /// <inheritdoc />
        public void Subsume(TableHandle table, params ReadOnlySpan<object> keys)
        {
            Writes();
            SubsumeCore(table, keys);
        }

        /// <summary>
        /// Reads a value, the context allowing reads.
        /// </summary>
        protected abstract bool TryLookupCore<T>(TableHandle table, ReadOnlySpan<object> keys, out T value);

        /// <summary>
        /// Reads whether a relation holds a row, the context allowing reads.
        /// </summary>
        protected abstract bool ContainsCore(RelationHandle relation, ReadOnlySpan<object> keys);

        /// <summary>
        /// Reads a table's size, the context allowing reads.
        /// </summary>
        protected abstract long TableSizeCore(TableHandle table);

        /// <summary>
        /// Inserts a row, the context allowing writes.
        /// </summary>
        protected abstract void InsertCore(RelationHandle relation, ReadOnlySpan<object> keys);

        /// <summary>
        /// Sets a function's value, the context allowing writes.
        /// </summary>
        protected abstract void SetCore(FunctionHandle function, ReadOnlySpan<object> keys, object value);

        /// <summary>
        /// Gets or makes a term's class, the context allowing writes.
        /// </summary>
        protected abstract TSort MakeCore<TSort>(ConstructorHandle constructor, ReadOnlySpan<object> keys) where TSort : struct, ISort<TSort>;

        /// <summary>
        /// Unions two classes, the context allowing writes.
        /// </summary>
        protected abstract void UnionCore<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>;

        /// <summary>
        /// Deletes a row, the context allowing writes.
        /// </summary>
        protected abstract void RemoveCore(TableHandle table, ReadOnlySpan<object> keys);

        /// <summary>
        /// Subsumes a row, the context allowing writes.
        /// </summary>
        protected abstract void SubsumeCore(TableHandle table, ReadOnlySpan<object> keys);

        /// <summary>
        /// Records a refusal, the first one only, and returns it to throw.
        /// </summary>
        protected abstract Exception Refuse(Exception exception);

        /// <summary>
        /// Checks that the primitive and its call site's context allow reads.
        /// </summary>
        /// <exception cref="InvalidOperationException">One does not.</exception>
        void Reads()
        {
            if (Gate.Access is not (PrimitiveAccess.Read or PrimitiveAccess.Full))
                throw Refuse(new InvalidOperationException(
                    $"Primitive '{Gate.Name}' read a table, but it only writes them (Primitive.CreateWrite), as egglog's write state reads none."));

            if (Context is not (PrimitiveAccess.Read or PrimitiveAccess.Full))
                throw Refuse(new InvalidOperationException(
                    $"Primitive '{Gate.Name}' read a table where its call site reads none, as egglog's pure and write contexts read none."));
        }

        /// <summary>
        /// Checks that the primitive and its call site's context allow writes.
        /// </summary>
        /// <exception cref="InvalidOperationException">One does not.</exception>
        void Writes()
        {
            if (Gate.Access is not (PrimitiveAccess.Write or PrimitiveAccess.Full))
                throw Refuse(new InvalidOperationException(
                    $"Primitive '{Gate.Name}' wrote a table, but it only reads them (Primitive.CreateRead), as egglog's read state writes none."));

            if (Context is not (PrimitiveAccess.Write or PrimitiveAccess.Full))
                throw Refuse(new InvalidOperationException(
                    $"Primitive '{Gate.Name}' wrote a table where its call site writes none, as egglog's pure and read contexts write none."));
        }

    }

}
