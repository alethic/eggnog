using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Calls <see cref="Function"/> on <see cref="Args"/> in every live lane and binds the result to
    /// <see cref="Destination"/>; where it produces nothing, calls <see cref="Fallback"/> on
    /// <see cref="FallbackArgs"/> instead, and a lane where both fail halts. egglog's
    /// <c>Instr::ExternalWithFallback</c> (core-relations/src/action/mod.rs:957-966, 829-862); the bridge calls
    /// primitives with a panic function as the fallback.
    /// </summary>
    internal sealed class ExternalWithFallbackInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="function">The function called first.</param>
        /// <param name="args">Its arguments.</param>
        /// <param name="fallback">The function called where the first fails.</param>
        /// <param name="fallbackArgs">Its arguments.</param>
        /// <param name="destination">The variable defined.</param>
        public ExternalWithFallbackInstr(ExternalFunctionId function, ReadOnlySpan<QueryEntry> args, ExternalFunctionId fallback, ReadOnlySpan<QueryEntry> fallbackArgs, Variable destination)
            : base(InstrKind.ExternalWithFallback)
        {
            Function = function;
            Args = CopyOperands(args, nameof(args));
            Fallback = fallback;
            FallbackArgs = CopyOperands(fallbackArgs, nameof(fallbackArgs));
            Destination = CheckDestination(destination, nameof(destination));
            CheckNotReading(FallbackArgs, destination, nameof(fallbackArgs));
        }

        /// <summary>
        /// Gets the function called first. egglog's <c>f1</c>.
        /// </summary>
        public ExternalFunctionId Function { get; }

        /// <summary>
        /// Gets its arguments. egglog's <c>args1</c>.
        /// </summary>
        public QueryEntry[] Args { get; }

        /// <summary>
        /// Gets the function called where the first fails. egglog's <c>f2</c>.
        /// </summary>
        public ExternalFunctionId Fallback { get; }

        /// <summary>
        /// Gets its arguments. egglog's <c>args2</c>.
        /// </summary>
        public QueryEntry[] FallbackArgs { get; }

        /// <summary>
        /// Gets the variable defined.
        /// </summary>
        public Variable Destination { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(Destination).Append(" <- ExternalWithFallback ").Append(Function).Append(' ');
            AppendList(b, Args).Append(" ?? ").Append(Fallback).Append(' ');
            return AppendList(b, FallbackArgs).ToString();
        }

    }

}
