using System;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// Collects one rule: <see cref="When"/> and <c>Where</c> make the body; <see cref="Then"/>, <see cref="Make{TSort}"/>
    /// and <see cref="Rewrite{TSort}"/> make the head. Body calls are unordered; head items are lowered in the order the
    /// builder calls were made (§6.3). Usable only inside the definition's callback.
    /// </summary>
    public sealed class RuleBuilder
    {

        readonly EGraph graph;
        readonly RuleDraft draft;
        int rewrites;
        bool closed;

        /// <summary>
        /// Initializes a builder for one rule.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="name">The rule's name.</param>
        internal RuleBuilder(EGraph graph, string name)
        {
            this.graph = graph;
            draft = new RuleDraft(name, this);
        }

        /// <summary>
        /// Gets the rule's name.
        /// </summary>
        public string Name => draft.Name;

        /// <summary>
        /// Declares a variable.
        /// </summary>
        /// <typeparam name="T">The CLR type of its values; a registered value type or a defined sort.</typeparam>
        /// <param name="name">The name, unique in the rule.</param>
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
        /// Declares four variables.
        /// </summary>
        /// <typeparam name="T1">The first variable's type.</typeparam>
        /// <typeparam name="T2">The second variable's type.</typeparam>
        /// <typeparam name="T3">The third variable's type.</typeparam>
        /// <typeparam name="T4">The fourth variable's type.</typeparam>
        /// <param name="name1">The first name.</param>
        /// <param name="name2">The second name.</param>
        /// <param name="name3">The third name.</param>
        /// <param name="name4">The fourth name.</param>
        /// <returns>The variables.</returns>
        public (Var<T1>, Var<T2>, Var<T3>, Var<T4>) Vars<T1, T2, T3, T4>(string name1, string name2, string name3, string name4) =>
            (Var<T1>(name1), Var<T2>(name2), Var<T3>(name3), Var<T4>(name4));

        /// <summary>
        /// Adds body facts: relation rows and applications to join, equalities to unify or check, disequalities and
        /// filters.
        /// </summary>
        /// <param name="facts">The facts.</param>
        public void When(params Fact[] facts)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(facts);
            foreach (var fact in facts)
                draft.Body.Add(fact ?? throw new ArgumentNullException(nameof(facts), "A fact is null."));
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
        /// Adds a filter on three terms with a lambda, which renders as <c>&lt;lambda&gt;</c>.
        /// </summary>
        /// <typeparam name="T1">The first term's type.</typeparam>
        /// <typeparam name="T2">The second term's type.</typeparam>
        /// <typeparam name="T3">The third term's type.</typeparam>
        /// <param name="a">The first term.</param>
        /// <param name="b">The second term.</param>
        /// <param name="c">The third term.</param>
        /// <param name="predicate">The filter: pure, deterministic and thread-safe; it must not call into any graph.</param>
        public void Where<T1, T2, T3>(Term<T1> a, Term<T2> b, Term<T3> c, Func<T1, T2, T3, bool> predicate) =>
            Where(Compute.Of(a, b, c, predicate));

        /// <summary>
        /// Adds a filter on four terms with a lambda, which renders as <c>&lt;lambda&gt;</c>.
        /// </summary>
        /// <typeparam name="T1">The first term's type.</typeparam>
        /// <typeparam name="T2">The second term's type.</typeparam>
        /// <typeparam name="T3">The third term's type.</typeparam>
        /// <typeparam name="T4">The fourth term's type.</typeparam>
        /// <param name="a">The first term.</param>
        /// <param name="b">The second term.</param>
        /// <param name="c">The third term.</param>
        /// <param name="d">The fourth term.</param>
        /// <param name="predicate">The filter: pure, deterministic and thread-safe; it must not call into any graph.</param>
        public void Where<T1, T2, T3, T4>(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d, Func<T1, T2, T3, T4, bool> predicate) =>
            Where(Compute.Of(a, b, c, d, predicate));

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
        /// Adds head facts, which are writes: relation rows, function values (<c>dom.Of(p).Is(d)</c>), constructor outputs
        /// and unions of classes.
        /// </summary>
        /// <param name="facts">The facts.</param>
        public void Then(params Fact[] facts)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(facts);
            foreach (var fact in facts)
                draft.Head.Add(new HeadItem(HeadItemKind.Then, fact ?? throw new ArgumentNullException(nameof(facts), "A fact is null."), null, null, null));
        }

        /// <summary>
        /// A head variable bound to the class of <paramref name="term"/>, made by get-or-add; it may not appear in the body.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="name">The variable's name, unique in the rule.</param>
        /// <param name="term">The term.</param>
        /// <returns>The variable, usable in later head items.</returns>
        public Var<TSort> Make<TSort>(string name, ConstructorTerm<TSort> term) where TSort : struct, ISort<TSort>
        {
            ThrowIfClosed();
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(term);
            var variable = new Var<TSort>(this, name);
            draft.Head.Add(new HeadItem(HeadItemKind.Make, null, variable, term, null));
            return variable;
        }

        /// <summary>
        /// A rewrite: in the body, a fresh class variable <c>t</c> with <c>t = left</c>; in the head, <c>t = right</c> (a
        /// constructor write when <paramref name="right"/> is a constructor application, a union otherwise).
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="left">The pattern.</param>
        /// <param name="right">The replacement.</param>
        public void Rewrite<TSort>(Term<TSort> left, Term<TSort> right) where TSort : struct, ISort<TSort>
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            var variable = Var<TSort>("$rw" + (rewrites++).ToString(CultureInfo.InvariantCulture));
            draft.Body.Add(variable.Is(left));
            draft.Head.Add(new HeadItem(HeadItemKind.Rewrite, null, variable, left, right));
        }

        /// <summary>
        /// Makes every run match in full (a naive engine rule, which never inherits watermarks).
        /// </summary>
        public void Naive()
        {
            ThrowIfClosed();
            draft.Naive = true;
        }

        /// <summary>
        /// Declares an untyped variable of a column type (D13).
        /// </summary>
        /// <param name="type">The column type.</param>
        /// <param name="name">The name, unique in the rule.</param>
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
                throw new InvalidOperationException($"The builder of rule '{draft.Name}' is used outside its definition.");
        }

    }

}
