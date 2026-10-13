using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The engine side of one <see cref="Scope"/> (D28): the only way the rest of the rule layer reaches the engine for a
    /// scope's contents. R2 implements it over an engine context for every scope, the root included
    /// (<see cref="ContextEngineScope"/>, §6.6.6); R1's root adapter over the egglog-compatible root API is gone.
    /// </summary>
    /// <remarks>
    /// Every member runs while the scope's <see cref="ScopeGuard"/> is held by the calling thread, or on a frozen scope,
    /// whose reads are thread-safe. Cells are the engine's: sort cells are class ids, value cells base values.
    /// </remarks>
    internal abstract class EngineScope
    {

        /// <summary>
        /// Gets the engine's change stamp (X30): it moves on every effective change to the scope's own layer.
        /// </summary>
        public abstract long Version { get; }

        /// <summary>
        /// Gets whether the graph's <see cref="EGraph.Contradiction"/> holds in the scope.
        /// </summary>
        public abstract bool IsContradictory { get; }

        /// <summary>
        /// Gets the number of rows in the scope's own layers, the engine's row-count estimate (the context database's
        /// <c>TotalSizeEstimate</c>): what <see cref="RunOptions.RowLimit"/> bounds (D42). O(1).
        /// </summary>
        public abstract long OwnRowCount { get; }

        /// <summary>
        /// Gets whether the engine context is frozen (colors §8.1): it never changes again, and its reads are thread-safe.
        /// </summary>
        public abstract bool IsFrozen { get; }

        /// <summary>
        /// Gets whether the engine context is disposed.
        /// </summary>
        public abstract bool IsDisposed { get; }

        /// <summary>
        /// Gets whether the engine faulted the context (X12): an exception escaped one of its writes, runs or rebuilds,
        /// and only disposal is allowed.
        /// </summary>
        public abstract bool IsFaulted { get; }

        /// <summary>
        /// Catches up with non-frozen ancestors and rebuilds when needed; a no-op when the scope is current or frozen.
        /// </summary>
        public abstract void EnsureClean();

        /// <summary>
        /// Gets the engine variants to run for <paramref name="rules"/> in this scope, lowering them on demand (§6.4.4). A
        /// naive rule always runs its naive variant.
        /// </summary>
        /// <param name="rules">The rules of one iteration.</param>
        /// <param name="semiNaive">Whether semi-naive variants are wanted (the internal <c>RunOptions.SemiNaive</c>).</param>
        /// <returns>One variant per rule, in order.</returns>
        public abstract LoweredRule[] PrepareRules(IReadOnlyList<CompiledRule> rules, bool semiNaive);

        /// <summary>
        /// Gets one variant of a program for this scope, lowered with the written values of its class constants: the
        /// engine runs each in its canonical form in the running context and gates watermarks on them (X19, I30), so a
        /// variant never changes once lowered (§6.4.4).
        /// </summary>
        /// <param name="variants">The program's variants.</param>
        /// <param name="writtenIdConstants">The class constants as written.</param>
        /// <param name="semiNaive">Whether the semi-naive variant is wanted.</param>
        /// <returns>The variant.</returns>
        public abstract LoweredRule PrepareVariant(RuleVariants variants, uint[] writtenIdConstants, bool semiNaive);

        /// <summary>
        /// Runs one engine iteration of <paramref name="rules"/> (§6.5.1).
        /// </summary>
        /// <param name="rules">The variants to run.</param>
        /// <returns>What the iteration did.</returns>
        /// <exception cref="Relations.PanicException">A failure was recorded; the rule layer turns it into a
        /// <see cref="RuleEvaluationException"/>.</exception>
        public abstract EngineIteration RunRules(ReadOnlySpan<Relations.EGraphRuleId> rules);

        /// <summary>
        /// Mints a class id for a new term of a host batch (§6.6.1).
        /// </summary>
        /// <returns>The id.</returns>
        public abstract uint FreshId();

        /// <summary>
        /// Stages a row of a host batch: keys then output; a relation row ends with the unit value. Sort cells must be
        /// canonical or fresh; they are canonicalized again (invariant C).
        /// </summary>
        /// <param name="function">The table's engine function.</param>
        /// <param name="row">The row.</param>
        public abstract void StageRow(Relations.FunctionId function, ReadOnlySpan<uint> row);

        /// <summary>
        /// Stages a union of a host batch.
        /// </summary>
        /// <param name="a">One class.</param>
        /// <param name="b">The other class.</param>
        public abstract void StageUnion(uint a, uint b);

        /// <summary>
        /// Merges what was staged and rebuilds. Never called for an empty batch.
        /// </summary>
        /// <returns>Whether the scope changed.</returns>
        /// <exception cref="Relations.PanicException">A merge recorded a failure.</exception>
        public abstract bool Flush();

        /// <summary>
        /// Gets the canonical member of a class in this scope.
        /// </summary>
        /// <param name="id">The class.</param>
        /// <returns>Its canonical id.</returns>
        public abstract uint Find(uint id);

        /// <summary>
        /// Maps the cells of some columns of row-major tuples through <see cref="Find"/>, in place, as one engine call:
        /// the sort columns of a query's matches or a table's rows (§6.6.4, §6.11). Not in the spec's member list; it
        /// spares a lock per cell. O(tuples × columns).
        /// </summary>
        /// <param name="cells">The tuples' cells, <paramref name="width"/> per tuple.</param>
        /// <param name="width">The number of cells per tuple.</param>
        /// <param name="columns">The columns to canonicalize, each below <paramref name="width"/>.</param>
        /// <returns>Whether a cell changed, so that two tuples may now be equal.</returns>
        public abstract bool CanonicalizeColumns(Span<uint> cells, int width, ReadOnlySpan<int> columns);

        /// <summary>
        /// Looks up a row by key: key sort cells are canonicalized here; a class output is canonical.
        /// </summary>
        /// <param name="function">The table's engine function.</param>
        /// <param name="key">The key cells.</param>
        /// <param name="output">The output cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the table holds a row for the key.</returns>
        public abstract bool TryLookup(Relations.FunctionId function, ReadOnlySpan<uint> key, out uint output);

        /// <summary>
        /// Gets the number of rows of a table in this scope's view (<c>Scope.CountRows</c>, §6.11). O(1).
        /// </summary>
        /// <param name="function">The table's engine function.</param>
        /// <returns>The row count.</returns>
        public abstract long TableSize(Relations.FunctionId function);

        /// <summary>
        /// Visits every row of a table in this scope's view: keys then output, sort cells canonical in a clean scope.
        /// Tagged R1b in the spec (for <c>Scope.Rows</c>); R1a's host batches use it to check the merges a batch would
        /// cause (D32). O(rows).
        /// </summary>
        /// <param name="function">The table's engine function.</param>
        /// <param name="visit">Receives each row; the cells are valid only during the visit, and it must not call the
        /// engine.</param>
        public abstract void ForEach(Relations.FunctionId function, Relations.FunctionRowCallback visit);

        /// <summary>
        /// Runs a query's naive engine rule against the scope's current contents (D34, D49): its collector hands each
        /// match to the scope's runtime. It runs as an iteration of the context that writes nothing: the clock advances,
        /// but <see cref="Version"/> does not move, and extraction staleness ignores the clock (E5), so no extractor is
        /// invalidated.
        /// </summary>
        /// <param name="query">The query's variant, prepared for this scope.</param>
        /// <exception cref="Relations.PanicException">A call site recorded a failure; the rule layer turns it into a
        /// <see cref="RuleEvaluationException"/> without faulting the scope.</exception>
        public abstract void RunQuery(LoweredRule query);

        /// <summary>
        /// Creates an engine extractor over this scope as it is now (§6.10).
        /// </summary>
        /// <param name="schema">The extraction schema.</param>
        /// <param name="costs">The engine cost model.</param>
        /// <returns>The extractor.</returns>
        public abstract Relations.Extractor CreateExtractor(Relations.ExtractionSchema schema, Relations.CostModel costs);

        /// <summary>
        /// Extracts the cheapest term of a class with an extractor this scope created.
        /// </summary>
        /// <param name="extractor">The extractor.</param>
        /// <param name="sort">The class's extraction sort.</param>
        /// <param name="id">The class; canonicalized here.</param>
        /// <param name="term">The term, when the result is <c>true</c>.</param>
        /// <returns>Whether a finite term exists.</returns>
        /// <exception cref="InvalidOperationException">The scope changed since the extractor was created.</exception>
        public abstract bool TryExtract(Relations.Extractor extractor, Relations.Sort sort, uint id, out Relations.ExtractedTerm? term);

        /// <summary>
        /// Gets the cost of the cheapest term of a class with an extractor this scope created.
        /// </summary>
        /// <param name="extractor">The extractor.</param>
        /// <param name="sort">The class's extraction sort.</param>
        /// <param name="id">The class; canonicalized here.</param>
        /// <param name="cost">The cost, when the result is <c>true</c>.</param>
        /// <returns>Whether a finite term exists.</returns>
        /// <exception cref="InvalidOperationException">The scope changed since the extractor was created.</exception>
        public abstract bool TryGetCost(Relations.Extractor extractor, Relations.Sort sort, uint id, out long cost);

        /// <summary>
        /// Creates a child context (E7, colors §8.2): thread-safe on a frozen scope; on a writable one, the scope is
        /// brought up to date first and the child shares its thread domain.
        /// </summary>
        /// <param name="options">The child's options.</param>
        /// <returns>The child's engine side.</returns>
        /// <exception cref="InvalidOperationException">The context is at the deepest level (colors X36), faulted or
        /// stopped.</exception>
        /// <exception cref="Relations.PanicException">A failure was recorded on this context.</exception>
        public abstract ContextEngineScope CreateChild(Relations.ContextOptions options);

        /// <summary>
        /// Freezes the context, pre-planning exactly <paramref name="planRules"/> (E10, colors §10.8): it flushes,
        /// rebuilds, plans those rules that have no plan yet on this context's statistics, and freezes storage.
        /// </summary>
        /// <param name="planRules">The rules to plan.</param>
        /// <exception cref="Relations.PanicException">A failure was recorded.</exception>
        public abstract void Freeze(IReadOnlyCollection<Relations.EGraphRuleId> planRules);

        /// <summary>
        /// Disposes the context (E7): it must have no live children and no call in progress in its thread domain on
        /// another thread. A second call does nothing.
        /// </summary>
        public abstract void Dispose();

    }

}
