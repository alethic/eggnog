using System;

namespace Eggnog
{

    /// <summary>
    /// The type of a column: a sort (its cells are classes) or a value type (its cells are base values or containers of
    /// values). Within one
    /// graph, equal values have equal cells in every scope, and distinct values distinct cells, so value joins,
    /// constants and disequalities compare cells.
    /// </summary>
    /// <remarks>
    /// Column types belong to one graph's <see cref="ValueStore"/>: <see cref="ValueStore.TypeOf{T}"/> finds them, the
    /// <c>Register*</c> methods and <see cref="EGraph.DefineSort{TSort}(string)"/> make them.
    /// </remarks>
    public abstract class ColumnType
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The type's name, unique in its store.</param>
        /// <param name="kind">Whether cells are classes or values.</param>
        /// <param name="clrType">The CLR type of the values.</param>
        private protected ColumnType(string name, ColumnKind kind, Type clrType)
        {
            Name = name;
            Kind = kind;
            ClrType = clrType;
        }

        /// <summary>
        /// Gets the type's name: <c>i64</c>, <c>String</c>, <c>Set&lt;String&gt;</c>, a sort's name, and so on.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets whether the cells are classes or values.
        /// </summary>
        public ColumnKind Kind { get; }

        /// <summary>
        /// Gets the CLR type of the values.
        /// </summary>
        public Type ClrType { get; }

        /// <summary>
        /// Formats a value as an s-expression literal: <c>"kind"</c>, <c>42</c>, <c>true</c>, <c>1.5</c>,
        /// <c>(set-of "a" "b")</c>, <c>#17</c>.
        /// </summary>
        /// <param name="value">The value, of <see cref="ClrType"/>.</param>
        /// <returns>The literal.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="value"/> is not of <see cref="ClrType"/>.</exception>
        public string Format(object value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ClrType.IsInstanceOfType(value) == false)
                throw new ArgumentException($"A {value.GetType().Name} is not a value of column type {Name} ({ClrType.Name}).", nameof(value));

            return FormatObject(value);
        }

        /// <summary>
        /// Gets the type's name.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;

        /// <summary>
        /// Gets or sets the store the type belongs to: graph ownership checks compare it.
        /// </summary>
        internal ValueStore? Store { get; set; }

        /// <summary>
        /// Gets the engine column type: <c>ColumnTy.Id</c> for a sort or a container, <c>ColumnTy.Base</c> of the codec
        /// otherwise.
        /// </summary>
        internal abstract Relations.ColumnTy EngineType { get; }

        /// <summary>
        /// Gets the type's extraction sort: an <c>EqSort</c> for a sort, a <c>ContainerSort</c> for a container, a
        /// <c>BaseSort</c> that formats like <see cref="Format"/> for any other value type. One instance per column
        /// type.
        /// </summary>
        /// <exception cref="InvalidOperationException">The type is the internal unit type, which extraction never
        /// sees.</exception>
        internal abstract Relations.Sort ExtractionSort { get; }

        /// <summary>
        /// Encodes a value into its cell, interning it when its codec does not box it inline.
        /// </summary>
        /// <param name="value">The value, of <see cref="ClrType"/>.</param>
        /// <returns>The cell.</returns>
        internal abstract uint EncodeObject(object value);

        /// <summary>
        /// Gets the cell of a value that is inline or interned already, without interning: a read never grows the
        /// graph's interning pools, which are permanent.
        /// </summary>
        /// <param name="value">The value, of <see cref="ClrType"/>.</param>
        /// <param name="cell">The cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the value has a cell already.</returns>
        internal abstract bool TryEncodeExistingObject(object value, out uint cell);

        /// <summary>
        /// Encodes a non-null value a primitive returned in <paramref name="scope"/>, as the typed
        /// <c>EncodeIn</c> does: a class, or the classes a container holds, checked usable there.
        /// </summary>
        /// <param name="value">The value, of the type's CLR type.</param>
        /// <param name="scope">The scope the call runs in.</param>
        /// <returns>The cell.</returns>
        internal abstract uint EncodeInObject(object value, Scope? scope);

        /// <summary>
        /// Compares two non-null values of the type in content order, as the typed <c>CompareValues</c> does.
        /// </summary>
        /// <param name="x">One value.</param>
        /// <param name="y">The other value.</param>
        /// <returns>Negative, zero or positive.</returns>
        internal abstract int CompareObjects(object x, object y);

        /// <summary>
        /// Gets whether the type's values hold classes: a container of a sort, or of such containers, egglog's eq-sort
        /// container (<c>is_eq_container_sort</c>). Its cell is a container id that the union-find rebuilds, as a
        /// class's; its value is not a class, so it is never unioned and is no constructor's output.
        /// </summary>
        internal virtual bool HoldsClasses => false;

        /// <summary>
        /// Gets whether a cell of the type names classes: a sort's, or a container's that holds classes, whose cell
        /// changes when classes merge, as egglog's eq sorts and eq-sort containers (<c>do_rebuild</c>).
        /// </summary>
        internal bool InvolvesClasses => Kind == ColumnKind.Sort || HoldsClasses;

        /// <summary>
        /// Encodes a host's value in <paramref name="scope"/>: each class it holds usable there and canonical through
        /// <paramref name="find"/>, and a container of classes interned at the root; any other value as
        /// <see cref="EncodeObject"/> does.
        /// </summary>
        /// <param name="value">The value, of <see cref="ClrType"/>.</param>
        /// <param name="scope">The scope written.</param>
        /// <param name="find">The canonical id of a class cell.</param>
        /// <param name="openBatch">The origin of an open Write batch whose classes the scope may use; <c>null</c> for
        /// none.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="ArgumentException">A class is not usable in the scope.</exception>
        /// <exception cref="InvalidOperationException">The value holds classes, the scope is not a writable root, and the
        /// root holds no such container (<see cref="UninternedContainerException"/>).</exception>
        internal abstract uint EncodeCanonicalObject(object value, Scope scope, Func<uint, uint> find, ScopeOrigin? openBatch);

        /// <summary>
        /// Gets the cell of a host's value in <paramref name="scope"/> without interning, as
        /// <see cref="TryEncodeExistingObject"/> does, each class it holds usable there and canonical through
        /// <paramref name="find"/>, and a container of classes canonical too.
        /// </summary>
        /// <param name="value">The value, of <see cref="ClrType"/>.</param>
        /// <param name="scope">The scope read.</param>
        /// <param name="find">The canonical id of a class cell.</param>
        /// <param name="cell">The cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the value has a cell already.</returns>
        /// <exception cref="ArgumentException">A class is not usable in the scope.</exception>
        internal abstract bool TryEncodeCanonicalObject(object value, Scope scope, Func<uint, uint> find, out uint cell);

        /// <summary>
        /// Gets a host's value as <paramref name="scope"/> sees it: each class it holds usable there and canonical through
        /// <paramref name="find"/>, so that two values compare as egglog, which interns them, would compare their cells.
        /// Any other value as it is.
        /// </summary>
        /// <param name="value">The value, of <see cref="ClrType"/>.</param>
        /// <param name="scope">The scope read.</param>
        /// <param name="find">The canonical id of a class cell.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">A class is not usable in the scope.</exception>
        internal abstract object CanonicalObject(object value, Scope scope, Func<uint, uint> find);

        /// <summary>
        /// Gets whether two non-null values of this type are one value as its codec interns them, so that a read can
        /// compare two values no write interned as egglog, which interns them, compares their cells.
        /// </summary>
        /// <param name="x">One value.</param>
        /// <param name="y">The other value.</param>
        /// <returns>Whether they would intern as one cell.</returns>
        internal abstract bool ObjectsEqual(object x, object y);

        /// <summary>
        /// Decodes a cell: a value, or for a sort a class with <paramref name="scope"/>'s origin.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="scope">The scope serving the call; needed for a sort.</param>
        /// <returns>The value.</returns>
        internal abstract object DecodeObject(uint cell, Scope? scope);

        /// <summary>
        /// Formats a value already known to be of <see cref="ClrType"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The literal.</returns>
        internal abstract string FormatObject(object value);

        /// <summary>
        /// Formats a value already known to be of <see cref="ClrType"/> as its engine codec formats its cell
        /// (<c>BaseValues.Format</c>, egglog's <c>BaseValuePrinter</c>), which needs no cell: Rust's <c>Debug</c> for
        /// the types with a Rust counterpart, an enum or an <c>int</c> as the <c>long</c> it is stored as, and a
        /// container as its literal.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        /// <exception cref="InvalidOperationException">The type is a sort, which has no codec.</exception>
        internal abstract string FormatDebug(object value);

        /// <summary>
        /// Formats a cell: its decoded value's literal, or <c>#17</c> for a class.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <returns>The literal.</returns>
        internal abstract string FormatCell(uint cell);

        /// <summary>
        /// Compares two cells in content order (see <see cref="ColumnType{T}.Compare"/>).
        /// </summary>
        /// <param name="x">One cell.</param>
        /// <param name="y">The other cell.</param>
        /// <returns>The order.</returns>
        internal abstract int CompareCells(uint x, uint y);

        /// <summary>
        /// Gets whether values of the type can be kept in <see cref="ElementOrder{T}"/> order, as the elements of a
        /// set, a multiset or a map's keys are, and as <see cref="ValueLattice.Max{T}"/> orders them: a string or a
        /// comparable type, or a container whose elements are.
        /// </summary>
        internal abstract bool IsOrdered { get; }

        /// <summary>
        /// Gets the class a value of a sort wraps.
        /// </summary>
        /// <param name="value">The value, of <see cref="ClrType"/>.</param>
        /// <returns>The class.</returns>
        /// <exception cref="InvalidOperationException">The type is not a sort.</exception>
        internal virtual ClassId ClassOf(object value) => throw new InvalidOperationException($"{Name} is not a sort.");

        /// <summary>
        /// Wraps a class into a value of a sort.
        /// </summary>
        /// <param name="id">The class.</param>
        /// <returns>The value, of <see cref="ClrType"/>.</returns>
        /// <exception cref="InvalidOperationException">The type is not a sort.</exception>
        internal virtual object Wrap(ClassId id) => throw new InvalidOperationException($"{Name} is not a sort.");

    }

}
