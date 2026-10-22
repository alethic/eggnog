namespace Eggnog
{

    /// <summary>
    /// A function sort's column type of any arity (<see cref="FnColumnType{TFn}"/>), as <see cref="FnApply"/> reads it.
    /// </summary>
    internal interface IFnColumnType
    {

        /// <summary>
        /// Resolves a value's target for the graph and the type.
        /// </summary>
        /// <param name="target">The table or the primitive.</param>
        /// <returns>The target.</returns>
        FnTarget Resolve(object target);

        /// <summary>
        /// Decodes the container of a cell: the target and the partially applied arguments' cells.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <returns>The target and the cells.</returns>
        (FnTarget Target, uint[] Arguments) DecodeCells(uint cell);

    }

}
