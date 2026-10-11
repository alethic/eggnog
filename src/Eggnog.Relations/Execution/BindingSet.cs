using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The rows of a factorized leaf scan: the values of <see cref="Variables"/> in each row of <see cref="Rows"/>, to
    /// be crossed with the other binding sets and the current bindings only when matches reach the action. One entry of
    /// egglog's <c>binding_sets</c> stack (core-relations/src/free_join/execute.rs:763-771).
    /// </summary>
    /// <remarks>
    /// Immutable while on the stack; a parallel hand-off shares it with the task.
    /// </remarks>
    internal readonly struct BindingSet
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="variables">The variables, one per cell of a row.</param>
        /// <param name="rows">The rows.</param>
        public BindingSet(Variable[] variables, TaggedRowBuffer rows)
        {
            ArgumentNullException.ThrowIfNull(variables);
            ArgumentNullException.ThrowIfNull(rows);
            Variables = variables;
            Rows = rows;
        }

        /// <summary>
        /// Gets the variables, one per cell of a row.
        /// </summary>
        public Variable[] Variables { get; }

        /// <summary>
        /// Gets the rows.
        /// </summary>
        public TaggedRowBuffer Rows { get; }

    }

}
