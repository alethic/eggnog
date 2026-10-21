using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    public sealed partial class Scope
    {

        /// <summary>
        /// Gets whether <see cref="EGraph.Contradiction"/> holds here. Always answers, on a stopped case and on a scope
        /// under one too: <c>true</c> there.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted; or it is writable and in use on another
        /// thread, or has a <c>Write</c> batch open; or the call comes from a primitive, lattice, filter or cost
        /// function.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>) and the scope,
        /// which is not frozen, has catch-up work, which takes one: an ancestor that changed since it last caught up, or
        /// a rebuild of its own that ran out before it finished. The scope is not faulted. A frozen scope, and one with
        /// nothing to catch up, reads without an epoch.</exception>
        public bool IsContradictory
        {
            get
            {
                var held = EnterRead();
                try
                {
                    // A stopped case, and a scope under one, answer this one read: the stop flag says it is
                    // contradictory.
                    return IsStopped || Engine.IsContradictory;
                }
                finally
                {
                    Exit(held);
                }
            }
        }

        /// <summary>
        /// Gets whether every ground fact holds. Never creates terms and never interns, and holds where egglog's
        /// <c>check</c>, which interns, holds: a value no write interned, a constant or a computation's result,
        /// equals only itself, and as a key is in no row. A scoped view (<c>T.In(tag)</c>) or a <see cref="Fact.In"/> reads the nearest
        /// ancestor-or-self tagged <c>tag</c> directly (host reads never use mirrors). A subsumed row holds, as in
        /// egglog's <c>check</c> (<see cref="TableOptions.Subsumable"/>).
        /// </summary>
        /// <param name="facts">The facts: relation rows, equalities of classes or values, existences.</param>
        /// <returns>Whether every fact holds.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">A fact is a filter, a term in it is not ground, a <see cref="Fact.In"/> is
        /// nested, or a handle scoped to one tag is inside a <see cref="Fact.In"/> of another.</exception>
        /// <exception cref="InvalidOperationException">A tag read does not resolve from this scope: no ancestor-or-self
        /// carries it; or the scope is faulted; or it is writable and in use on another thread, or has a <c>Write</c>
        /// batch open; or the call comes from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>) and the scope,
        /// which is not frozen, has catch-up work, which takes one: an ancestor that changed since it last caught up, or
        /// a rebuild of its own that ran out before it finished. The scope is not faulted. A frozen scope, and one with
        /// nothing to catch up, reads without an epoch.</exception>
        public bool Check(params Fact[] facts)
        {
            ArgumentNullException.ThrowIfNull(facts);
            var held = EnterRead();
            try
            {
                ThrowIfStopped();
                foreach (var fact in facts)
                    if (TermReader.Check(this, fact ?? throw new ArgumentNullException(nameof(facts), "A fact is null.")) == false)
                        return false;

                return true;
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Gets the value of a ground term: a function's value, the class of a constructor term, or the value of a
        /// constant or a computation, which no write need have interned, as egglog evaluates an expression.
        /// </summary>
        /// <typeparam name="T">The term's type.</typeparam>
        /// <param name="term">The term.</param>
        /// <returns>The value; a class is canonical here and usable in this scope.</returns>
        /// <remarks>A scoped view in the term reads the nearest ancestor-or-self carrying its tag directly.</remarks>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The term has no value here, or a tag it reads does not resolve
        /// from this scope; or the scope is faulted; or it is writable and in use on another thread, or has a
        /// <c>Write</c> batch open; or the call comes from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="ArgumentException">The term is not ground, or holds a class this scope cannot
        /// use.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>) and the scope,
        /// which is not frozen, has catch-up work, which takes one: an ancestor that changed since it last caught up, or
        /// a rebuild of its own that ran out before it finished. The scope is not faulted. A frozen scope, and one with
        /// nothing to catch up, reads without an epoch.</exception>
        public T Get<T>(Term<T> term)
        {
            if (TryGet(term, out var value))
                return value;

            throw new InvalidOperationException($"{term} has no value in scope '{Path}'.");
        }

        /// <summary>
        /// Gets the value of a ground term, if it has one.
        /// </summary>
        /// <typeparam name="T">The term's type.</typeparam>
        /// <param name="term">The term.</param>
        /// <param name="value">The value, when the result is <c>true</c>.</param>
        /// <returns>Whether the term has a value here.</returns>
        /// <remarks>A scoped view in the term reads the nearest ancestor-or-self carrying its tag directly.</remarks>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">A tag the term reads does not resolve from this scope; or the
        /// scope is faulted; or it is writable and in use on another thread, or has a <c>Write</c> batch open; or the
        /// call comes from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="ArgumentException">The term is not ground, or holds a class this scope cannot
        /// use.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>) and the scope,
        /// which is not frozen, has catch-up work, which takes one: an ancestor that changed since it last caught up, or
        /// a rebuild of its own that ran out before it finished. The scope is not faulted. A frozen scope, and one with
        /// nothing to catch up, reads without an epoch.</exception>
        public bool TryGet<T>(Term<T> term, [MaybeNullWhen(false)] out T value)
        {
            ArgumentNullException.ThrowIfNull(term);
            var held = EnterRead();
            try
            {
                ThrowIfStopped();
                if (TermReader.TryReadValue(this, term, out var cell, out var unencoded))
                {
                    value = unencoded is null ? Graph.Values.TypeOf<T>().Decode(cell, this) : (T)unencoded;
                    return true;
                }

                value = default;
                return false;
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Gets the canonical member of a class here.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="value">The class.</param>
        /// <returns>The canonical member, usable in this scope.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted; or it is writable and in use on another
        /// thread, or has a <c>Write</c> batch open; or the call comes from a primitive, lattice, filter or cost
        /// function.</exception>
        /// <exception cref="ArgumentException">The class value is not usable in this scope.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>) and the scope,
        /// which is not frozen, has catch-up work, which takes one: an ancestor that changed since it last caught up, or
        /// a rebuild of its own that ran out before it finished. The scope is not faulted. A frozen scope, and one with
        /// nothing to catch up, reads without an epoch.</exception>
        public TSort Find<TSort>(TSort value) where TSort : struct, ISort<TSort>
        {
            var held = EnterRead();
            try
            {
                ThrowIfStopped();
                var cell = ClassChecks.Require(this, value.Id, nameof(value));
                return TSort.FromId(new ClassId(Engine.Find(cell), Origin));
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Gets whether two classes are equal here.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="a">One class.</param>
        /// <param name="b">The other class.</param>
        /// <returns>Whether they are one class.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted; or it is writable and in use on another
        /// thread, or has a <c>Write</c> batch open; or the call comes from a primitive, lattice, filter or cost
        /// function.</exception>
        /// <exception cref="ArgumentException">A class value is not usable in this scope.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>) and the scope,
        /// which is not frozen, has catch-up work, which takes one: an ancestor that changed since it last caught up, or
        /// a rebuild of its own that ran out before it finished. The scope is not faulted. A frozen scope, and one with
        /// nothing to catch up, reads without an epoch.</exception>
        public bool AreEqual<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>
        {
            var held = EnterRead();
            try
            {
                ThrowIfStopped();
                var x = ClassChecks.Require(this, a.Id, nameof(a));
                var y = ClassChecks.Require(this, b.Id, nameof(b));
                return Engine.Find(x) == Engine.Find(y);
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Gets whether a class equals the class of a ground term here; <c>false</c> if the term does not exist here.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="a">The class.</param>
        /// <param name="b">The term.</param>
        /// <returns>Whether the term exists and is in the class.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted; or it is writable and in use on another
        /// thread, or has a <c>Write</c> batch open; or the call comes from a primitive, lattice, filter or cost
        /// function.</exception>
        /// <exception cref="ArgumentException">A class value is not usable in this scope.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>) and the scope,
        /// which is not frozen, has catch-up work, which takes one: an ancestor that changed since it last caught up, or
        /// a rebuild of its own that ran out before it finished. The scope is not faulted. A frozen scope, and one with
        /// nothing to catch up, reads without an epoch.</exception>
        public bool AreEqual<TSort>(TSort a, Term<TSort> b) where TSort : struct, ISort<TSort>
        {
            ArgumentNullException.ThrowIfNull(b);
            var held = EnterRead();
            try
            {
                ThrowIfStopped();
                var x = ClassChecks.Require(this, a.Id, nameof(a));
                return TermReader.TryRead(this, b, out var y) && Engine.Find(x) == y;
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Gets the number of rows of a table in this scope's view, subsumed rows included: egglog's <c>print-size</c>.
        /// A scoped view (<c>T.In(tag)</c>) counts the rows of the nearest ancestor-or-self tagged <c>tag</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The row count.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">The table belongs to another graph.</exception>
        /// <exception cref="InvalidOperationException">The view's tag does not resolve from this scope; or the scope is
        /// faulted; or it is writable and in use on another thread, or has a <c>Write</c> batch open; or the call comes
        /// from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>) and the scope,
        /// which is not frozen, has catch-up work, which takes one: an ancestor that changed since it last caught up, or
        /// a rebuild of its own that ran out before it finished. The scope is not faulted. A frozen scope, and one with
        /// nothing to catch up, reads without an epoch.</exception>
        public long CountRows(TableHandle table)
        {
            ArgumentNullException.ThrowIfNull(table);
            var held = EnterRead();
            try
            {
                ThrowIfStopped();
                return RowReader.Count(this, table);
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Gets the rows of a table in this scope's view, decoded and in content order: for debugging and tests. A
        /// sort cell is a <see cref="ClassId"/>, canonical here and usable in this scope. A scoped view (<c>T.In(tag)</c>)
        /// lists the rows of the nearest ancestor-or-self tagged <c>tag</c>. Subsumed rows are listed, with
        /// <see cref="TableRow.Subsumed"/> set.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The rows.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">The table belongs to another graph.</exception>
        /// <exception cref="InvalidOperationException">The view's tag does not resolve from this scope; or the scope is
        /// faulted; or it is writable and in use on another thread, or has a <c>Write</c> batch open; or the call comes
        /// from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>) and the scope,
        /// which is not frozen, has catch-up work, which takes one: an ancestor that changed since it last caught up, or
        /// a rebuild of its own that ran out before it finished. The scope is not faulted. A frozen scope, and one with
        /// nothing to catch up, reads without an epoch.</exception>
        public IReadOnlyList<TableRow> Rows(TableHandle table)
        {
            ArgumentNullException.ThrowIfNull(table);
            var held = EnterRead();
            try
            {
                ThrowIfStopped();
                return RowReader.Read(this, table);
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Serializes this scope's e-graph as egglog's <c>EGraph::serialize</c> does, into the graph of the
        /// egraph-serialize crate that egglog's visualizer reads (<see cref="SerializedGraph.ToJson"/>): for debugging.
        /// It is the scope's view, as <see cref="Rows"/> lists it, classes canonical here: every table but the hidden
        /// mirrors, in definition order, its rows in the table's storage order, as egglog numbers them, subsumed ones marked. Reads only: it never interns.
        /// </summary>
        /// <param name="options">What to include; <c>null</c> for everything.</param>
        /// <returns>The serialized graph.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A limit is negative.</exception>
        /// <exception cref="ArgumentNullException">The options' <see cref="SerializeOptions.Roots"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">A root is a class this scope cannot use, or no row holds it.</exception>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted; or it is writable and in use on another
        /// thread, or has a <c>Write</c> batch open; or the call comes from a primitive, lattice, filter or cost
        /// function.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>) and the scope,
        /// which is not frozen, has catch-up work, which takes one. The scope is not faulted.</exception>
        public SerializedGraph Serialize(SerializeOptions? options = null)
        {
            options ??= new SerializeOptions();
            if (options.MaxTables is < 0)
                throw new ArgumentOutOfRangeException(nameof(options), options.MaxTables, "MaxTables must not be negative.");

            if (options.MaxRowsPerTable is < 0)
                throw new ArgumentOutOfRangeException(nameof(options), options.MaxRowsPerTable, "MaxRowsPerTable must not be negative.");

            if (options.Roots is null)
                throw new ArgumentNullException(nameof(options), "Roots is null.");

            var held = EnterRead();
            try
            {
                ThrowIfStopped();
                return GraphSerializer.Serialize(this, options);
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Gets whether a query has a match here. The engine stops at the first match. A query writes nothing: it
        /// moves neither <see cref="Version"/> nor any extractor; the mirrors of the tags it reads are brought
        /// current first, which neither does either. Like a rule body, and unlike <see cref="Check"/>, a query does not
        /// match subsumed rows (<see cref="TableOptions.Subsumable"/>).
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>Whether it has a match.</returns>
        /// <exception cref="ArgumentException">The query belongs to another graph, or holds a class this scope cannot
        /// use.</exception>
        /// <exception cref="InvalidOperationException">A tag the query reads does not resolve from this scope (refused
        /// before any work); the scope is faulted or in use on another thread.</exception>
        /// <exception cref="ObjectDisposedException">The query is disposed.</exception>
        /// <exception cref="RuleEvaluationException">A primitive or filter of the query failed; the scope stays usable.
        /// Or a lattice merge threw in the catch-up the call makes first, here or in a writable ancestor: that is a
        /// merge into the scope, so the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>): a query runs as
        /// a rule, here or in a transient child of a frozen scope, and takes an epoch. Nothing is read and the scope is
        /// not faulted, but every later query throws the same way.</exception>
        public bool Any(Query query)
        {
            using var matches = RunQuery(query, true);
            return matches.Count > 0;
        }

        /// <summary>
        /// Gets the number of a query's matches here: its distinct full bindings. A query writes nothing, and does not
        /// match subsumed rows (<see cref="Any"/>).
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>The number of matches.</returns>
        /// <exception cref="ArgumentException">The query belongs to another graph, or holds a class this scope cannot
        /// use.</exception>
        /// <exception cref="InvalidOperationException">A tag the query reads does not resolve from this scope (refused
        /// before any work); the scope is faulted or in use on another thread.</exception>
        /// <exception cref="ObjectDisposedException">The query is disposed.</exception>
        /// <exception cref="RuleEvaluationException">A primitive or filter of the query failed; the scope stays usable.
        /// Or a lattice merge threw in the catch-up the call makes first, here or in a writable ancestor: that is a
        /// merge into the scope, so the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>): a query runs as
        /// a rule, here or in a transient child of a frozen scope, and takes an epoch. Nothing is read and the scope is
        /// not faulted, but every later query throws the same way.</exception>
        public long Count(Query query)
        {
            using var matches = RunQuery(query, false);
            return matches.Count;
        }

        /// <summary>
        /// Delivers a query's matches here, its distinct full bindings, in content order, until the callback returns
        /// <c>false</c>. The matches are collected, and ordered, before the first callback, so the callback may call
        /// the scope, writes and runs included: a class a match returns is canonical when it is read (<see
        /// cref="QueryMatch.Get{T}(Var{T})"/>), so after a union made by an earlier callback two matches may name one
        /// class. A query writes nothing, and does not match subsumed rows (<see cref="Any"/>); the query counts as in
        /// use until this returns.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <param name="callback">Receives each match; returns <c>false</c> to stop.</param>
        /// <exception cref="ArgumentException">The query belongs to another graph, or holds a class this scope cannot
        /// use.</exception>
        /// <exception cref="InvalidOperationException">A tag the query reads does not resolve from this scope (refused
        /// before any work); the scope is faulted or in use on another thread.</exception>
        /// <exception cref="ObjectDisposedException">The query is disposed.</exception>
        /// <exception cref="RuleEvaluationException">A primitive or filter of the query failed; the scope stays usable.
        /// Or a lattice merge threw in the catch-up the call makes first, here or in a writable ancestor: that is a
        /// merge into the scope, so the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of epochs (<see cref="EGraph.Usage"/>): a query runs as
        /// a rule, here or in a transient child of a frozen scope, and takes an epoch. Nothing is read and the scope is
        /// not faulted, but every later query throws the same way.</exception>
        public void ForEachMatch(Query query, QueryCallback callback)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(callback);
            CheckQuery(query);
            var held = EnterRead();
            try
            {
                ThrowIfStopped();
                var compiled = query.Compiled;
                compiled.Acquire();
                try
                {
                    using var matches = QueryRunner.Run(this, compiled, false, true);
                    for (var i = 0; i < matches.Count; i++)
                        if (callback(new QueryMatch(compiled, this, matches[i])) == false)
                            break;
                }
                finally
                {
                    compiled.Release();
                }
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Runs a query here for <see cref="Any"/> and <see cref="Count"/>, unordered; the caller disposes the matches.
        /// </summary>
        TupleBuffer RunQuery(Query query, bool stopAtFirst)
        {
            ArgumentNullException.ThrowIfNull(query);
            CheckQuery(query);
            var held = EnterRead();
            try
            {
                ThrowIfStopped();
                return QueryRunner.Run(this, query.Compiled, stopAtFirst, false);
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Checks that a query belongs to this scope's graph.
        /// </summary>
        void CheckQuery(Query query)
        {
            if (ReferenceEquals(query.Graph, Graph) == false)
                throw new ArgumentException($"Query '{query.Name}' belongs to another graph.", nameof(query));
        }

    }

}
