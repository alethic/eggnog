using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Compares (row id, cells) pairs by id and cell contents, for assertions on scan output.
    /// </summary>
    sealed class RowTupleEquality : IEqualityComparer<(int Id, uint[] Row)>
    {

        /// <inheritdoc />
        public bool Equals((int Id, uint[] Row) x, (int Id, uint[] Row) y) => x.Id == y.Id && x.Row.AsSpan().SequenceEqual(y.Row);

        /// <inheritdoc />
        public int GetHashCode((int Id, uint[] Row) obj) => obj.Id;

    }

}
