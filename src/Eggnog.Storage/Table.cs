using System;
using System.Collections.Generic;

namespace Eggnog.Storage
{

    /// <summary>
    /// A table defined in a <see cref="Store"/>. Every context of the store has its own view of it.
    /// </summary>
    public sealed class Table
    {

        internal Table(Store store, int index, TableSchema schema)
        {
            Store = store;
            Index = index;
            Schema = schema;
            KeyCount = schema.Keys.Count;
            Arity = schema.Arity;
            HasOutput = schema.Output is not null;
            OutputIsId = schema.Output == ColumnKind.Id;

            IsId = new bool[Arity];
            var ids = new List<int>();
            var keyIds = new List<int>();

            for (var i = 0; i < KeyCount; i++)
            {
                if (schema.Keys[i] == ColumnKind.Id)
                {
                    IsId[i] = true;
                    ids.Add(i);
                    keyIds.Add(i);
                }
            }

            if (OutputIsId)
            {
                IsId[KeyCount] = true;
                ids.Add(KeyCount);
            }

            IdColumns = ids.ToArray();
            KeyIdColumns = keyIds.ToArray();
        }

        /// <summary>
        /// Gets the store the table belongs to.
        /// </summary>
        public Store Store { get; }

        /// <summary>
        /// Gets the table's schema.
        /// </summary>
        public TableSchema Schema { get; }

        /// <summary>
        /// Gets the table's name.
        /// </summary>
        public string Name => Schema.Name;

        /// <inheritdoc />
        public override string ToString() => Name;

        internal int Index { get; }

        internal int KeyCount { get; }

        internal int Arity { get; }

        internal bool HasOutput { get; }

        internal bool OutputIsId { get; }

        /// <summary>
        /// Whether each column, keys then output, holds an identifier.
        /// </summary>
        internal bool[] IsId { get; }

        /// <summary>
        /// The columns, keys and output alike, that hold identifiers.
        /// </summary>
        internal int[] IdColumns { get; }

        /// <summary>
        /// The key columns that hold identifiers.
        /// </summary>
        internal int[] KeyIdColumns { get; }

        /// <summary>
        /// Combines two values for one key.
        /// </summary>
        internal ulong MergeValues(ulong current, ulong incoming)
        {
            if (current == incoming)
                return current;

            if (Schema.Merge is null)
                throw new InvalidOperationException($"Table '{Name}' was given two values, {current} and {incoming}, for one key and has no merge function.");

            return Schema.Merge(current, incoming);
        }

    }

}
