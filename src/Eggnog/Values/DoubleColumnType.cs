using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// <c>double</c> over <c>DoubleCodec</c>, named <c>f64</c>, with egglog's <c>OrderedFloat</c> semantics
    /// (<see cref="OrderedDouble"/>): NaN is a value, equal to itself and greater than every other value, and −0.0
    /// equals +0.0. Encoding maps −0.0 to +0.0 and every NaN to <see cref="double.NaN"/>, because equal values must have
    /// equal cells: <c>DoubleCodec</c> treats −0.0 and +0.0 as one value and decodes whichever was interned first.
    /// </summary>
    internal sealed class DoubleColumnType : ColumnType<double>
    {

        readonly Relations.BaseValues values;
        readonly Relations.ColumnTy engineType;
        readonly Relations.BaseSort<Relations.DoubleCodec, double> sort;

        /// <summary>
        /// Initializes a new instance and registers the codec with <paramref name="values"/>.
        /// </summary>
        /// <param name="values">The graph's base value registry.</param>
        public DoubleColumnType(Relations.BaseValues values)
            : base("f64", ColumnKind.Value)
        {
            this.values = values;
            engineType = Relations.ColumnTy.Base(values.RegisterType<Relations.DoubleCodec, double>());
            sort = new Relations.BaseSort<Relations.DoubleCodec, double>("f64", Relations.LiteralText.Float, Comparer<double>.Default);
        }

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => engineType;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <inheritdoc />
        internal override uint Encode(double value) => values.Get<Relations.DoubleCodec, double>(Normalize(value)).Rep;

        /// <inheritdoc />
        internal override bool TryEncodeExisting(double value, out uint cell)
        {
            var found = values.TryGetExisting<Relations.DoubleCodec, double>(Normalize(value), out var boxed);
            cell = boxed.Rep;
            return found;
        }

        /// <inheritdoc />
        internal override double Decode(uint cell, Scope? scope) => values.Unwrap<Relations.DoubleCodec, double>(new Relations.Value(cell));

        /// <inheritdoc />
        internal override string FormatValue(double value) => Relations.LiteralText.Float(value);

        /// <inheritdoc />
        internal override int CompareValues(double x, double y) => OrderedDouble.CompareValues(x, y);

        /// <summary>
        /// Maps −0.0 to +0.0 and every NaN to <see cref="double.NaN"/>: <c>OrderedFloat</c>'s one zero and one NaN.
        /// </summary>
        static double Normalize(double value) => double.IsNaN(value) ? double.NaN : value == 0 ? 0.0 : value;

    }

}
