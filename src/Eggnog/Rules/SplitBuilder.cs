using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Collects one split rule: a body of <see cref="When"/> facts and <c>Where</c> filters, the element set to split
    /// over (<see cref="Cases{T}"/>), the key (<see cref="Key"/>; by default every declared variable except the set),
    /// and the assumptions written into each case (<see cref="Assume"/>). Usable only inside the definition's callback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coverage is the caller's obligation: whenever the body matches in a scope C, C must entail "for the elements of the
    /// set at this match, one of the cases' assumptions holds", and that must stay true as C grows, as it does for a
    /// domain narrowed by a lattice meet. The body is also the split's demand: a key it does not match is never split.
    /// </para>
    /// <para>
    /// Assumptions are ground once a match and an element are bound: host-write facts over constants, the declared
    /// variables, the case variable, constructor applications and computations of those; a scoped handle, a
    /// <see cref="Fact.In"/>, or a function read in a term is a definition error.
    /// </para>
    /// </remarks>
    public sealed class SplitBuilder
    {

        readonly EGraph graph;
        readonly RuleDraft draft;
        readonly List<Fact> assumptions = new();
        List<Term>? key;
        Term? set;
        Term? element;
        Type? elementType;
        Func<ColumnType, uint, int>? countOf;
        Func<ColumnType, uint, int, object>? elementAt;
        int casesCalls;
        bool closed;

        /// <summary>
        /// Initializes a builder for one split rule.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="name">The split rule's name.</param>
        internal SplitBuilder(EGraph graph, string name)
        {
            this.graph = graph;
            draft = new RuleDraft(name, this);
        }

        /// <summary>
        /// Declares a body variable.
        /// </summary>
        /// <typeparam name="T">The CLR type of its values; a registered value type or a defined sort.</typeparam>
        /// <param name="name">The name, unique in the split rule.</param>
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
        /// Declares two body variables.
        /// </summary>
        /// <typeparam name="T1">The first variable's type.</typeparam>
        /// <typeparam name="T2">The second variable's type.</typeparam>
        /// <param name="name1">The first name.</param>
        /// <param name="name2">The second name.</param>
        /// <returns>The variables.</returns>
        public (Var<T1>, Var<T2>) Vars<T1, T2>(string name1, string name2) => (Var<T1>(name1), Var<T2>(name2));

        /// <summary>
        /// Declares three body variables.
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
        /// filters, as in a rule. Scoped reads (<c>In(tag)</c>, <see cref="Fact.In"/>) read the tagged ancestor's view
        /// through its mirror.
        /// </summary>
        /// <param name="facts">The facts.</param>
        /// <exception cref="ArgumentException">The tag of a <see cref="Fact.In"/> belongs to another graph.</exception>
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
        /// Adds a filter on a boolean term, e.g. <c>Primitives.Greater.Of(Primitives.Count&lt;string&gt;().Of(d), 1L)</c>.
        /// </summary>
        /// <param name="condition">The condition.</param>
        public void Where(Term<bool> condition)
        {
            ThrowIfClosed();
            draft.Body.Add(Fact.True(condition));
        }

        /// <summary>
        /// Splits over the elements of <paramref name="set"/>: one case per element of the set a match binds, in element
        /// order. The returned variable is bound to that case's element in <see cref="Assume"/>; it may not appear in the
        /// body. Called once per split rule.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="name">The case variable's name, unique in the split rule.</param>
        /// <param name="set">A declared body variable holding the set, a set of values.</param>
        /// <returns>The case variable.</returns>
        public Var<T> Cases<T>(string name, Var<Set<T>> set) where T : notnull
        {
            ThrowIfClosed();
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(set);
            casesCalls++;
            var variable = new Var<T>(this, name);
            this.set = set;
            element = variable;
            elementType = typeof(T);

            // A split's set holds values (RuleCompiler refuses classes), which decode outside any scope and read the
            // same through every container view.
            countOf = static (type, cell) => ((SetColumnType<T>)type).CountOfCell(cell, default);
            elementAt = static (type, cell, index) => ((ColumnType<Set<T>>)type).Decode(cell, null)[index];

            return variable;
        }

        /// <summary>
        /// Sets the split's key: what identifies it, so that a split step skips a key a live split of the same rule covers
        /// in the scope or an ancestor. By default, every declared body variable except the set, in declaration order.
        /// Calls add to the key.
        /// </summary>
        /// <param name="variables">Declared body variables of this split rule.</param>
        public void Key(params Term[] variables)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(variables);
            key ??= new List<Term>();
            foreach (var variable in variables)
                key.Add(variable ?? throw new ArgumentNullException(nameof(variables), "A key variable is null."));
        }

        /// <summary>
        /// Adds the assumptions written into each case, as one host batch: relation rows, function values
        /// (<c>F.Of(k).Is(v)</c>, merged by F's lattice), constructor outputs and unions, over the declared variables and the
        /// case variable.
        /// </summary>
        /// <param name="facts">The facts.</param>
        /// <exception cref="ArgumentException">The tag of a <see cref="Fact.In"/> belongs to another graph.</exception>
        public void Assume(params Fact[] facts)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(facts);
            foreach (var fact in facts)
            {
                graph.CheckFactTags(fact ?? throw new ArgumentNullException(nameof(facts), "A fact is null."), nameof(facts));
                assumptions.Add(fact);
            }
        }

        /// <summary>
        /// Gets what the builder collected of the body.
        /// </summary>
        internal RuleDraft Draft => draft;

        /// <summary>
        /// Gets the set variable given to <see cref="Cases{T}"/>; <c>null</c> before it is called.
        /// </summary>
        internal Term? Set => set;

        /// <summary>
        /// Gets the case variable <see cref="Cases{T}"/> returned; <c>null</c> before it is called.
        /// </summary>
        internal Term? Element => element;

        /// <summary>
        /// Gets the CLR type of the case variable.
        /// </summary>
        internal Type? ElementType => elementType;

        /// <summary>
        /// Gets the number of elements of a set cell, given the set's column type.
        /// </summary>
        internal Func<ColumnType, uint, int>? CountOf => countOf;

        /// <summary>
        /// Gets an element of a set cell by its position in set order, given the set's column type.
        /// </summary>
        internal Func<ColumnType, uint, int, object>? ElementAt => elementAt;

        /// <summary>
        /// Gets the number of <see cref="Cases{T}"/> calls: exactly one is valid.
        /// </summary>
        internal int CasesCalls => casesCalls;

        /// <summary>
        /// Gets the explicit key; <c>null</c> for the default.
        /// </summary>
        internal IReadOnlyList<Term>? KeyTerms => key;

        /// <summary>
        /// Gets the assumptions, in call order.
        /// </summary>
        internal IReadOnlyList<Fact> Assumptions => assumptions;

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
                throw new InvalidOperationException($"The builder of split rule '{draft.Name}' is used outside its definition.");
        }

    }

}
