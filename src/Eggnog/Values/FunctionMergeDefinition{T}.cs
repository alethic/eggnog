using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A merge that is not a lattice over values of <typeparamref name="T"/>: <c>new</c>, <c>old</c>, or a function of
    /// the old and the new value, which runs as an external function of the engine (<see cref="FunctionMergeCall{T}"/>).
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    internal sealed class FunctionMergeDefinition<T> : FunctionMergeDefinition
    {

        /// <summary>
        /// Gets <c>:merge new</c>.
        /// </summary>
        public static FunctionMergeDefinition<T> NewMerge { get; } = new("new", null);

        /// <summary>
        /// Gets <c>:merge old</c>.
        /// </summary>
        public static FunctionMergeDefinition<T> OldMerge { get; } = new("old", null);

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The merge's name.</param>
        /// <param name="function">The function of the old and the new value; <c>null</c> for <c>new</c> and
        /// <c>old</c>.</param>
        public FunctionMergeDefinition(string name, Func<T, T, T>? function)
            : base(name)
        {
            Function = function;
        }

        /// <summary>
        /// Gets the function of the old and the new value; <c>null</c> for <c>new</c> and <c>old</c>.
        /// </summary>
        public Func<T, T, T>? Function { get; }

        /// <inheritdoc />
        public override bool DecodesValues => Function is not null;

        /// <inheritdoc />
        public override Relations.MergeFn CreateMergeFn(EGraph graph, string functionName, ColumnType type, List<Relations.ExternalFunctionId> registered)
        {
            if (ReferenceEquals(this, NewMerge))
                return Relations.MergeFn.New;

            if (ReferenceEquals(this, OldMerge))
                return Relations.MergeFn.Old;

            var merge = graph.Engine.RegisterExternalFunction(new FunctionMergeCall<T>(graph, functionName, this, (ColumnType<T>)type));
            registered.Add(merge);
            return Relations.MergeFn.Primitive(merge, Relations.MergeFn.Old, Relations.MergeFn.New);
        }

        /// <inheritdoc />
        public override uint MergeCells(Scope scope, ColumnType type, uint stored, uint incoming)
        {
            if (ReferenceEquals(this, NewMerge))
                return incoming;

            if (ReferenceEquals(this, OldMerge))
                return stored;

            var typed = (ColumnType<T>)type;
            return typed.Encode(Function!(typed.Decode(stored, scope), typed.Decode(incoming, scope)));
        }

    }

}
