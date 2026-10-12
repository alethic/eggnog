using System;

namespace Eggnog
{

    /// <summary>
    /// A fact: a relation row, an equality, a disequality, an existence or a filter. In a rule body a fact is a
    /// condition; in a head, a write; in a host write, a ground fact to assume; in a host check, a ground fact to test.
    /// Immutable; renders as an s-expression.
    /// </summary>
    public abstract class Fact
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        private protected Fact()
        {

        }

        /// <summary>
        /// The fact that a function or constructor application has a row.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <returns>The fact.</returns>
        /// <exception cref="ArgumentException"><paramref name="application"/> is not an application of a function or
        /// constructor.</exception>
        public static Fact Exists(Term application)
        {
            ArgumentNullException.ThrowIfNull(application);
            if (application.Shape.Kind != TermShapeKind.Application)
                throw new ArgumentException("Fact.Exists takes an application of a function or constructor.", nameof(application));

            return new ExistsFact(application);
        }

        /// <summary>
        /// A filter on a boolean computation: in a body only.
        /// </summary>
        /// <param name="condition">The condition.</param>
        /// <returns>The fact.</returns>
        public static Fact True(Term<bool> condition)
        {
            ArgumentNullException.ThrowIfNull(condition);
            return new FilterFact(condition);
        }

        /// <summary>
        /// Renders the fact as an s-expression: <c>(present p)</c>, <c>(= d (dom p))</c>, <c>(!= a b)</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => SExpression.Render(this);

        /// <summary>
        /// Gets what the fact is.
        /// </summary>
        internal abstract FactKind Kind { get; }

        /// <summary>
        /// The untyped equality (D13): the terms' types are checked where the fact is used.
        /// </summary>
        /// <param name="left">One side.</param>
        /// <param name="right">The other side.</param>
        /// <returns>The fact.</returns>
        internal static Fact Equal(Term left, Term right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            return new EqualityFact(left, right);
        }

        /// <summary>
        /// The untyped disequality (D13).
        /// </summary>
        /// <param name="left">One side.</param>
        /// <param name="right">The other side.</param>
        /// <returns>The fact.</returns>
        internal static Fact NotEqual(Term left, Term right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            return new NotEqualFact(left, right);
        }

    }

}
