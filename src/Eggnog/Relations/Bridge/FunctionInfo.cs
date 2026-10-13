using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// What an <see cref="EGraph"/> keeps about a declared function: egglog-bridge's <c>FunctionInfo</c>
    /// (egglog-bridge/src/lib.rs:982-997), without the ids of the rule-based rebuild rules, which the engine does not
    /// build (decision 10: rebuild is the native path only).
    /// </summary>
    /// <remarks>
    /// Immutable once the function is added.
    /// </remarks>
    internal sealed class FunctionInfo
    {

        readonly ColumnTy[] schema;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The function's table.</param>
        /// <param name="schema">The column types, keys then output; owned by the new instance.</param>
        /// <param name="defaultValue">What a lookup of a missing key does.</param>
        /// <param name="merge">The declared merge function.</param>
        /// <param name="canSubsume">Whether the table has a subsumption column.</param>
        /// <param name="name">The function's name.</param>
        public FunctionInfo(TableId table, ColumnTy[] schema, DefaultVal defaultValue, MergeFn merge, bool canSubsume, string name)
        {
            Table = table;
            this.schema = schema;
            Default = defaultValue;
            Merge = merge;
            CanSubsume = canSubsume;
            Name = name;
            Math = new SchemaMath(schema.Length, canSubsume);
        }

        /// <summary>
        /// Gets the function's table.
        /// </summary>
        public TableId Table { get; }

        /// <summary>
        /// Gets the column types, keys then output.
        /// </summary>
        public ReadOnlySpan<ColumnTy> Schema => schema;

        /// <summary>
        /// Gets what a lookup of a missing key does.
        /// </summary>
        public DefaultVal Default { get; }

        /// <summary>
        /// Gets the declared merge function.
        /// </summary>
        public MergeFn Merge { get; }

        /// <summary>
        /// Gets whether the table has a subsumption column.
        /// </summary>
        public bool CanSubsume { get; }

        /// <summary>
        /// Gets the function's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the layout of the function's table.
        /// </summary>
        public SchemaMath Math { get; }

        /// <summary>
        /// Gets the type of the output column. egglog's <c>ret_ty</c>.
        /// </summary>
        public ColumnTy OutputType => schema[^1];

        /// <inheritdoc />
        public override string ToString() => $"{Name} ({Table})";

    }

}
