using System;

namespace Eggnog
{

    /// <summary>
    /// The function of a function value, resolved for a graph: egglog's <c>ResolvedFunction</c> (src/sort/fn.rs:470-518),
    /// which <c>resolve_function_container_target_with_context</c> (src/lib.rs:2275-2393) makes from the name, the
    /// partially applied arguments' sorts and the function sort. Its id is what the function value's container holds.
    /// Immutable.
    /// </summary>
    internal sealed class FnTarget
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id">The target's id in the graph.</param>
        /// <param name="kind">What the target applies.</param>
        /// <param name="target">The table or the primitive.</param>
        /// <param name="partial">The column types of the partially applied arguments.</param>
        /// <param name="inputs">The column types of the function sort's inputs, the arguments an application gives.</param>
        /// <param name="output">The column type of the function sort's output.</param>
        public FnTarget(int id, FnTargetKind kind, object target, ColumnType[] partial, ColumnType[] inputs, ColumnType output)
        {
            if (partial.Length > 64)
                throw new ArgumentException($"A function value holds at most 64 partially applied arguments, not {partial.Length}.", nameof(partial));

            Id = id;
            Kind = kind;
            Target = target;
            Partial = partial;
            Inputs = inputs;
            Output = output;
            Parameters = [.. partial, .. inputs];
            for (var i = 0; i < partial.Length; i++)
            {
                if (partial[i].InvolvesClasses)
                    Rebuild |= 1UL << i;

                if (partial[i] is IFnColumnType)
                    Nested |= 1UL << i;
            }
        }

        /// <summary>
        /// Gets the target's id in the graph.
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// Gets what the target applies.
        /// </summary>
        public FnTargetKind Kind { get; }

        /// <summary>
        /// Gets the table or the primitive.
        /// </summary>
        public object Target { get; }

        /// <summary>
        /// Gets the name, egglog's: the table's or the primitive's.
        /// </summary>
        public string Name => Target is TableHandle table ? table.Name : ((Primitive)Target).Name;

        /// <summary>
        /// Gets the table, for a constructor or a function.
        /// </summary>
        public TableHandle? Table => Target as TableHandle;

        /// <summary>
        /// Gets the primitive, for a primitive.
        /// </summary>
        public Primitive? Primitive => Target as Primitive;

        /// <summary>
        /// Gets the column types of the partially applied arguments.
        /// </summary>
        public ColumnType[] Partial { get; }

        /// <summary>
        /// Gets the column types of the function sort's inputs.
        /// </summary>
        public ColumnType[] Inputs { get; }

        /// <summary>
        /// Gets the column types of the whole application's arguments: the partial ones, then the inputs.
        /// </summary>
        public ColumnType[] Parameters { get; }

        /// <summary>
        /// Gets the column type of the output.
        /// </summary>
        public ColumnType Output { get; }

        /// <summary>
        /// Gets the rebuild flags of the partially applied arguments, one bit per argument of an e-class or eq-container
        /// sort, as egglog's <c>rebuild_contents</c> rebuilds them. A value clears the bit of a nested function value that
        /// holds no classes (<see cref="Nested"/>).
        /// </summary>
        public ulong Rebuild { get; }

        /// <summary>
        /// Gets the flags of the partially applied arguments that are function values, which hold classes or not by their
        /// own arguments.
        /// </summary>
        public ulong Nested { get; }

        /// <summary>
        /// Gets the message of an application in a context where the target is not valid: egglog's panic of
        /// <c>FunctionContainer::apply</c> (src/lib.rs:1351-1354).
        /// </summary>
        /// <param name="context">The call site's context.</param>
        /// <returns>The exception.</returns>
        public InvalidOperationException NotValidIn(PrimitiveAccess context) =>
            new($"unstable-fn over '{Name}' was applied in a context where its function is not valid for the call site ({Context(context)}); in a rule, make the rule naive (Naive()).");

        /// <summary>
        /// Names a context as egglog does.
        /// </summary>
        static string Context(PrimitiveAccess context) => context switch
        {
            PrimitiveAccess.Pure => "a pure context, which neither reads nor writes tables",
            PrimitiveAccess.Read => "a read context, which writes no table",
            PrimitiveAccess.Write => "a write context, which reads no table",
            _ => "a full context",
        };

    }

}
