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
        /// The fact of a call, as egglog's bare call is one. In a body, a query or a host check, it holds where a
        /// function's or a constructor's application has a row, or where a primitive's has a result; one with none drops
        /// the match. In a head or a host write, it is egglog's action of an expression for its effect alone, a
        /// <c>let</c> of a value never used (src/core.rs:765-770): a constructor's term is made, a primitive runs, a
        /// write primitive's unions take effect, and a call with no result fails the rule or the fact, as egglog panics.
        /// </summary>
        /// <param name="application">The application of a function, a constructor or a primitive.</param>
        /// <returns>The fact.</returns>
        /// <exception cref="ArgumentException"><paramref name="application"/> is a variable or a constant.</exception>
        public static Fact Exists(Term application)
        {
            ArgumentNullException.ThrowIfNull(application);
            if (application.Shape.Kind is not (TermShapeKind.Application or TermShapeKind.Computation))
                throw new ArgumentException("Fact.Exists takes an application of a function, a constructor or a primitive.", nameof(application));

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
        /// Scopes every table application inside <paramref name="facts"/>, nested ones included, to the nearest
        /// ancestor-or-self of the running scope tagged <paramref name="scope"/>: in a rule body, the applications read
        /// that scope's view through mirrors; in a host check, they read that scope directly. In a rule or query,
        /// nesting <c>Fact.In</c>, a handle already scoped to a different tag, a table with a sort column (a
        /// constructor, say), and any use in a head are <see cref="RuleDefinitionException"/>s; a tag of another graph
        /// is an <see cref="ArgumentException"/> at the builder's <c>When</c> or <c>Then</c>. In a host write it is an
        /// <see cref="ArgumentException"/> (read-only), and in a host check, nesting or a handle scoped to another tag
        /// is one too.
        /// </summary>
        /// <param name="scope">The tag of the scope to read.</param>
        /// <param name="facts">The facts.</param>
        /// <returns>The scoped fact.</returns>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">A table inside the facts belongs to another graph than the
        /// tag.</exception>
        public static Fact In(ScopeTag scope, params Fact[] facts)
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(facts);
            var copy = new Fact[facts.Length];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = facts[i] ?? throw new ArgumentNullException(nameof(facts), $"Fact {i} is null.");
                if (ScopedFact.FindForeignTable(copy[i], scope.Graph) is { } foreign)
                    throw new ArgumentException($"Scope tag '{scope.Name}' belongs to another graph than table '{foreign.Name}'.", nameof(scope));
            }

            return new ScopedFact(scope, copy);
        }

        /// <summary>
        /// Renders the fact as an s-expression: <c>(present p)</c>, <c>(= d (dom p))</c>, <c>(!= a b)</c>; a scoped
        /// application renders as <c>(present @schema p)</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => SExpression.Render(this);

        /// <summary>
        /// Gets what the fact is.
        /// </summary>
        internal abstract FactKind Kind { get; }

        /// <summary>
        /// The untyped equality: the terms' types are checked where the fact is used.
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
        /// The untyped disequality.
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
