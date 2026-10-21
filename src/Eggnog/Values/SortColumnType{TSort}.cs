using System;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// A sort: cells are class ids (<c>ColumnTy.Id</c>). Encoding takes the value's cell (the callers check the class
    /// with <see cref="ClassChecks.Require"/> in the scope at hand); decoding makes a <see cref="ClassId"/> with the
    /// scope's origin; the extraction sort is an <c>EqSort</c> of the sort's name.
    /// </summary>
    /// <typeparam name="TSort">The sort.</typeparam>
    internal sealed class SortColumnType<TSort> : ColumnType<TSort> where TSort : struct, ISort<TSort>
    {

        readonly Relations.EqSort sort;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        public SortColumnType(string name)
            : base(name, ColumnKind.Sort)
        {
            sort = new Relations.EqSort(name);
        }

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => Relations.ColumnTy.Id;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <inheritdoc />
        internal override uint Encode(TSort value)
        {
            var id = value.Id;
            if (id.IsDefault)
                throw new ArgumentException("default(ClassId) names no class", nameof(value));

            return id.Cell;
        }

        /// <inheritdoc />
        internal override uint EncodeIn(TSort value, Scope? scope)
        {
            if (scope is not null && ClassChecks.Check(scope.Origin, value.Id, null) is { } error)
                throw new InvalidOperationException($"A primitive returned a class this scope cannot use: {error}.");

            return Encode(value);
        }

        /// <inheritdoc />
        internal override bool TryEncodeExisting(TSort value, out uint cell)
        {
            var id = value.Id;
            cell = id.Cell;
            return id.IsDefault == false;
        }

        /// <inheritdoc />
        internal override TSort Decode(uint cell, Scope? scope)
        {
            if (scope is null)
                throw new InvalidOperationException($"A cell of sort {Name} decodes only in a scope, whose origin the class carries.");

            return TSort.FromId(new ClassId(cell, scope.Origin));
        }

        /// <inheritdoc />
        internal override string FormatValue(TSort value) => value.Id.ToString();

        /// <inheritdoc />
        internal override int CompareValues(TSort x, TSort y) => x.Id.Cell.CompareTo(y.Id.Cell);

        /// <inheritdoc />
        internal override string FormatCell(uint cell) => "#" + cell.ToString(CultureInfo.InvariantCulture);

        /// <inheritdoc />
        internal override int CompareCells(uint x, uint y) => x.CompareTo(y);

        /// <inheritdoc />
        internal override ClassId ClassOf(object value) => ((TSort)value).Id;

        /// <inheritdoc />
        internal override object Wrap(ClassId id) => TSort.FromId(id);

    }

}
