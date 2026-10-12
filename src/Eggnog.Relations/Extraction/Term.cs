using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A view of one term of a <see cref="TermDag"/>: egglog's <c>Term</c> (src/termdag.rs:13-17), read in place. A
    /// literal (<see cref="TermKind.Lit"/>), a variable (<see cref="TermKind.Var"/>) or an application of
    /// <see cref="Name"/> to <see cref="Children"/> (<see cref="TermKind.App"/>).
    /// </summary>
    /// <remarks>
    /// A two-field struct: copying it is free, and every accessor is an array read in the DAG. Two views are equal when
    /// they name the same term of the same DAG, which, the DAG being hash-consed, is when the terms are equal.
    /// </remarks>
    internal readonly struct Term : IEquatable<Term>
    {

        readonly TermDag dag;

        /// <summary>
        /// Initializes a new instance over a term known to be in <paramref name="dag"/>.
        /// </summary>
        /// <param name="dag">The DAG.</param>
        /// <param name="id">The term's id.</param>
        internal Term(TermDag dag, TermId id)
        {
            this.dag = dag;
            Id = id;
        }

        /// <summary>
        /// Gets the term's id.
        /// </summary>
        public TermId Id { get; }

        /// <summary>
        /// Gets the term's kind.
        /// </summary>
        public TermKind Kind => dag.KindOf(Id);

        /// <summary>
        /// Gets the head symbol of an application or the name of a variable.
        /// </summary>
        /// <exception cref="InvalidOperationException">The term is a literal.</exception>
        public string Name => dag.NameOf(Id) ?? throw new InvalidOperationException("A literal has no name.");

        /// <summary>
        /// Gets the literal of a literal term.
        /// </summary>
        /// <exception cref="InvalidOperationException">The term is not a literal.</exception>
        public TermLiteral Literal
        {
            get => Kind == TermKind.Lit ? dag.LiteralOf(Id) : throw new InvalidOperationException("The term is not a literal.");
        }

        /// <summary>
        /// Gets the number of children: zero unless the term is an application with arguments.
        /// </summary>
        public int Arity => dag.ChildrenOf(Id).Length;

        /// <summary>
        /// Gets the children of an application, empty for other terms. Valid until the DAG next grows.
        /// </summary>
        public ReadOnlySpan<TermId> Children => dag.ChildrenOf(Id);

        /// <summary>
        /// Gets the child at <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The child's position.</param>
        /// <returns>The child.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a child position.</exception>
        public Term Child(int index)
        {
            var children = dag.ChildrenOf(Id);
            if ((uint)index >= (uint)children.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"The term has {children.Length} children.");

            return new Term(dag, children[index]);
        }

        /// <inheritdoc />
        public bool Equals(Term other) => ReferenceEquals(dag, other.dag) && Id == other.Id;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Term other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => Id.Rep;

        /// <summary>
        /// Renders the term as an s-expression (<see cref="TermDag.ToString(TermId)"/>).
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString() => dag is null ? string.Empty : dag.ToString(Id);

        /// <summary>
        /// Compares two views for equality.
        /// </summary>
        /// <param name="left">The first view.</param>
        /// <param name="right">The second view.</param>
        /// <returns>Whether they name the same term of the same DAG.</returns>
        public static bool operator ==(Term left, Term right) => left.Equals(right);

        /// <summary>
        /// Compares two views for inequality.
        /// </summary>
        /// <param name="left">The first view.</param>
        /// <param name="right">The second view.</param>
        /// <returns>Whether they name different terms or DAGs.</returns>
        public static bool operator !=(Term left, Term right) => left.Equals(right) == false;

    }

}
