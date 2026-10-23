using System;

namespace Eggnog
{

    /// <summary>
    /// egglog's fallible form of a filter primitive (<see cref="Primitives.Fallible"/>): <c>&lt;</c> of <c>bool-&lt;</c>
    /// (src/sort/i64.rs:46-52), <c>set-contains</c> of the value <c>bool-set-contains</c>, and so on. A partial
    /// primitive of <c>Unit</c>, named as egglog names it, that runs the filter: a result where the filter holds, none
    /// where it does not.
    /// </summary>
    internal sealed class FallibleCall : Primitive
    {

        readonly Primitive filter;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="filter">The filter primitive: of a <c>bool</c>, with egglog's fallible name.</param>
        public FallibleCall(Primitive filter)
            : base(filter.FilterName!, filter.ParameterTypes, typeof(ValueTuple), false, true)
        {
            this.filter = filter;
        }

        /// <inheritdoc />
        internal override uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope) =>
            TryInvokeCore(arguments, parameters, result, scope, out var cell) ? cell : throw NoResult(Name);

        /// <inheritdoc />
        internal override bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope, out uint cell)
        {
            var truth = result.Store!.TypeOf<bool>();
            cell = ((UnitColumnType)result).Unit;
            return filter.TryInvokeCore(arguments, parameters, truth, scope, out var held) && truth.Decode(held, scope);
        }

        /// <inheritdoc />
        internal override bool TryInvokeValue(object[] arguments, ColumnType[] parameters, Relations.ContainerView view, out object? value)
        {
            value = default(ValueTuple);
            return filter.TryInvokeValue(arguments, parameters, view, out var held) && (bool)held!;
        }

    }

}
