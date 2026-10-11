using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The type of a function column as extraction sees it: an e-class sort (<see cref="EqSort"/>), a base value sort
    /// (<see cref="BaseSort{TCodec, T}"/>) or a container sort. The bridge's <see cref="ColumnTy"/> only says whether a
    /// column holds ids or base values; extraction also needs to know which constructors produce which ids and how to
    /// read and print values, which is what egglog's frontend <c>Sort</c> trait supplies (src/sort/mod.rs:47-143).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sort is identified by reference; its <see cref="Name"/> must be unique within an <see cref="ExtractionSchema"/>,
    /// as egglog keys sorts by name. The hierarchy is closed: only this assembly derives from it.
    /// </para>
    /// <para>
    /// Immutable and thread-safe.
    /// </para>
    /// </remarks>
    public abstract class Sort
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <param name="kind">What its values are.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        private protected Sort(string name, SortKind kind)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            Name = name;
            Kind = kind;
        }

        /// <summary>
        /// Gets the sort's name. egglog's <c>name</c>.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets what the sort's values are.
        /// </summary>
        public SortKind Kind { get; }

        /// <summary>
        /// Gets whether the values are e-class ids. egglog's <c>is_eq_sort</c>.
        /// </summary>
        public bool IsEqSort => Kind == SortKind.Eq;

        /// <summary>
        /// Gets whether the values are containers. egglog's <c>is_container_sort</c>.
        /// </summary>
        public bool IsContainerSort => Kind == SortKind.Container;

        /// <summary>
        /// Gets whether a column of type <paramref name="type"/> in <paramref name="egraph"/> can hold values of this sort:
        /// egglog's <c>column_ty</c>, checked rather than computed. E-class and container sorts live in id columns; a base
        /// sort lives in a column of its registered base type.
        /// </summary>
        /// <param name="egraph">The e-graph whose base value registry the column refers to.</param>
        /// <param name="type">The column type.</param>
        /// <returns>Whether the column fits.</returns>
        internal abstract bool FitsColumn(EGraph egraph, ColumnTy type);

        /// <summary>
        /// Compares two distinct values of this sort for extraction's tie-break (rule-layer decision D10): content order
        /// for base values, which <see cref="BaseSort{TCodec, T}"/> overrides, and id order for everything else. Never
        /// returns 0 for distinct values. Called only when two candidates tie on cost, function and earlier columns.
        /// </summary>
        /// <param name="baseValues">The registry the values were boxed in.</param>
        /// <param name="left">The first value.</param>
        /// <param name="right">The second value.</param>
        /// <returns>The sign of the comparison.</returns>
        internal virtual int CompareValues(BaseValues baseValues, Value left, Value right) => left.Rep.CompareTo(right.Rep);

        /// <summary>
        /// Adds a base value of this sort to <paramref name="dag"/>: egglog's <c>reconstruct_termdag_base</c>.
        /// </summary>
        /// <param name="baseValues">The registry the value was boxed in.</param>
        /// <param name="value">The value.</param>
        /// <param name="dag">The term DAG.</param>
        /// <returns>The term.</returns>
        /// <exception cref="InvalidOperationException">This is not a base sort.</exception>
        internal virtual TermId ReconstructBase(BaseValues baseValues, Value value, TermDag dag)
        {
            throw new InvalidOperationException($"Sort {Name} is not a base sort.");
        }

        /// <summary>
        /// Renders a value of this sort as text: a base sort's literal text, and the raw value for other sorts.
        /// </summary>
        /// <param name="baseValues">The registry the value was boxed in.</param>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        internal virtual string FormatValue(BaseValues baseValues, Value value) => value.ToString();

        /// <inheritdoc />
        public override string ToString() => Name;

    }

}
