using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The logical schema of a table for <see cref="ReferenceView"/>: the spike's <c>Table</c>/<c>TableSchema</c>
    /// (src/Eggnog.Storage/TableSchema.cs). Columns exclude the timestamp the engine's tables add.
    /// </summary>
    sealed class ReferenceTable
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The name, for traces and descriptions.</param>
        /// <param name="keyCount">The number of key columns.</param>
        /// <param name="isId">Whether each logical column holds an id.</param>
        /// <param name="hasOutput">Whether the column after the key is an output the key determines.</param>
        /// <param name="mergeValues">How two non-id outputs merge (a lattice), or <c>null</c>.</param>
        public ReferenceTable(string name, int keyCount, bool[] isId, bool hasOutput, Func<uint, uint, uint>? mergeValues = null)
        {
            Name = name;
            KeyCount = keyCount;
            IsId = isId;
            HasOutput = hasOutput;
            MergeValues = mergeValues ?? ((a, _) => a);
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the number of key columns.
        /// </summary>
        public int KeyCount { get; }

        /// <summary>
        /// Gets the number of logical columns.
        /// </summary>
        public int Arity => IsId.Length;

        /// <summary>
        /// Gets whether each logical column holds an id.
        /// </summary>
        public bool[] IsId { get; }

        /// <summary>
        /// Gets whether the table has an output column, at <see cref="KeyCount"/>.
        /// </summary>
        public bool HasOutput { get; }

        /// <summary>
        /// Gets whether the output is an id, merged by union.
        /// </summary>
        public bool OutputIsId => HasOutput && IsId[KeyCount];

        /// <summary>
        /// Gets how two non-id outputs merge.
        /// </summary>
        public Func<uint, uint, uint> MergeValues { get; }

        /// <inheritdoc />
        public override string ToString() => Name;

    }

}
