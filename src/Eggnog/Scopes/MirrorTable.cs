namespace Eggnog
{

    /// <summary>
    /// The mirror of one value-only table under one tag (§6.8): the engine function <c>T@tag</c> that scoped atoms read,
    /// and the copy rule that keeps it equal to the tagged scope's own view of <c>T</c> (invariant M).
    /// </summary>
    internal sealed class MirrorTable
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="source">The table mirrored.</param>
        /// <param name="tag">The tag it is read under.</param>
        /// <param name="handle">The internal handle over the mirror's engine function.</param>
        /// <param name="copyRule">The copy rule.</param>
        public MirrorTable(TableHandle source, ScopeTag tag, TableHandle handle, CompiledRule copyRule)
        {
            Source = source;
            Tag = tag;
            Handle = handle;
            CopyRule = copyRule;
        }

        /// <summary>
        /// Gets the table mirrored.
        /// </summary>
        public TableHandle Source { get; }

        /// <summary>
        /// Gets the tag it is read under.
        /// </summary>
        public ScopeTag Tag { get; }

        /// <summary>
        /// Gets the internal handle of the source's kind over the mirror's engine function; never listed in
        /// <see cref="EGraph.Tables"/>.
        /// </summary>
        public TableHandle Handle { get; }

        /// <summary>
        /// Gets the mirror's engine function.
        /// </summary>
        public Relations.FunctionId Function => Handle.Function;

        /// <summary>
        /// Gets the mirror's name, <c>{source}@{tag}</c>: unique, since table names contain no '@' and tag names are
        /// unique per graph.
        /// </summary>
        public string Name => Handle.Name;

        /// <summary>
        /// Gets the copy rule: <c>T(x…) → T@tag(x…)</c> for a relation, <c>T(x…) = v → T@tag(x…) = v</c> for a function
        /// (merged by its lattice). Semi-naive and inheritable; it runs only in mirror syncs (D48).
        /// </summary>
        public CompiledRule CopyRule { get; }

        /// <inheritdoc />
        public override string ToString() => Name;

    }

}
