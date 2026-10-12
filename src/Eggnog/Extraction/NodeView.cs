using System;

namespace Eggnog
{

    /// <summary>
    /// One row of a constructor, as a <see cref="NodeCostFunction"/> sees it: valid only during the call.
    /// </summary>
    public readonly ref struct NodeView
    {

        readonly Relations.FunctionRow row;
        readonly Scope scope;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="head">The constructor.</param>
        /// <param name="row">The engine row: keys then output.</param>
        /// <param name="scope">The extractor's scope, whose origin classes carry.</param>
        internal NodeView(ConstructorHandle head, Relations.FunctionRow row, Scope scope)
        {
            Head = head;
            this.row = row;
            this.scope = scope;
        }

        /// <summary>
        /// Gets the constructor.
        /// </summary>
        public ConstructorHandle Head { get; }

        /// <summary>
        /// Gets the number of key columns.
        /// </summary>
        public int Arity => Head.Keys.Count;

        /// <summary>
        /// Gets a key column, decoded; for a sort column, the class (with the extractor scope's origin).
        /// </summary>
        /// <typeparam name="T">The column's type.</typeparam>
        /// <param name="column">The column, from 0.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="column"/> is not a key column.</exception>
        /// <exception cref="ArgumentException"><typeparamref name="T"/> is not the column's type.</exception>
        public T Get<T>(int column)
        {
            CheckColumn(column);
            if (Head.KeyArray[column] is not ColumnType<T> type)
                throw new ArgumentException($"Column {column} of {Head.Name} is a {Head.KeyArray[column].Name}, not a {ValueStore.DisplayName(typeof(T))}.", nameof(T));

            return type.Decode(row.Values[column].Rep, scope);
        }

        /// <summary>
        /// Gets a key column, decoded; for a sort column, the class.
        /// </summary>
        /// <param name="column">The column, from 0.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="column"/> is not a key column.</exception>
        public object Get(int column)
        {
            CheckColumn(column);
            return Head.KeyArray[column].DecodeObject(row.Values[column].Rep, scope);
        }

        /// <summary>
        /// Checks a column index.
        /// </summary>
        void CheckColumn(int column)
        {
            if ((uint)column >= (uint)Head.Keys.Count)
                throw new ArgumentOutOfRangeException(nameof(column), column, $"{Head.Name} has {Head.Keys.Count} key columns.");
        }

    }

}
