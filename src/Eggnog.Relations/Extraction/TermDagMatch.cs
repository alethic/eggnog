using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the entry of a <see cref="TermDag"/>'s hash-cons index that holds a probed term: same hash, same kind,
    /// same head or literal, same children. The probed children sit in the DAG's child buffer past its committed end, so
    /// probing copies nothing.
    /// </summary>
    internal readonly struct TermDagMatch : IEntryMatch<TermDagEntry>
    {

        readonly TermDag dag;
        readonly ulong hash;
        readonly TermKind kind;
        readonly string? name;
        readonly TermLiteral literal;
        readonly int childStart;
        readonly int childCount;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="dag">The DAG probed.</param>
        /// <param name="hash">The probed term's hash.</param>
        /// <param name="kind">The probed term's kind.</param>
        /// <param name="name">Its head or variable name; <c>null</c> for a literal.</param>
        /// <param name="literal">Its literal; <c>default</c> otherwise.</param>
        /// <param name="childStart">Where its children start in the DAG's child buffer.</param>
        /// <param name="childCount">How many children it has.</param>
        public TermDagMatch(TermDag dag, ulong hash, TermKind kind, string? name, TermLiteral literal, int childStart, int childCount)
        {
            this.dag = dag;
            this.hash = hash;
            this.kind = kind;
            this.name = name;
            this.literal = literal;
            this.childStart = childStart;
            this.childCount = childCount;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in TermDagEntry entry)
        {
            return entry.Hash == hash && dag.Holds(entry.Term, kind, name, literal, childStart, childCount);
        }

    }

}
