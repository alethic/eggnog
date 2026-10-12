using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// <c>int</c> and enums, stored as the engine's <c>i64</c> (<c>Int64Codec</c>) through widen and narrow functions
    /// (D20): <c>Int32Codec</c> boxes −1 to <c>Value.StaleRep</c>, which marks a deleted row when it lands in a table's
    /// first column, while <c>Int64Codec</c> boxes <c>[0, 2^31)</c> inline and interns the rest. The extraction sort is a
    /// <c>BaseSort&lt;Int64Codec, long&gt;</c> under this type's name that formats through the narrow function.
    /// </summary>
    /// <typeparam name="T">The CLR type of the values.</typeparam>
    internal sealed class WidenedColumnType<T> : ColumnType<T> where T : notnull
    {

        readonly Relations.BaseValues values;
        readonly Func<T, long> widen;
        readonly Func<long, T> narrow;
        readonly Func<T, string> format;
        readonly Relations.ColumnTy engineType;
        readonly Relations.BaseSort<Relations.Int64Codec, long> sort;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The type's name.</param>
        /// <param name="values">The graph's base value registry.</param>
        /// <param name="widen">Maps a value to the <c>long</c> it is stored as; injective.</param>
        /// <param name="narrow">Inverts <paramref name="widen"/>.</param>
        /// <param name="format">Formats a value as an s-expression literal.</param>
        public WidenedColumnType(string name, Relations.BaseValues values, Func<T, long> widen, Func<long, T> narrow, Func<T, string> format)
            : base(name, ColumnKind.Value)
        {
            this.values = values;
            this.widen = widen;
            this.narrow = narrow;
            this.format = format;
            engineType = Relations.ColumnTy.Base(values.RegisterType<Relations.Int64Codec, long>());
            sort = new Relations.BaseSort<Relations.Int64Codec, long>(name, stored => format(narrow(stored)), Comparer<long>.Default);
        }

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => engineType;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <inheritdoc />
        internal override uint Encode(T value) => values.Get<Relations.Int64Codec, long>(widen(value)).Rep;

        /// <inheritdoc />
        internal override bool TryEncodeExisting(T value, out uint cell)
        {
            var found = values.TryGetExisting<Relations.Int64Codec, long>(widen(value), out var boxed);
            cell = boxed.Rep;
            return found;
        }

        /// <inheritdoc />
        internal override T Decode(uint cell, Scope? scope) => narrow(values.Unwrap<Relations.Int64Codec, long>(new Relations.Value(cell)));

        /// <inheritdoc />
        internal override string FormatValue(T value) => format(value);

        /// <inheritdoc />
        internal override int CompareValues(T x, T y) => widen(x).CompareTo(widen(y));

    }

}
