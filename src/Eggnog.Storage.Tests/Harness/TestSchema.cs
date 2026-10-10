using System;

namespace Eggnog.Storage.Tests.Harness
{

    /// <summary>
    /// The tables random programs run over: a binary and a unary constructor, a lattice, a relation mixing
    /// identifiers with values, and a constructor keyed by a value alone.
    /// </summary>
    public sealed class TestSchema
    {

        public TestSchema(Store store)
        {
            Pair = store.DefineTable(TableSchema.Constructor("pair", ColumnKind.Id, ColumnKind.Id));
            Wrap = store.DefineTable(TableSchema.Constructor("wrap", ColumnKind.Id));
            Low = store.DefineTable(TableSchema.Lattice("low", Math.Min, ColumnKind.Id));
            Tag = store.DefineTable(TableSchema.Relation("tag", ColumnKind.Id, ColumnKind.Value));
            Named = store.DefineTable(TableSchema.Constructor("named", ColumnKind.Value));
        }

        public Table Pair { get; }

        public Table Wrap { get; }

        public Table Low { get; }

        public Table Tag { get; }

        public Table Named { get; }

    }

}
