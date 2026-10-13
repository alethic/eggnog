using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One table of the context differentials, as the engine and the naive reference each see it: its engine id and its
    /// logical shape, the columns a row has without the engine's timestamp: keys, then an output for a function.
    /// </summary>
    /// <remarks>
    /// Logical rows are what the facade and <see cref="ReferenceContextView"/> exchange. The engine stores each with
    /// its timestamp appended (<see cref="EngineArity"/>), which every engine table of the schema has.
    /// </remarks>
    sealed class LogicalTable
    {

        /// <summary>
        /// Initializes a table.
        /// </summary>
        /// <param name="name">The table's name.</param>
        /// <param name="id">The engine table's id.</param>
        /// <param name="keyIsId">Whether each key column holds an id.</param>
        /// <param name="output">The output column's kind: <c>null</c> for a relation, <c>true</c> for an id (a
        /// constructor), <c>false</c> for a value combined by <paramref name="merge"/>.</param>
        /// <param name="merge">How two values for one key combine, for a value output.</param>
        public LogicalTable(string name, TableId id, bool[] keyIsId, bool? output, Func<ulong, ulong, ulong>? merge)
        {
            Name = name;
            Id = id;
            KeyCount = keyIsId.Length;
            HasOutput = output is not null;
            OutputIsId = output == true;
            Merge = merge;
            Arity = KeyCount + (HasOutput ? 1 : 0);
            IsId = new bool[Arity];
            Array.Copy(keyIsId, IsId, KeyCount);
            if (OutputIsId)
                IsId[KeyCount] = true;
        }

        /// <summary>
        /// Gets the table's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the engine table's id.
        /// </summary>
        public TableId Id { get; }

        /// <summary>
        /// Gets the number of key columns.
        /// </summary>
        public int KeyCount { get; }

        /// <summary>
        /// Gets the number of logical columns: keys, then the output if any.
        /// </summary>
        public int Arity { get; }

        /// <summary>
        /// Gets the number of engine columns: the logical ones plus the timestamp.
        /// </summary>
        public int EngineArity => Arity + 1;

        /// <summary>
        /// Gets whether the table is a function.
        /// </summary>
        public bool HasOutput { get; }

        /// <summary>
        /// Gets whether the output is an id (a constructor).
        /// </summary>
        public bool OutputIsId { get; }

        /// <summary>
        /// Gets whether each logical column holds an id.
        /// </summary>
        public bool[] IsId { get; }

        /// <summary>
        /// Gets how two values for one key combine, or <c>null</c>.
        /// </summary>
        public Func<ulong, ulong, ulong>? Merge { get; }

        /// <summary>
        /// Combines two values of a value output.
        /// </summary>
        public ulong MergeValues(ulong current, ulong incoming)
        {
            if (current == incoming)
                return current;

            return Merge?.Invoke(current, incoming) ?? throw new InvalidOperationException($"Table {Name} has no merge function.");
        }

        /// <inheritdoc />
        public override string ToString() => Name;

    }

}
