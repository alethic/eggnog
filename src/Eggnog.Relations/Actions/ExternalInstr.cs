using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Calls <see cref="Function"/> on <see cref="Args"/> in every live lane and binds the result to
    /// <see cref="Destination"/>; a lane where the function produces nothing halts. egglog's <c>Instr::External</c>
    /// (core-relations/src/action/mod.rs:950-955, 819-828): primitives and panics.
    /// </summary>
    internal sealed class ExternalInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="args">The arguments.</param>
        /// <param name="destination">The variable defined.</param>
        public ExternalInstr(ExternalFunctionId function, ReadOnlySpan<QueryEntry> args, Variable destination)
            : base(InstrKind.External)
        {
            Function = function;
            Args = CopyOperands(args, nameof(args));
            Destination = CheckDestination(destination, nameof(destination));
        }

        /// <summary>
        /// Gets the function.
        /// </summary>
        public ExternalFunctionId Function { get; }

        /// <summary>
        /// Gets the arguments.
        /// </summary>
        public QueryEntry[] Args { get; }

        /// <summary>
        /// Gets the variable defined.
        /// </summary>
        public Variable Destination { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(Destination).Append(" <- External ").Append(Function).Append(' ');
            return AppendList(b, Args).ToString();
        }

    }

}
