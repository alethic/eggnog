namespace Eggnog.Relations
{

    /// <summary>
    /// The concrete class of a <see cref="Table"/>. The C# replacement for egglog's <c>as_any</c> downcast and
    /// <c>WrapperImpl&lt;T&gt;</c> (core-relations/src/table_spec.rs:384-545): hot paths switch on the kind once per
    /// call and then run the monomorphic, non-virtual code of the sealed class.
    /// </summary>
    internal enum TableKind : byte
    {

        /// <summary>
        /// A <see cref="SortedWritesTable"/>: a keyed table with a merge function, the table of every egglog function.
        /// </summary>
        SortedWrites,

        /// <summary>
        /// A <see cref="DisplacedTable"/>: the union-find exposed as a table.
        /// </summary>
        Displaced,

        /// <summary>
        /// An <see cref="OverlayDisplacedTable"/>: a child context's union-find, its overlay and displaced journal over
        /// its parent's.
        /// </summary>
        OverlayDisplaced,

    }

}
