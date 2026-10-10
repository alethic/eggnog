using System;
using System.Collections.Generic;

namespace Eggnog.Storage
{

    /// <summary>
    /// Describes a table: key columns and, for a function, one output column the keys determine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table with no output is a relation: a set of rows. A table with an output is a function: at most one
    /// row per key. When two rows come to share a key — because they were inserted that way, or because merging
    /// classes made their keys equal — their outputs are combined. An <see cref="ColumnKind.Id"/> output is
    /// combined by merging the two classes, which is congruence. A <see cref="ColumnKind.Value"/> output is
    /// combined by <see cref="Merge"/>.
    /// </para>
    /// </remarks>
    public sealed class TableSchema
    {

        /// <summary>
        /// Describes a relation: a set of rows with no output.
        /// </summary>
        /// <param name="name">The table's name.</param>
        /// <param name="keys">The key columns.</param>
        /// <returns>The schema.</returns>
        public static TableSchema Relation(string name, params ColumnKind[] keys) => new(name, keys);

        /// <summary>
        /// Describes a function whose output is an e-class: a constructor, in e-graph terms.
        /// </summary>
        /// <param name="name">The table's name.</param>
        /// <param name="keys">The key columns.</param>
        /// <returns>The schema.</returns>
        public static TableSchema Constructor(string name, params ColumnKind[] keys) => new(name, keys, ColumnKind.Id);

        /// <summary>
        /// Describes a function whose output is a value combined by <paramref name="merge"/>.
        /// </summary>
        /// <param name="name">The table's name.</param>
        /// <param name="merge">How two values for one key combine.</param>
        /// <param name="keys">The key columns.</param>
        /// <returns>The schema.</returns>
        public static TableSchema Lattice(string name, MergeFunction merge, params ColumnKind[] keys) => new(name, keys, ColumnKind.Value, merge);

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The table's name.</param>
        /// <param name="keys">The key columns.</param>
        /// <param name="output">The output column, or <c>null</c> for a relation.</param>
        /// <param name="merge">
        /// How two values for one key combine. Only meaningful for a <see cref="ColumnKind.Value"/> output; where it
        /// is omitted, two different values for one key are an error.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="keys"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="merge"/> is given for an output that is not a value.</exception>
        public TableSchema(string name, IReadOnlyList<ColumnKind> keys, ColumnKind? output = null, MergeFunction? merge = null)
        {
            if (name is null)
                throw new ArgumentNullException(nameof(name));
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));
            if (merge is not null && output != ColumnKind.Value)
                throw new ArgumentException("A merge function applies only to a value output.", nameof(merge));

            Name = name;
            Keys = new List<ColumnKind>(keys).ToArray();
            Output = output;
            Merge = merge;
        }

        /// <summary>
        /// Gets the table's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the key columns.
        /// </summary>
        public IReadOnlyList<ColumnKind> Keys { get; }

        /// <summary>
        /// Gets the output column, or <c>null</c> for a relation.
        /// </summary>
        public ColumnKind? Output { get; }

        /// <summary>
        /// Gets how two values for one key combine.
        /// </summary>
        public MergeFunction? Merge { get; }

        /// <summary>
        /// Gets the number of columns in a row: the keys, then the output if there is one.
        /// </summary>
        public int Arity => Keys.Count + (Output is null ? 0 : 1);

    }

}
