namespace Eggnog.Relations
{

    /// <summary>
    /// An e-class sort: its values are e-class ids, produced by the constructors whose output it is. egglog's
    /// <c>EqSort</c> (src/sort/mod.rs:157-187). Its columns are <see cref="ColumnTy.Id"/> columns.
    /// </summary>
    /// <remarks>
    /// Immutable and thread-safe.
    /// </remarks>
    internal sealed class EqSort : Sort
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <exception cref="System.ArgumentException"><paramref name="name"/> is empty.</exception>
        public EqSort(string name)
            : base(name, SortKind.Eq)
        {

        }

        /// <inheritdoc />
        internal override bool FitsColumn(EGraph egraph, ColumnTy type) => type.IsId;

    }

}
