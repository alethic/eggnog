using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// What one block of a decomposed plan materializes: egglog's <c>MatSpec</c>
    /// (core-relations/src/free_join/plan.rs:261-275).
    /// </summary>
    /// <remarks>
    /// The block writes a map from the values of <see cref="MessageVariables"/> (shared with later bags) to rows of
    /// <see cref="ValueVariables"/> values (variables whose last bag this is and that the action or an earlier block
    /// needs). A variable is a value variable in at most one block. Variables in neither list are projected out.
    /// </remarks>
    internal sealed class MatSpec
    {

        /// <summary>
        /// Initializes a new instance. The arrays are owned by the new instance.
        /// </summary>
        /// <param name="messageVariables">The key variables, in ascending order.</param>
        /// <param name="valueVariables">The value variables, in ascending order.</param>
        public MatSpec(Variable[] messageVariables, Variable[] valueVariables)
        {
            ArgumentNullException.ThrowIfNull(messageVariables);
            ArgumentNullException.ThrowIfNull(valueVariables);
            MessageVariables = messageVariables;
            ValueVariables = valueVariables;
        }

        /// <summary>
        /// Gets the key variables (egglog's <c>msg_vars</c>). Callers must not modify the array.
        /// </summary>
        public Variable[] MessageVariables { get; }

        /// <summary>
        /// Gets the value variables (egglog's <c>val_vars</c>). Callers must not modify the array.
        /// </summary>
        public Variable[] ValueVariables { get; }

        /// <summary>
        /// Formats as <c>msg[v2] val[v4]</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append("msg[");
            PlanText.AppendJoined(b, MessageVariables, ",");
            b.Append("] val[");
            PlanText.AppendJoined(b, ValueVariables, ",");
            return b.Append(']').ToString();
        }

    }

}
