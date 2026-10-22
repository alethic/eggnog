using System;

namespace Eggnog
{

    /// <summary>
    /// <c>double</c> over <c>DoubleCodec</c>, named <c>f64</c>, with egglog's <c>OrderedFloat</c> semantics
    /// (<see cref="OrderedDouble"/>): NaN is a value, equal to itself and greater than every other value, and −0.0
    /// equals +0.0. As in egglog, the codec interns both zeros as one value and every NaN as one, and decodes whichever
    /// was interned first: in a graph that interned −0.0 before +0.0, the zero reads back as −0.0.
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
            sort = new Relations.BaseSort<Relations.DoubleCodec, double>("f64", Relations.LiteralText.Float);
        }

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => engineType;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <inheritdoc />
        internal override uint Encode(double value) => values.Get<Relations.DoubleCodec, double>(value).Rep;

        /// <inheritdoc />
        internal override bool TryEncodeExisting(double value, out uint cell)
        {
            var found = values.TryGetExisting<Relations.DoubleCodec, double>(value, out var boxed);
            cell = boxed.Rep;
            return found;
        }

        /// <inheritdoc />
        internal override double Decode(uint cell, Scope? scope) => values.Unwrap<Relations.DoubleCodec, double>(new Relations.Value(cell));

        /// <inheritdoc />
        internal override string FormatValue(double value) => Relations.LiteralText.Float(value);

        /// <inheritdoc />
        internal override string DebugValue(double value) => Relations.DoubleCodec.Format(value);

        /// <inheritdoc />
        internal override int CompareValues(double x, double y) => OrderedDouble.CompareValues(x, y);

    }

}
