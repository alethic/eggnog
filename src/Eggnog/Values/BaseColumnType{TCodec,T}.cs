using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A value type over an engine codec: encoding is <c>BaseValues.Get</c>, decoding <c>Unwrap</c>, and
    /// <see cref="TryEncodeExisting"/> <c>BaseValues.TryGetExisting</c> (E1); the extraction sort is a <c>BaseSort</c>
    /// that formats and orders like this type. Serves <c>string</c>, <c>bool</c>, <c>long</c>, <see cref="ValueSet{T}"/>
    /// (over <c>InternedCodec&lt;ValueSet&lt;T&gt;&gt;</c>) and registered types (<c>InternedCodec&lt;T&gt;</c>).
    /// </summary>
    /// <typeparam name="TCodec">The engine codec.</typeparam>
    /// <typeparam name="T">The CLR type of the values.</typeparam>
    internal sealed class BaseColumnType<TCodec, T> : ColumnType<T>
        where TCodec : struct, Relations.IBaseValueCodec<T>
        where T : notnull, IEquatable<T>
    {

        readonly Relations.BaseValues values;
        readonly Func<T, string> format;
        readonly IComparer<T>? order;
        readonly Relations.ColumnTy engineType;
        readonly Relations.BaseSort<TCodec, T> sort;

        /// <summary>
        /// Initializes a new instance and registers the codec with <paramref name="values"/>.
        /// </summary>
        /// <param name="name">The type's name.</param>
        /// <param name="values">The graph's base value registry.</param>
        /// <param name="format">Formats a value as an s-expression literal; must not depend on the culture.</param>
        /// <param name="order">The content order; <c>null</c> orders by cell.</param>
        public BaseColumnType(string name, Relations.BaseValues values, Func<T, string> format, IComparer<T>? order)
            : base(name, ColumnKind.Value)
        {
            this.values = values;
            this.format = format;
            this.order = order;
            engineType = Relations.ColumnTy.Base(values.RegisterType<TCodec, T>());
            sort = new Relations.BaseSort<TCodec, T>(name, format, order);
        }

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => engineType;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <inheritdoc />
        internal override uint Encode(T value)
        {
            if (value is null)
                throw new ArgumentNullException(nameof(value));

            return values.Get<TCodec, T>(value).Rep;
        }

        /// <inheritdoc />
        internal override bool TryEncodeExisting(T value, out uint cell)
        {
            if (value is null)
                throw new ArgumentNullException(nameof(value));

            var found = values.TryGetExisting<TCodec, T>(value, out var boxed);
            cell = boxed.Rep;
            return found;
        }

        /// <inheritdoc />
        internal override T Decode(uint cell, Scope? scope) => values.Unwrap<TCodec, T>(new Relations.Value(cell));

        /// <inheritdoc />
        internal override string FormatValue(T value) => format(value);

        /// <summary>
        /// Compares two values in content order: by the type's order, else by cell. Cells are read with
        /// <see cref="TryEncodeExisting"/>, so comparing never interns (D15): a value never interned comes after every
        /// interned one, and two such values compare by hash code (zero when they are equal), a deterministic preorder
        /// within the process.
        /// </summary>
        /// <param name="x">One value.</param>
        /// <param name="y">The other value.</param>
        /// <returns>Negative, zero or positive.</returns>
        internal override int CompareValues(T x, T y)
        {
            if (order is not null)
                return order.Compare(x, y);

            var hasX = TryEncodeExisting(x, out var cellX);
            var hasY = TryEncodeExisting(y, out var cellY);
            if (hasX && hasY)
                return cellX.CompareTo(cellY);

            if (hasX)
                return -1;

            if (hasY)
                return 1;

            if (x.Equals(y))
                return 0;

            return EqualityComparer<T>.Default.GetHashCode(x).CompareTo(EqualityComparer<T>.Default.GetHashCode(y));
        }

        /// <inheritdoc />
        internal override int CompareCells(uint x, uint y)
        {
            if (x == y)
                return 0;

            return order is not null ? order.Compare(Decode(x, null), Decode(y, null)) : x.CompareTo(y);
        }

    }

}
