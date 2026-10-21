using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A column type whose values are engine containers, interned in the graph's container environments: egglog's
    /// container sorts as the library reads them, for the serializer and the cost model.
    /// </summary>
    internal interface IContainerColumnType
    {

        /// <summary>
        /// Appends the elements of the container in <paramref name="cell"/>, each with its column type, in the
        /// container's order: egglog's <c>inner_values</c>. Reads the stored cells and never interns.
        /// </summary>
        /// <param name="cell">The container's cell.</param>
        /// <param name="destination">Receives the elements.</param>
        /// <exception cref="System.InvalidOperationException">No container of this type has the cell.</exception>
        void AppendElements(uint cell, List<(ColumnType Type, uint Cell)> destination);

        /// <summary>
        /// Gets the column types of the container's elements: egglog's <c>inner_sorts</c>.
        /// </summary>
        IReadOnlyList<ColumnType> ElementTypes { get; }

        /// <summary>
        /// Gets the name a serialized e-graph gives the container in <paramref name="cell"/>: egglog's
        /// <c>serialized_name</c>.
        /// </summary>
        /// <param name="cell">The container's cell.</param>
        /// <returns>The name.</returns>
        string SerializedName(uint cell);

    }

}
