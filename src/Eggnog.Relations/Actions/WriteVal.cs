namespace Eggnog.Relations
{

    /// <summary>
    /// A value an action writes into a row it builds: egglog's <c>WriteVal</c> (core-relations/src/action/mod.rs:58-82),
    /// the default of a value column in <see cref="LookupOrInsertDefaultInstr"/>.
    /// </summary>
    internal readonly record struct WriteVal
    {

        WriteVal(WriteValKind kind, QueryEntry entry, CounterId counter, int column)
        {
            Kind = kind;
            Entry = entry;
            Counter = counter;
            Column = column;
        }

        /// <summary>
        /// Gets the kind of value.
        /// </summary>
        public WriteValKind Kind { get; }

        /// <summary>
        /// Gets the variable or constant of a <see cref="WriteValKind.Entry"/> value.
        /// </summary>
        public QueryEntry Entry { get; }

        /// <summary>
        /// Gets the counter of a <see cref="WriteValKind.IncrementCounter"/> value.
        /// </summary>
        public CounterId Counter { get; }

        /// <summary>
        /// Gets the column of the row being built that a <see cref="WriteValKind.CurrentValue"/> value copies; it must
        /// come before the column being written.
        /// </summary>
        public int Column { get; }

        /// <summary>
        /// Creates a value read from a variable or a constant. egglog's <c>WriteVal::QueryEntry</c>.
        /// </summary>
        /// <param name="entry">The variable or constant.</param>
        /// <returns>The value.</returns>
        public static WriteVal Of(QueryEntry entry) => new(WriteValKind.Entry, entry, default, 0);

        /// <summary>
        /// Creates a fresh value from <paramref name="counter"/>. egglog's <c>WriteVal::IncCounter</c>.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value.</returns>
        public static WriteVal FromCounter(CounterId counter) => new(WriteValKind.IncrementCounter, default, counter, 0);

        /// <summary>
        /// Creates a copy of column <paramref name="column"/> of the row being built. egglog's <c>WriteVal::CurrentVal</c>.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <returns>The value.</returns>
        public static WriteVal CurrentValue(int column) => new(WriteValKind.CurrentValue, default, default, column);

        /// <summary>
        /// Converts an operand to a value.
        /// </summary>
        /// <param name="entry">The operand.</param>
        public static implicit operator WriteVal(QueryEntry entry) => Of(entry);

        /// <summary>
        /// Converts a variable to a value.
        /// </summary>
        /// <param name="variable">The variable.</param>
        public static implicit operator WriteVal(Variable variable) => Of(QueryEntry.Var(variable));

        /// <summary>
        /// Converts a constant to a value.
        /// </summary>
        /// <param name="constant">The constant.</param>
        public static implicit operator WriteVal(Value constant) => Of(QueryEntry.Const(constant));

        /// <summary>
        /// Converts a counter to a fresh value.
        /// </summary>
        /// <param name="counter">The counter.</param>
        public static implicit operator WriteVal(CounterId counter) => FromCounter(counter);

        /// <inheritdoc />
        public override string ToString() => Kind switch
        {
            WriteValKind.Entry => Entry.ToString(),
            WriteValKind.IncrementCounter => "fresh(" + Counter + ")",
            _ => "row[" + Column + "]",
        };

    }

}
