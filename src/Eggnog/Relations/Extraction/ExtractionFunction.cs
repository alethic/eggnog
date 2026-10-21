using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A function of an <see cref="ExtractionSchema"/>, resolved against its e-graph: its table and layout, the sort of
    /// every column, and egglog's derived extraction properties (<c>extraction_output_sort</c>,
    /// <c>extraction_num_children</c>, <c>extraction_term_name</c> and <c>extraction_output_index</c>,
    /// src/extract.rs:774-817), computed once.
    /// </summary>
    /// <remarks>
    /// Immutable.
    /// </remarks>
    internal sealed class ExtractionFunction
    {

        readonly Sort[] columns;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id">The function.</param>
        /// <param name="info">The e-graph's information about it.</param>
        /// <param name="columns">The sort of every schema column, keys then output; owned by the new instance.</param>
        /// <param name="signature">The declaration.</param>
        /// <param name="termName">The name its terms carry: its own, or its term constructor's for a view.</param>
        public ExtractionFunction(FunctionId id, FunctionInfo info, Sort[] columns, FunctionSignature signature, string termName)
        {
            Id = id;
            Name = info.Name;
            Table = info.Table;
            Math = info.Math;
            this.columns = columns;
            IsConstructor = signature.IsConstructor;
            Cost = signature.Cost;
            Unextractable = signature.Unextractable;
            Hidden = signature.Hidden;
            TermConstructor = signature.TermConstructor;
            TermName = termName;

            var keys = columns.Length - 1;
            if (signature.TermConstructor is null)
            {
                OutputIndex = keys;
                ChildCount = keys;
            }
            else
            {
                // A view's rows are [children..., class, view]: the class is its last key (extract.rs:804-817).
                OutputIndex = keys - 1;
                ChildCount = keys - 1;
            }

            OutputSort = columns[OutputIndex];
        }

        /// <summary>
        /// Gets the function.
        /// </summary>
        public FunctionId Id { get; }

        /// <summary>
        /// Gets the function's own name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the function's table.
        /// </summary>
        public TableId Table { get; }

        /// <summary>
        /// Gets the layout of the function's table.
        /// </summary>
        public SchemaMath Math { get; }

        /// <summary>
        /// Gets the sort of every schema column, keys then output.
        /// </summary>
        public ReadOnlySpan<Sort> Columns => columns;

        /// <summary>
        /// Gets the sorts of the key columns.
        /// </summary>
        public ReadOnlySpan<Sort> Inputs => columns.AsSpan(0, columns.Length - 1);

        /// <summary>
        /// Gets the sort of the output column.
        /// </summary>
        public Sort Output => columns[^1];

        /// <summary>
        /// Gets whether the function is a constructor.
        /// </summary>
        public bool IsConstructor { get; }

        /// <summary>
        /// Gets the declared head cost, or <c>null</c> for the default of 1.
        /// </summary>
        public ulong? Cost { get; }

        /// <summary>
        /// Gets whether the function is marked unextractable.
        /// </summary>
        public bool Unextractable { get; }

        /// <summary>
        /// Gets whether the function is marked hidden.
        /// </summary>
        public bool Hidden { get; }

        /// <summary>
        /// Gets the term constructor of a view, or <c>null</c>.
        /// </summary>
        public FunctionId? TermConstructor { get; }

        /// <summary>
        /// Gets whether the function is a view: egglog's <c>term_constructor.is_some()</c>.
        /// </summary>
        public bool IsView => TermConstructor is not null;

        /// <summary>
        /// Gets the name of the terms this function's rows extract to. egglog's <c>extraction_term_name</c>.
        /// </summary>
        public string TermName { get; }

        /// <summary>
        /// Gets the column holding the e-class a row belongs to. egglog's <c>extraction_output_index</c>.
        /// </summary>
        public int OutputIndex { get; }

        /// <summary>
        /// Gets the number of leading key columns that are the children of a row's term. egglog's
        /// <c>extraction_num_children</c>.
        /// </summary>
        public int ChildCount { get; }

        /// <summary>
        /// Gets the sort of the e-classes the rows belong to. egglog's <c>extraction_output_sort</c>.
        /// </summary>
        public Sort OutputSort { get; }

        /// <inheritdoc />
        public override string ToString() => $"{Name} ({Id})";

    }

}
