namespace Eggnog.Relations
{

    internal sealed partial class ExecutionState
    {

        /// <summary>
        /// Gets the base value registry of the database this state runs against: egglog's
        /// <c>ExecutionState::base_values</c> (core-relations/src/action/mod.rs:491-493), through which external functions
        /// box and unbox base values.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">The state is not attached to a database.</exception>
        public BaseValues BaseValues => RequireView().Owner.BaseValues;

    }

}
