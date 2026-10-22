using System;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// Collects one rule: <see cref="When"/> and <c>Where</c> make the body; <see cref="Then"/>, <see
    /// cref="Make{TSort}"/>, <c>Rewrite</c> and <c>Subsume</c> make the head. Body calls are unordered for matching,
    /// and their order is the order in which the rule interns its constants, as egglog's order of a rule's query does;
    /// head items are lowered in the order the builder calls were made. Usable only inside the definition's
    /// callback.
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
        /// <exception cref="ArgumentException">The tag of a <see cref="Fact.In"/> belongs to another graph.</exception>
        public void Then(params Fact[] facts)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(facts);
            foreach (var fact in facts)
            {
                graph.CheckFactTags(fact ?? throw new ArgumentNullException(nameof(facts), "A fact is null."), nameof(facts));
                draft.Head.Add(new HeadItem(HeadItemKind.Then, fact, null, null, null));
            }
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
        public void Rewrite<TSort>(Term<TSort> left, Term<TSort> right) where TSort : struct, ISort<TSort> => Rewrite(left, right, false);

        /// <summary>
        /// A rewrite (<see cref="Rewrite{TSort}(Term{TSort}, Term{TSort})"/>) that, with <paramref name="subsume"/>,
        /// also subsumes the matched left side: egglog's <c>rewrite ... :subsume</c>, whose head is <c>t = right</c>
        /// then <c>(subsume left)</c>. Later matches skip the left side's row, so no other rule rewrites it again, and
        /// extraction skips it. The row subsumed is the one the body matched: the left side's arguments are the values
        /// the match bound, so a left side may read a function, as in a plain rewrite. The left side must then be an
        /// application of a constructor defined <see cref="TableOptions.Subsumable"/> (a <see
        /// cref="RuleDefinitionException"/> otherwise).
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="left">The pattern.</param>
        /// <param name="right">The replacement.</param>
        /// <param name="subsume">Whether to subsume the matched left side.</param>
        public void Rewrite<TSort>(Term<TSort> left, Term<TSort> right, bool subsume) where TSort : struct, ISort<TSort> =>
            Rewrite(left, right, subsume, false);

        /// <summary>
        /// A rewrite whose left side is the body's first fact, before the conditions already added: egglog's
        /// <c>rewrite</c> as its desugaring builds it (src/ast/desugar.rs, <c>desugar_rewrite</c>), the left side then the
        /// <c>:when</c> conditions, for a <c>birewrite</c>.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="left">The pattern.</param>
        /// <param name="right">The replacement.</param>
        internal void RewriteFirst<TSort>(Term<TSort> left, Term<TSort> right) where TSort : struct, ISort<TSort> =>
            Rewrite(left, right, false, true);

        /// <summary>
        /// Adds a rewrite, its left side last in the body, or first.
        /// </summary>
        void Rewrite<TSort>(Term<TSort> left, Term<TSort> right, bool subsume, bool first) where TSort : struct, ISort<TSort>
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            var variable = Var<TSort>("$rw" + (rewrites++).ToString(CultureInfo.InvariantCulture));
            draft.Body.Insert(first ? 0 : draft.Body.Count, variable.Is(left));
            draft.Head.Add(new HeadItem(HeadItemKind.Rewrite, null, variable, left, right));
            if (subsume)
                draft.Head.Add(new HeadItem(HeadItemKind.Subsume, null, variable, left, null));
        }

        /// <summary>
        /// Subsumes a constructor term: egglog's <c>(subsume (f args...))</c>. The term's row stays, and lookups,
        /// checks and host reads still find it, but from the next iteration rule bodies, queries and split rules no
        /// longer match it, and extraction skips it; its class keeps every other member. A term that does not exist yet
        /// is made, subsumed, and the rule's later head items still run for the match. Subsumption is sticky. The
        /// constructor must be defined <see cref="TableOptions.Subsumable"/> (a <see cref="RuleDefinitionException"/>
        /// otherwise). The term's arguments are evaluated as head terms, made if absent.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="term">The constructor term.</param>
        public void Subsume<TSort>(ConstructorTerm<TSort> term) where TSort : struct, ISort<TSort>
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(term);
            draft.Head.Add(new HeadItem(HeadItemKind.Subsume, null, null, term, null));
        }

        /// <summary>
        /// Subsumes relation rows (<c>r.Of(a, b)</c>): egglog's <c>(subsume (r a b))</c>. Each row stays, and lookups,
        /// checks and host reads still find it, but from the next iteration rule bodies, queries and split rules no
        /// longer match it. A row that does not exist yet is inserted, subsumed, and the rule's later head items still
        /// run for the match. Subsumption is sticky. Each relation must be defined <see
        /// cref="TableOptions.Subsumable"/>, and each fact must be a relation row of the running scope, not a scoped
        /// view (a <see cref="RuleDefinitionException"/> otherwise).
        /// </summary>
        /// <param name="rows">The relation rows.</param>
        /// <exception cref="ArgumentException">The tag of a <see cref="Fact.In"/> belongs to another graph.</exception>
        public void Subsume(params Fact[] rows)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(rows);
            foreach (var row in rows)
            {
                graph.CheckFactTags(row ?? throw new ArgumentNullException(nameof(rows), "A fact is null."), nameof(rows));
                draft.Head.Add(new HeadItem(HeadItemKind.Subsume, row, null, null, null));
            }
        }

        /// <summary>
        /// Deletes the row of a function or constructor application: egglog's <c>(delete (f args...))</c>. The row of the
        /// application's keys goes, if there is one, and nothing else: a deleted constructor term's class stays, with its
        /// other members. The keys are evaluated as head terms, made if absent. Every deletion of an iteration lands
        /// before its writes, so a row the same iteration also writes stays, as in egglog, and a deletion is no change
        /// for saturation. A rule that deletes runs only where the scope may delete: not in a scope with live
        /// descendants, a child of a writable parent, or a run that splits (<see cref="Scope.Run"/> refuses it before any
        /// work). The application must be of the running scope, not a scoped view (a
        /// <see cref="RuleDefinitionException"/> otherwise).
        /// </summary>
        /// <typeparam name="T">The application's type.</typeparam>
        /// <param name="term">The application: <c>f.Of(k)</c> of a function or a constructor.</param>
        public void Delete<T>(Term<T> term)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(term);
            draft.Head.Add(new HeadItem(HeadItemKind.Delete, null, null, term, null));
        }

        /// <summary>
        /// Deletes relation rows (<c>r.Of(a, b)</c>): egglog's <c>(delete (r a b))</c>. A row that does not exist deletes
        /// nothing. Otherwise as <see cref="Delete{T}(Term{T})"/>: each fact must be a relation row of the running scope,
        /// not a scoped view (a <see cref="RuleDefinitionException"/> otherwise).
        /// </summary>
        /// <param name="rows">The relation rows.</param>
        /// <exception cref="ArgumentException">The tag of a <see cref="Fact.In"/> belongs to another graph.</exception>
        public void Delete(params Fact[] rows)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(rows);
            foreach (var row in rows)
            {
                graph.CheckFactTags(row ?? throw new ArgumentNullException(nameof(rows), "A fact is null."), nameof(rows));
                draft.Head.Add(new HeadItem(HeadItemKind.Delete, row, null, null, null));
            }
        }

        /// <summary>
        /// Adds a callback to the head: egglog's <c>rust_rule</c>, whose right-hand side is host code. For each match it
        /// gets the values of the rule's variables (<see cref="RuleCallbackContext.Get"/>) and writes as a head does:
        /// rows, function values, terms, unions, deletions and subsumptions. It reads no table, so the rule stays
        /// semi-naive; <see cref="ThenCallFull"/> reads. It runs in order with the other head items, sees the variables
        /// the body binds and those made before it, and runs on the engine's threads, so it must be thread-safe. A write
        /// is checked when the callback makes it (<see cref="RuleCallbackContext"/>); one refused, or an exception it
        /// throws, fails the rule.
        /// </summary>
        /// <param name="callback">The callback.</param>
        public void ThenCall(Action<RuleCallbackContext> callback)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(callback);
            draft.Head.Add(new HeadItem(HeadItemKind.Callback, null, null, null, null, new RuleCallbackDefinition(callback)));
        }

        /// <summary>
        /// Adds a callback that reads tables too (<see cref="FullRuleCallbackContext"/>): egglog's <c>rust_rule_full</c>.
        /// As egglog marks such a rule, the rule becomes naive (<see cref="Naive"/>): matched in full every run, so that a
        /// match is found again when a row the callback read changes; under <see cref="Schedule.RunWith"/> its query is
        /// semi-naive, as egglog builds every scheduler's. A rule under a licence reads outside it here, so it must call
        /// <see cref="ReadsOwnFacts"/>. Otherwise as <see cref="ThenCall"/>.
        /// </summary>
        /// <param name="callback">The callback.</param>
        public void ThenCallFull(Action<FullRuleCallbackContext> callback)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(callback);
            draft.Head.Add(new HeadItem(HeadItemKind.Callback, null, null, null, null, new RuleCallbackDefinition(callback)));
            draft.Naive = true;
        }

        /// <summary>
        /// Makes every run match in full: against every row, not only the rows new since the rule last ran here or in
        /// an ancestor.
        /// </summary>
        public void Naive()
        {
            ThrowIfClosed();
            draft.Naive = true;
        }

        /// <summary>
        /// Makes this rule erasing under <paramref name="licence"/>, whatever its ruleset says: every atom over a fact
        /// table must read the licence scope (<c>In(licence)</c>), unless the rule calls <see cref="ReadsOwnFacts"/>.
        /// </summary>
        /// <param name="licence">The licence tag.</param>
        /// <exception cref="ArgumentNullException"><paramref name="licence"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The tag belongs to another graph.</exception>
        public void Erasing(ScopeTag licence)
        {
            ThrowIfClosed();
            graph.CheckTag(licence, nameof(licence));
            draft.Erasing = licence;
        }

        /// <summary>
        /// Exempts this rule from its licence: it may read fact tables in the running scope. <paramref name="reason"/>
        /// says why that is sound, and <see cref="Rule.ToString"/> renders it. The rule must have a licence, its
        /// ruleset's or its own (a <see cref="RuleDefinitionException"/> otherwise), and the reason must not be empty.
        /// </summary>
        /// <param name="reason">Why reading the running scope's facts is sound for this rule.</param>
        public void ReadsOwnFacts(string reason)
        {
            ThrowIfClosed();
            draft.OwnFactsReason = reason ?? string.Empty;
        }

        /// <summary>
        /// Declares an untyped variable of a column type, for the internal untyped layer.
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
