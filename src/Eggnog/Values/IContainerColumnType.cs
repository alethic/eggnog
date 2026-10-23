using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A column type whose values are engine containers, interned in the graph's container environments: egglog's
    /// container sorts as the library reads them, for the serializer and the cost model. A member that reads a
    /// container by its cell reads it through the container view it is given (<see cref="Relations.ContainerView"/>),
    /// the containers of the scope the cell belongs to.
    /// </summary>
    internal interface IContainerColumnType
    {

        /// <summary>
        /// Appends the elements of the container in <paramref name="cell"/>, each with its column type, in the
        /// container's order: egglog's <c>inner_values</c>. Reads the stored cells and never interns.
        /// </summary>
        /// <param name="cell">The container's cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <param name="destination">Receives the elements.</param>
        /// <exception cref="System.InvalidOperationException">No container of this type has the cell.</exception>
        void AppendElements(uint cell, Relations.ContainerView view, List<(ColumnType Type, uint Cell)> destination);

        /// <summary>
        /// Appends the parts of a constant of the type, each with its column type, as egglog's form of it lists them, the
        /// arguments of a call: a set's, a vector's or a multiset's elements, in element order with a multiset's
        /// repeats; a pair's two; a map's keys and values in key order, key before value
        /// (<see cref="LiteralOrder.Parts"/>).
        /// </summary>
        /// <param name="value">The constant.</param>
        /// <param name="destination">Receives the parts.</param>
        void AppendLiteralParts(object value, List<(ColumnType Type, object Value)> destination);

        /// <summary>
        /// Gets the column types of the container's elements: egglog's <c>inner_sorts</c>.
        /// </summary>
        IReadOnlyList<ColumnType> ElementTypes { get; }

        /// <summary>
        /// Gets a key of the container in <paramref name="cell"/> as its rebuild would make it, each element given by
        /// <paramref name="part"/>: two containers of the type have one key exactly when, their elements so, they are
        /// one container. A vector and a pair list their elements in order; a set keeps one of elements whose parts are
        /// one, as egglog's rebuild of a <c>BTreeSet</c>, a map collapses such keys, the later in key-cell order kept, as
        /// its rebuild of a <c>BTreeMap</c>, and a multiset adds their counts; the three list their entries in the parts'
        /// order.
        /// </summary>
        /// <param name="cell">The container's cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <param name="part">An element's part of the key, given its column type and its cell.</param>
        /// <returns>The key.</returns>
        string CanonicalKey(uint cell, Relations.ContainerView view, System.Func<ColumnType, uint, string> part);

        /// <summary>
        /// Appends the distinct elements of the container in <paramref name="cell"/>, each with its column type: as
        /// <see cref="AppendElements"/>, except that a multiset's element comes once, whatever its count.
        /// </summary>
        /// <param name="cell">The container's cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <param name="destination">Receives the elements.</param>
        void AppendDistinctElements(uint cell, Relations.ContainerView view, List<(ColumnType Type, uint Cell)> destination);

        /// <summary>
        /// Gets whether the type's rebuild depends on the order in which its classes merge: a map whose keys are classes,
        /// or hold them, keeps the later of two keys that become one, and which is later depends on what the rebuild
        /// passes before made of them; or a container of such maps.
        /// </summary>
        bool RebuildsInOrder { get; }

        /// <summary>
        /// Gets the content of the container in <paramref name="cell"/> as its stored layout: a vector's or a pair's
        /// elements in order, a set's elements in cell order, a map's keys and values in key-cell order, key before value,
        /// a multiset's elements and counts in cell order, element before count.
        /// </summary>
        /// <param name="cell">The container's cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <returns>The content.</returns>
        long[] Content(uint cell, Relations.ContainerView view);

        /// <summary>
        /// Rebuilds a content as the container's rebuild would, each element that names classes mapped through
        /// <paramref name="find"/>: a set's elements that become one become one element, a map's keys that become one
        /// collapse, the later kept, and a multiset's elements add their counts, all then in cell order.
        /// </summary>
        /// <param name="content">The content, in <see cref="Content"/>'s layout.</param>
        /// <param name="find">The cell an element's cell is now.</param>
        /// <returns>The rebuilt content, in that layout.</returns>
        long[] Rebuild(long[] content, System.Func<uint, uint> find);

        /// <summary>
        /// Gets the name a serialized e-graph gives the container in <paramref name="cell"/>: egglog's
        /// <c>serialized_name</c>.
        /// </summary>
        /// <param name="cell">The container's cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <returns>The name.</returns>
        string SerializedName(uint cell, Relations.ContainerView view);

    }

}
