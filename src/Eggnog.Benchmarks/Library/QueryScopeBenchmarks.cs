using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// What a transient query scope costs, and what an idle local rule adds to it: a query is a child of a frozen
    /// schema scope that asserts one fact, saturates, checks one fact and is disposed, the way a service asks a
    /// question of a compiled schema. The schema scope defines <see cref="Rules"/> local rules of <see cref="Atoms"/>
    /// body atoms over <see cref="Tables"/> relations; in the <see cref="QueryMode.Idle"/> mode the query asserts into
    /// a relation no rule reads, so every rule is idle, and in <see cref="QueryMode.Active"/> it asserts the one fact
    /// that makes the rules reading <c>p0</c> fire. The allocated bytes per query, against <see cref="Rules"/>, give
    /// the bytes per idle rule (the slope); the times give the latency of a transient child and the time per idle rule
    /// per iteration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The theory is <see cref="LongTables"/>: its root, with 1,000 rows in each <c>p</c> relation, is frozen, and the
    /// schema scope is a child of it that defines the local rules, saturates them and freezes. Only the public API is
    /// used. Each invocation makes and disposes its own query scope, so BenchmarkDotNet chooses the invocation count,
    /// and the memory diagnoser's allocated bytes per operation are the bytes per query.
    /// </para>
    /// <para>
    /// Every invocation checks its result: the run saturated after the expected iterations (none without rules; one,
    /// which changes nothing, for idle rules; two for active ones), and the fact checked holds (the asserted
    /// <c>idle(1)</c>, or the derived <c>q0(999)</c>). The setup checks the schema scope's saturated tables against
    /// the model of <see cref="LongTables.Expected"/>, that an idle query derives nothing, and that one benchmark query
    /// disposes its scope; the cleanup checks that no query scope outlived its invocation. The class is not sealed
    /// because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    public class QueryScopeBenchmarks : Relations.Benchmarks.IItemsPerInvocation
    {

        LongTables? theory;
        Scope? schema;
        Schedule? saturate;
        Fact? assumed;
        Fact? derived;
        int iterations;

        /// <summary>
        /// Gets or sets the body atoms of each local rule.
        /// </summary>
        [Params(2, 4)]
        public int Atoms { get; set; }

        /// <summary>
        /// Gets or sets the number of <c>p</c> relations the rules read.
        /// </summary>
        [Params(10, 30, 50)]
        public int Tables { get; set; }

        /// <summary>
        /// Gets or sets the number of local rules the schema scope defines.
        /// </summary>
        [Params(0, 10, 100, 1000)]
        public int Rules { get; set; }

        /// <summary>
        /// Gets or sets what the query asserts.
        /// </summary>
        [Params(QueryMode.Idle, QueryMode.Active)]
        public QueryMode Mode { get; set; }

        /// <summary>
        /// Builds and freezes the root, compiles and freezes the schema scope, and checks one query.
        /// </summary>
        /// <exception cref="InvalidOperationException">The schema scope's tables differ from the model, the query's run
        /// or its result differs from what the mode expects, or a query left its scope alive.</exception>
        [GlobalSetup]
        public void GlobalSetup()
        {
            theory = new LongTables(Tables);
            theory.Graph.Root.Freeze();
            schema = theory.Graph.Root.CreateChild();
            var t = theory;
            var atoms = Atoms;
            for (var r = 0; r < Rules; r++)
            {
                var rule = r;
                schema.DefineRule(t.Rules, $"rule {r}", b => t.Define(b, rule, atoms));
            }

            saturate = Schedule.Saturate(theory.Rules);
            if (schema.Run(saturate).Saturated == false)
                throw new InvalidOperationException("The schema scope did not saturate.");

            theory.CheckSaturated(schema, Rules, Atoms);
            schema.Freeze();

            assumed = Mode == QueryMode.Idle ? theory.Idle.Of(1L) : theory.P[0].Of(LongTables.Missing);
            derived = theory.Q[0].Of(LongTables.Missing);
            iterations = Rules == 0 ? 0 : Mode == QueryMode.Idle ? 1 : 2;

            // An idle query derives nothing; an active one derives q0(999), which no rule of the schema scope could.
            var query = schema.CreateChild();
            query.Assume(assumed);
            query.Run(saturate);
            var holds = query.Check(derived);
            query.Dispose();
            if (holds != (Mode == QueryMode.Active && Rules > 0))
                throw new InvalidOperationException($"q0({LongTables.Missing}) {(holds ? "holds" : "does not hold")} after a query in mode {Mode}.");

            Query();
            if (schema.Children.Count != 0)
                throw new InvalidOperationException("A query left its scope alive.");
        }

        /// <summary>
        /// One query: a child of the schema scope, one assumed fact, a saturating run, one check, disposal.
        /// </summary>
        /// <returns>The run's iterations.</returns>
        /// <exception cref="InvalidOperationException">The run did not saturate after the expected iterations, or the
        /// checked fact does not hold.</exception>
        [Benchmark]
        public int Query()
        {
            using var query = schema!.CreateChild();
            query.Assume(assumed!);
            var report = query.Run(saturate!);
            var holds = query.Check(Mode == QueryMode.Idle || Rules == 0 ? assumed! : derived!);
            if (report.Saturated == false || report.Iterations != iterations || holds == false)
                throw new InvalidOperationException($"A query ran {report.Iterations} iterations (saturated: {report.Saturated}, checked fact holds: {holds}); expected {iterations}, saturated, and the fact.");

            return report.Iterations;
        }

        /// <summary>
        /// Checks that every query scope was disposed: the schema scope has no live child.
        /// </summary>
        /// <exception cref="InvalidOperationException">A query scope is still alive.</exception>
        [GlobalCleanup]
        public void GlobalCleanup()
        {
            if (schema!.Children.Count != 0)
                throw new InvalidOperationException($"The schema scope has {schema.Children.Count} live query scopes.");
        }

        /// <inheritdoc />
        /// <remarks>
        /// The local rules a query runs: the mean time divided by it is the time per rule per query.
        /// </remarks>
        long Relations.Benchmarks.IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => Rules;

    }

}
