using System;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>Unit</c> sort, of <see cref="ValueTuple"/>, over <c>UnitCodec</c>, whose single value is the inline
    /// cell 0, rendered <c>()</c>: the output of relations, and of the functions, function sorts and primitives of
    /// <c>Unit</c> (<see cref="ValueStore.TypeOf{T}"/>).
    /// </summary>
    internal sealed class UnitColumnType : ColumnType<ValueTuple>
    {

        readonly Relations.ColumnTy engineType;
        readonly Relations.BaseSort<Relations.UnitCodec, ValueTuple> sort = new("Unit", static _ => "()");

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
        internal override Relations.Sort ExtractionSort => sort;

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
