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
        /// Gets a key of the container in <paramref name="cell"/> as its rebuild would make it, each element given by
        /// <paramref name="part"/>: two containers of the type have one key exactly when, their elements so, they are
        /// one container. A vector and a pair list their elements in order; a map collapses keys whose parts are one, the
        /// later in key-cell order kept, as egglog's rebuild of a <c>BTreeMap</c>, and a multiset adds their counts; both
        /// list their entries in the parts' order.
        /// </summary>
        /// <param name="cell">The container's cell.</param>
        /// <param name="part">An element's part of the key, given its column type and its cell.</param>
        /// <returns>The key.</returns>
        string CanonicalKey(uint cell, System.Func<ColumnType, uint, string> part);

        /// <summary>
        /// Gets the name a serialized e-graph gives the container in <paramref name="cell"/>: egglog's
        /// <c>serialized_name</c>.
        /// </summary>
        /// <param name="cell">The container's cell.</param>
        /// <returns>The name.</returns>
        string SerializedName(uint cell);

    }

}
