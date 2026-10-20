using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// <c>double</c> over <c>DoubleCodec</c>, named <c>f64</c>. Encoding maps −0.0 to +0.0 and rejects NaN, because
    /// equal values must have equal cells: <c>DoubleCodec</c> treats −0.0 and +0.0 as one value and decodes whichever
    /// was interned first, and NaN equals nothing.
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
            if (double.IsNaN(value))
            {
                cell = 0;
                return false;
            }

            var found = values.TryGetExisting<Relations.DoubleCodec, double>(value == 0 ? 0.0 : value, out var boxed);
            cell = boxed.Rep;
            return found;
        }

        /// <inheritdoc />
        internal override double Decode(uint cell, Scope? scope) => values.Unwrap<Relations.DoubleCodec, double>(new Relations.Value(cell));

        /// <inheritdoc />
        internal override string FormatValue(double value) => Relations.LiteralText.Float(value);

        /// <inheritdoc />
        internal override string DebugValue(double value) => Relations.DoubleCodec.Format(value == 0 ? 0.0 : value);

        /// <inheritdoc />
        internal override int CompareValues(double x, double y) => x.CompareTo(y);

        /// <summary>
        /// Maps −0.0 to +0.0 and rejects NaN.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="value"/> is NaN.</exception>
        static double Normalize(double value)
        {
            if (double.IsNaN(value))
                throw new ArgumentException("NaN is not a value: it equals nothing, so no column can hold it.", nameof(value));

            return value == 0 ? 0.0 : value;
        }

    }

}
