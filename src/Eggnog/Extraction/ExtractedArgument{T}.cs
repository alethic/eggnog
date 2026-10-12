namespace Eggnog
{

    /// <summary>
    /// One argument of an extracted node: a decoded value, or for a sort column the class and its extracted subterm.
    /// </summary>
    /// <typeparam name="T">The argument's type.</typeparam>
    public readonly struct ExtractedArgument<T>
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="value">The value, or the class.</param>
        /// <param name="term">The subterm of a sort column; <c>null</c> for a value column.</param>
        internal ExtractedArgument(T value, ExtractedTerm? term)
        {
            Value = value;
            Term = term;
        }

        /// <summary>
        /// Gets the value, or for a sort column the class.
        /// </summary>
        public T Value { get; }

        /// <summary>
        /// Gets the subterm of a sort column; <c>null</c> for a value column.
        /// </summary>
        public ExtractedTerm? Term { get; }

        /// <summary>
        /// Renders the argument: the subterm, or the value.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => Term?.ToString() ?? Value?.ToString() ?? string.Empty;

    }

}
