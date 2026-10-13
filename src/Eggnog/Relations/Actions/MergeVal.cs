namespace Eggnog.Relations
{

    /// <summary>
    /// A value written into a row that an execution state predicts for a missing key: egglog's <c>MergeVal</c>
    /// (core-relations/src/action/mod.rs:84-103), taken by <see cref="ExecutionState.PredictRow(TableId, System.ReadOnlySpan{uint}, System.ReadOnlySpan{MergeVal}, System.Span{uint})"/>
    /// and <see cref="ExecutionState.PredictColumn(TableId, System.ReadOnlySpan{uint}, System.ReadOnlySpan{MergeVal}, ColumnId)"/>.
    /// </summary>
    internal readonly record struct MergeVal
    {

        MergeVal(bool isCounter, CounterId counter, Value constant)
        {
            IsCounter = isCounter;
            Counter = counter;
            Constant = constant;
        }

        /// <summary>
        /// Gets whether the value is fresh: the previous value of <see cref="Counter"/>, which is incremented.
        /// Otherwise it is <see cref="Constant"/>.
        /// </summary>
        public bool IsCounter { get; }

        /// <summary>
        /// Gets the counter of a fresh value.
        /// </summary>
        public CounterId Counter { get; }

        /// <summary>
        /// Gets the constant of a constant value.
        /// </summary>
        public Value Constant { get; }

        /// <summary>
        /// Creates a fresh value from <paramref name="counter"/>. egglog's <c>MergeVal::Counter</c>.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value.</returns>
        public static MergeVal FromCounter(CounterId counter) => new(true, counter, default);

        /// <summary>
        /// Creates a constant value. egglog's <c>MergeVal::Constant</c>.
        /// </summary>
        /// <param name="constant">The constant.</param>
        /// <returns>The value.</returns>
        public static MergeVal FromConstant(Value constant) => new(false, default, constant);

        /// <summary>
        /// Converts a counter to a fresh value.
        /// </summary>
        /// <param name="counter">The counter.</param>
        public static implicit operator MergeVal(CounterId counter) => FromCounter(counter);

        /// <summary>
        /// Converts a constant to a constant value.
        /// </summary>
        /// <param name="constant">The constant.</param>
        public static implicit operator MergeVal(Value constant) => FromConstant(constant);

        /// <inheritdoc />
        public override string ToString() => IsCounter ? "fresh(" + Counter + ")" : Constant.ToString();

    }

}
