namespace Eggnog
{

    /// <summary>
    /// The mirror of one value-only table under one tag: the engine function <c>T @tag</c> that scoped atoms read, and
    /// the copy rules that keep it equal to the tagged scope's own view of <c>T</c>: one copies the rows that are not
    /// subsumed, and for a subsumable relation a second subsumes in the mirror the rows that are, so that scoped atoms
    /// skip exactly the rows the tagged scope's own atoms skip.
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
        /// <param name="subsumeRule">The copy rule of subsumed rows, for a subsumable relation; <c>null</c>
        /// otherwise.</param>
        public MirrorTable(TableHandle source, ScopeTag tag, TableHandle handle, CompiledRule copyRule, CompiledRule? subsumeRule)
        {
            Source = source;
            Tag = tag;
            Handle = handle;
            CopyRule = copyRule;
            SubsumeRule = subsumeRule;
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
        /// Gets the mirror's name, <c>{source} @{tag}</c>: with a space, so never an egglog symbol, and so never the name
        /// of a table an egglog program defines, <c>{source}@{tag}</c> among them. Mirrors are told apart by their source
        /// and tag, not by name.
        /// </summary>
        public string Name => Handle.Name;

        /// <summary>
        /// Gets the copy rule: <c>T(x…) → T @tag(x…)</c> for a relation, <c>T(x…) = v → T @tag(x…) = v</c> for a function
        /// (merged by its lattice). Semi-naive and inheritable; it runs only in mirror syncs.
        /// </summary>
        public CompiledRule CopyRule { get; }

        /// <summary>
        /// Gets the copy rule of subsumed rows: it subsumes in the mirror each row the tagged scope sees subsumed. Only
        /// a subsumable relation has one; <c>null</c> otherwise.
        /// </summary>
        public CompiledRule? SubsumeRule { get; }

        /// <inheritdoc />
        public override string ToString() => Name;

    }

}
