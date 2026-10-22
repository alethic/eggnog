using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A <see cref="LiftTable"/> resolved against the e-graph's definitions for one lift: the function's layout, which
    /// key columns hold ids, and whether its output does.
    /// </summary>
    internal sealed class LiftedTable
    {

        /// <summary>
        /// Initializes the resolved form of <paramref name="table"/>.
        /// </summary>
        /// <param name="table">The spec's entry.</param>
        /// <param name="info">The function's definition.</param>
        public LiftedTable(LiftTable table, FunctionInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            Table = table;
            Info = info;
            KeyCount = info.Math.KeyCount;
            IdKeys = new bool[KeyCount];
            var schema = info.Schema;
            for (var i = 0; i < KeyCount; i++)
            {
                IdKeys[i] = schema[i].IsId;
                HasIdKey |= IdKeys[i];
            }

            OutputIsId = info.OutputType.IsId && table.ValueOutput == false;
        }

        /// <summary>
        /// Gets the spec's entry.
        /// </summary>
        public LiftTable Table { get; }

        /// <summary>
        /// Gets the function's definition.
        /// </summary>
        public FunctionInfo Info { get; }

        /// <summary>
        /// Gets the number of key columns.
        /// </summary>
        public int KeyCount { get; }

        /// <summary>
        /// Gets which key columns hold ids.
        /// </summary>
        public bool[] IdKeys { get; }

        /// <summary>
        /// Gets whether some key column holds ids: only then can an ancestor row name a group root in its key.
        /// </summary>
        public bool HasIdKey { get; }

        /// <summary>
        /// Gets whether the output holds an id a case's find may move: a class, or a container of classes; a container
        /// of values (<see cref="LiftTable.ValueOutput"/>) is a base output.
        /// </summary>
        public bool OutputIsId { get; }

    }

}
