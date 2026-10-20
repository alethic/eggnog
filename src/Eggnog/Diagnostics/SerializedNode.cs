using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A node of a <see cref="SerializedGraph"/>: egraph-serialize's <c>Node</c>. A table's row is a node named after the
    /// table, with an edge to one node of each argument's class; a value is a node of its own class; and a class whose
    /// every node was left out is one placeholder node, <c>[...]</c>.
    /// </summary>
    public sealed class SerializedNode
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id">The node's id.</param>
        /// <param name="op">The operator.</param>
        /// <param name="class">The id of the node's class.</param>
        /// <param name="cost">The cost.</param>
        /// <param name="children">The ids of the argument nodes.</param>
        /// <param name="subsumed">Whether the row is subsumed.</param>
        internal SerializedNode(string id, string op, string @class, double cost, IReadOnlyList<string> children, bool subsumed)
        {
            Id = id;
            Op = op;
            Class = @class;
            Cost = cost;
            Children = children;
            Subsumed = subsumed;
        }

        /// <summary>
        /// Gets the node's id: <c>function-{row}-{table}</c> for a table's row (its position in the table's content
        /// order), <c>primitive-{class}</c> for a value, <c>dummy-{class}</c> for a placeholder.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the operator: the table's name; a value's literal as egglog prints it (Rust's <c>Debug</c>, so a string
        /// is quoted); <c>set-of</c> for a set, whose children are its elements; <c>[...]</c> for a placeholder.
        /// </summary>
        public string Op { get; }

        /// <summary>
        /// Gets the id of the node's class (<see cref="SerializedClass.Id"/>).
        /// </summary>
        public string Class { get; }

        /// <summary>
        /// Gets the cost: a constructor's declared cost, 1 for a function's or relation's row and for a value, and
        /// positive infinity for a placeholder.
        /// </summary>
        public double Cost { get; }

        /// <summary>
        /// Gets the ids of the argument nodes, one per key column; for a set, its elements' nodes.
        /// </summary>
        public IReadOnlyList<string> Children { get; }

        /// <summary>
        /// Gets whether the row is subsumed (<see cref="TableOptions.Subsumable"/>).
        /// </summary>
        public bool Subsumed { get; }

        /// <summary>
        /// Gets the id.
        /// </summary>
        /// <returns>The id.</returns>
        public override string ToString() => Id;

    }

}
