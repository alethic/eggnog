using System;

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
        /// Gets the column type of the output.
        /// </summary>
        ColumnType Output { get; }

        /// <summary>
        /// Decodes the container of a cell, read through <paramref name="view"/>: the target and the partially applied
        /// arguments' cells.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <returns>The target and the cells.</returns>
        (FnTarget Target, uint[] Arguments) DecodeCells(uint cell, Relations.ContainerView view);

        /// <summary>
        /// Gets whether a function value holds classes: whether one of its partially applied arguments is a class or holds
        /// one.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>Whether it does.</returns>
        /// <exception cref="ArgumentException">The value is a default one, or its function's signature is not this
        /// type's.</exception>
        bool HoldsClassesIn(object value);

    }

}
