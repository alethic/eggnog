using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The type of a column whose values are <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The CLR type of the values.</typeparam>
    public abstract class ColumnType<T> : ColumnType
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The type's name, unique in its store.</param>
        /// <param name="kind">Whether cells are classes or values.</param>
        private protected ColumnType(string name, ColumnKind kind)
            : base(name, kind, typeof(T))
        {

        }

        /// <summary>
        /// Formats a value as an s-expression literal (see <see cref="ColumnType.Format(object)"/>).
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The literal.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is <c>null</c>.</exception>
        public string Format(T value)
        {
            if (value is null)
                throw new ArgumentNullException(nameof(value));

            return FormatValue(value);
        }

        /// <summary>
        /// Compares two values in content order: strings by code point, numbers numeric, <c>false &lt; true</c>, sets
        /// lexicographic over their sorted elements, registered types by their comparer (else
        /// <c>Comparer&lt;T&gt;.Default</c> when comparable, else their cells), classes by id. Host reads that list
        /// rows sort by this order. Comparing is a read: it never interns. Ordered by cell, a value that was never
        /// interned comes after every interned one, and two such values compare by hash code (zero when they are
        /// equal).
        /// </summary>
        /// <param name="x">One value.</param>
        /// <param name="y">The other value.</param>
        /// <returns>Negative, zero or positive, as <paramref name="x"/> comes before, with or after
        /// <paramref name="y"/>.</returns>
        /// <exception cref="ArgumentNullException">A value is <c>null</c>.</exception>
        public int Compare(T x, T y)
        {
            if (x is null)
                throw new ArgumentNullException(nameof(x));

            if (y is null)
                throw new ArgumentNullException(nameof(y));

            return CompareValues(x, y);
        }

        /// <summary>
        /// Encodes a value into its cell, interning it when its codec does not box it inline. Thread-safe.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="ArgumentException">The value is not representable (a default class).</exception>
        internal abstract uint Encode(T value);

        /// <summary>
        /// Encodes a value a primitive returned in <paramref name="scope"/>: as <see cref="Encode"/>, except that a class
        /// must be usable there (<see cref="SortColumnType{TSort}"/>).
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="scope">The scope the call runs in; <c>null</c> for no check.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="InvalidOperationException">The value is a class this scope cannot use.</exception>
        internal virtual uint EncodeIn(T value, Scope? scope) => Encode(value);

        /// <summary>
        /// Encodes a host's value in <paramref name="scope"/> (<see cref="ColumnType.EncodeCanonicalObject"/>): as
        /// <see cref="EncodeIn"/>, except that each class the value holds is checked and canonical through
        /// <paramref name="find"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="scope">The scope written.</param>
        /// <param name="find">The canonical id of a class cell.</param>
        /// <param name="openBatch">The origin of an open Write batch whose classes the scope may use; <c>null</c> for
        /// none.</param>
        /// <returns>The cell.</returns>
        internal virtual uint EncodeCanonical(T value, Scope scope, Func<uint, uint> find, ScopeOrigin? openBatch) =>
            EncodeIn(value, scope);

        /// <summary>
        /// Gets the cell of a host's value in <paramref name="scope"/> without interning
        /// (<see cref="ColumnType.TryEncodeCanonicalObject"/>): as <see cref="TryEncodeExisting"/>, except that each class
        /// the value holds is checked and canonical through <paramref name="find"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="scope">The scope read.</param>
        /// <param name="find">The canonical id of a class cell.</param>
        /// <param name="cell">The cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the value has a cell already.</returns>
        internal virtual bool TryEncodeCanonical(T value, Scope scope, Func<uint, uint> find, out uint cell) =>
            TryEncodeExisting(value, out cell);

        /// <summary>
        /// Gets a host's value as <paramref name="scope"/> sees it (<see cref="ColumnType.CanonicalObject"/>): each class
        /// it holds checked and canonical through <paramref name="find"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="scope">The scope read.</param>
        /// <param name="find">The canonical id of a class cell.</param>
        /// <returns>The value.</returns>
        internal virtual T Canonical(T value, Scope scope, Func<uint, uint> find) => value;

        /// <summary>
        /// Gets the cell of a value that is inline or interned already, without interning: a read never grows the
        /// graph's interning pools, which are permanent.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="cell">The cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the value has a cell already.</returns>
        internal abstract bool TryEncodeExisting(T value, out uint cell);

        /// <summary>
        /// Decodes a cell: a value, or for a sort a class with <paramref name="scope"/>'s origin. Lock-free.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="scope">The scope serving the call; needed for a sort.</param>
        /// <returns>The value.</returns>
        internal abstract T Decode(uint cell, Scope? scope);

        /// <inheritdoc />
        internal override bool IsOrdered =>
            typeof(T) == typeof(string)
                || typeof(IComparable<T>).IsAssignableFrom(typeof(T))
                || typeof(IComparable).IsAssignableFrom(typeof(T));

        /// <summary>
        /// Formats a non-null value.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The literal.</returns>
        internal abstract string FormatValue(T value);

        /// <summary>
        /// Compares two non-null values in content order.
        /// </summary>
        /// <param name="x">One value.</param>
        /// <param name="y">The other value.</param>
        /// <returns>The order.</returns>
        internal abstract int CompareValues(T x, T y);

        /// <summary>
        /// Formats a non-null value as its engine codec formats its cell (<see cref="ColumnType.FormatDebug"/>).
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        /// <exception cref="InvalidOperationException">The type is a sort, which has no codec.</exception>
        internal virtual string DebugValue(T value) => throw new InvalidOperationException($"{Name} is a sort, which has no base value codec.");

        /// <inheritdoc />
        internal override string FormatDebug(object value) => DebugValue(Cast(value));

        /// <inheritdoc />
        internal override uint EncodeObject(object value) => Encode(Cast(value));

        /// <inheritdoc />
        internal override bool TryEncodeExistingObject(object value, out uint cell) => TryEncodeExisting(Cast(value), out cell);

        /// <inheritdoc />
        internal override uint EncodeInObject(object value, Scope? scope) => EncodeIn(Cast(value), scope);

        /// <inheritdoc />
        internal override int CompareObjects(object x, object y) => CompareValues(Cast(x), Cast(y));

        /// <inheritdoc />
        internal override uint EncodeCanonicalObject(object value, Scope scope, Func<uint, uint> find, ScopeOrigin? openBatch) =>
            EncodeCanonical(Cast(value), scope, find, openBatch);

        /// <inheritdoc />
        internal override bool TryEncodeCanonicalObject(object value, Scope scope, Func<uint, uint> find, out uint cell) =>
            TryEncodeCanonical(Cast(value), scope, find, out cell);

        /// <inheritdoc />
        internal override object CanonicalObject(object value, Scope scope, Func<uint, uint> find) =>
            Canonical(Cast(value), scope, find)!;

        /// <summary>
        /// Gets whether two non-null values are one value as the codec interns them: by <see cref="IEquatable{T}"/>, as
        /// the intern tables compare, unless the codec makes more values one.
        /// </summary>
        /// <param name="x">One value.</param>
        /// <param name="y">The other value.</param>
        /// <returns>Whether they would intern as one cell.</returns>
        internal virtual bool ValuesEqual(T x, T y) => EqualityComparer<T>.Default.Equals(x, y);

        /// <inheritdoc />
        internal override bool ObjectsEqual(object x, object y) => ValuesEqual(Cast(x), Cast(y));

        /// <inheritdoc />
        internal override object DecodeObject(uint cell, Scope? scope) => Decode(cell, scope)!;

        /// <inheritdoc />
        internal override string FormatObject(object value) => FormatValue(Cast(value));

        /// <inheritdoc />
        internal override string FormatCell(uint cell) => FormatValue(Decode(cell, null));

        /// <inheritdoc />
        internal override int CompareCells(uint x, uint y) => x == y ? 0 : CompareValues(Decode(x, null), Decode(y, null));

        /// <summary>
        /// Casts a value to <typeparamref name="T"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The value is not a <typeparamref name="T"/>.</exception>
        T Cast(object value)
        {
            if (value is T typed)
                return typed;

            throw new ArgumentException($"A {value?.GetType().Name ?? "null"} is not a value of column type {Name} ({typeof(T).Name}).", nameof(value));
        }

    }

}
