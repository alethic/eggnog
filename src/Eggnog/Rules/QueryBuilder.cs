using System;

namespace Eggnog
{

    /// <summary>
    /// Collects one query (§6.6.4): its variables, and a body of <see cref="When"/> facts and <c>Where</c> filters, as in a
    /// rule. A match binds every declared variable, so each one must be bound by the body. Usable only inside the
    /// definition's callback.
    /// </summary>
    public sealed class QueryBuilder
    {

        readonly EGraph graph;
        readonly RuleDraft draft;
        bool closed;

        /// <summary>
        /// Initializes a builder for one query.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="name">The query's name.</param>
        internal QueryBuilder(EGraph graph, string name)
        {
            this.graph = graph;
            draft = new RuleDraft(name, this);
        }

        /// <summary>
        /// Declares a variable: a column of every match.
        /// </summary>
        /// <typeparam name="T">The CLR type of its values; a registered value type or a defined sort.</typeparam>
        /// <param name="name">The name, unique in the query.</param>
        /// <returns>The variable.</returns>
        public Var<T> Var<T>(string name)
        {
            ThrowIfClosed();
            NameRules.ValidateName(name, nameof(name));
            var variable = new Var<T>(this, name);
            graph.Values.TryGetType<T>(out var type);
            draft.Variables.Add((name, type, variable));
            return variable;
        }

        /// <summary>
        /// Declares two variables.
        /// </summary>
        /// <typeparam name="T1">The first variable's type.</typeparam>
        /// <typeparam name="T2">The second variable's type.</typeparam>
        /// <param name="name1">The first name.</param>
        /// <param name="name2">The second name.</param>
        /// <returns>The variables.</returns>
        public (Var<T1>, Var<T2>) Vars<T1, T2>(string name1, string name2) => (Var<T1>(name1), Var<T2>(name2));

        /// <summary>
        /// Declares three variables.
        /// </summary>
        /// <typeparam name="T1">The first variable's type.</typeparam>
        /// <typeparam name="T2">The second variable's type.</typeparam>
        /// <typeparam name="T3">The third variable's type.</typeparam>
        /// <param name="name1">The first name.</param>
        /// <param name="name2">The second name.</param>
        /// <param name="name3">The third name.</param>
        /// <returns>The variables.</returns>
        public (Var<T1>, Var<T2>, Var<T3>) Vars<T1, T2, T3>(string name1, string name2, string name3) =>
            (Var<T1>(name1), Var<T2>(name2), Var<T3>(name3));

        /// <summary>
        /// Adds body facts: relation rows and applications to join, equalities to unify or check, disequalities and
        /// filters.
        /// </summary>
        /// <param name="facts">The facts.</param>
        /// <exception cref="ArgumentException">The tag of a <see cref="Fact.In"/> belongs to another graph (D38).</exception>
        public void When(params Fact[] facts)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(facts);
            foreach (var fact in facts)
            {
                graph.CheckFactTags(fact ?? throw new ArgumentNullException(nameof(facts), "A fact is null."), nameof(facts));
                draft.Body.Add(fact);
            }
        }

        /// <summary>
        /// Adds a filter on one term with a lambda, which renders as <c>&lt;lambda&gt;</c>.
        /// </summary>
        /// <typeparam name="T1">The term's type.</typeparam>
        /// <param name="a">The term.</param>
        /// <param name="predicate">The filter: pure, deterministic and thread-safe; it must not call into any graph.</param>
        public void Where<T1>(Term<T1> a, Func<T1, bool> predicate) => Where(Compute.Of(a, predicate));

        /// <summary>
        /// Adds a filter on two terms with a lambda, which renders as <c>&lt;lambda&gt;</c>.
        /// </summary>
        /// <typeparam name="T1">The first term's type.</typeparam>
        /// <typeparam name="T2">The second term's type.</typeparam>
        /// <param name="a">The first term.</param>
        /// <param name="b">The second term.</param>
        /// <param name="predicate">The filter: pure, deterministic and thread-safe; it must not call into any graph.</param>
        public void Where<T1, T2>(Term<T1> a, Term<T2> b, Func<T1, T2, bool> predicate) => Where(Compute.Of(a, b, predicate));

        /// <summary>
        /// Adds a filter on a boolean term, e.g. <c>Primitives.Contains&lt;string&gt;().Of(d, v)</c>.
        /// </summary>
        /// <param name="condition">The condition.</param>
        public void Where(Term<bool> condition)
        {
            ThrowIfClosed();
            draft.Body.Add(Fact.True(condition));
        }

        /// <summary>
        /// Declares an untyped variable of a column type (D13).
        /// </summary>
        /// <param name="type">The column type.</param>
        /// <param name="name">The name, unique in the query.</param>
        /// <returns>The variable.</returns>
        internal Term Var(ColumnType type, string name)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(type);
            NameRules.ValidateName(name, nameof(name));
            var variable = new UntypedVar(this, type, name);
            draft.Variables.Add((name, type, variable));
            return variable;
        }

        /// <summary>
        /// Gets what the builder collected.
        /// </summary>
        internal RuleDraft Draft => draft;

        /// <summary>
        /// Ends the definition: later calls throw.
        /// </summary>
        internal void Close() => closed = true;

        /// <summary>
        /// Throws once the definition has ended.
        /// </summary>
        void ThrowIfClosed()
        {
            if (closed)
                throw new InvalidOperationException($"The builder of query '{draft.Name}' is used outside its definition.");
        }

    }

}
