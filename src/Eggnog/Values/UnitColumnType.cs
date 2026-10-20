using System;

namespace Eggnog
{

    /// <summary>
    /// The unit value type of relation outputs, over <c>UnitCodec</c>, whose single value is the inline cell 0. Not
    /// reachable through <see cref="ValueStore.TypeOf{T}"/>, and never seen by extraction.
    /// </summary>
    internal sealed class UnitColumnType : ColumnType<ValueTuple>
    {

        readonly Relations.ColumnTy engineType;

        /// <summary>
        /// Initializes a new instance and registers the codec with <paramref name="values"/>.
        /// </summary>
        /// <param name="values">The graph's base value registry.</param>
        public UnitColumnType(Relations.BaseValues values)
            : base("Unit", ColumnKind.Value)
        {
            engineType = Relations.ColumnTy.Base(values.RegisterType<Relations.UnitCodec, ValueTuple>());
            Unit = values.Get<Relations.UnitCodec, ValueTuple>(default).Rep;
        }

        /// <summary>
        /// Gets the unit value's cell.
        /// </summary>
        public uint Unit { get; }

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => engineType;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => throw new InvalidOperationException("The unit type of relation outputs has no extraction sort.");

        /// <inheritdoc />
        internal override uint Encode(ValueTuple value) => Unit;

        /// <inheritdoc />
        internal override bool TryEncodeExisting(ValueTuple value, out uint cell)
        {
            cell = Unit;
            return true;
        }

        /// <inheritdoc />
        internal override ValueTuple Decode(uint cell, Scope? scope) => default;

        /// <inheritdoc />
        internal override string FormatValue(ValueTuple value) => "()";

        /// <inheritdoc />
        internal override string DebugValue(ValueTuple value) => "()";

        /// <inheritdoc />
        internal override int CompareValues(ValueTuple x, ValueTuple y) => 0;

    }

}
