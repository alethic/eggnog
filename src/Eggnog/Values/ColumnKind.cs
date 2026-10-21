namespace Eggnog
{

    /// <summary>
    /// What a column's cells are: classes of a sort, or base values.
    /// </summary>
    public enum ColumnKind
    {

        /// <summary>
        /// The cells are e-classes of a sort (<see cref="EGraph.DefineSort{TSort}(string)"/>); equalities rewrite them.
        /// </summary>
        Sort,

        /// <summary>
        /// The cells are values: base values (numbers, strings, sets, registered types) or containers of values (pairs);
        /// equal values have equal cells.
        /// </summary>
        Value,

    }

}
