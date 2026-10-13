using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Extraction over a context: an on-demand extractor made through the e-graph's root API (the baseline), the same
    /// through the context form at the root, and the context form in a child of the frozen root that made terms and
    /// unions of its own, frozen or not (<see cref="FrozenChild"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root holds 40,000 terms over <c>leaf(i64)</c>, <c>wrap(E)</c> and <c>pair(E, E)</c> with 4,000 unions, drawn
    /// from the fixed seed <c>0x5EED</c>, and is frozen; the child adds 4,000 terms and 400 unions. Each invocation creates
    /// a fresh extractor and costs the same 4,000 classes, drawn from every level, so it measures discovery (index probes
    /// and row reads) and Knuth's algorithm over what they reach, plus the per-query staleness check.
    /// </para>
    /// <para>
    /// The root API and the root context form must cost the same per node: the context form tests once per discovery
    /// round whether it reads layers, and never per node. A child costs one index probe per present layer per producing
    /// function on top. The setup checks that both root forms give the same total cost over the queried classes, that
    /// the child costs no queried root class it keeps canonical more than the root does, and that the child extracts
    /// something.
    /// </para>
    /// <para>The class is not sealed because BenchmarkDotNet generates a class that derives from it.</para>
    /// </remarks>
    public class ContextExtractionBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The number of terms the root makes.
        /// </summary>
        public const int RootTerms = 40_000;

        /// <summary>
        /// The number of terms the child makes.
        /// </summary>
        public const int ChildTerms = 4_000;

        /// <summary>
        /// The number of classes each invocation costs.
        /// </summary>
        public const int Queries = 4_000;

        /// <summary>
        /// The seed the terms, unions and queries are drawn from.
        /// </summary>
        public const int Seed = 0x5EED;

        ExtractionSchema? schema;
        EqSort? sort;
        EGraphContext? child;
        Value[]? rootQueries;
        Value[]? childQueries;

        /// <summary>
        /// Gets or sets whether the child is frozen too.
        /// </summary>
        [Params(false, true)]
        public bool FrozenChild { get; set; }

        /// <summary>
        /// Builds the frozen root and the child, and checks that the extractors agree.
        /// </summary>
        /// <exception cref="InvalidOperationException">The extractors disagree.</exception>
        [GlobalSetup]
        public void GlobalSetup()
        {
            var random = new Random(Seed);
            var egraph = new EGraph(ParallelHeuristics.Serial);
            var i64 = new BaseSort<Int64Codec, long>("i64");
            var e = new EqSort("E");
            var i64Column = ColumnTy.Base(egraph.BaseValues.RegisterType<Int64Codec, long>());
            schema = new ExtractionSchema(egraph);
            sort = e;
            var leaf = Constructor(egraph, schema, "leaf", e, [i64], [i64Column]);
            var wrap = Constructor(egraph, schema, "wrap", e, [e], [ColumnTy.Id]);
            var pair = Constructor(egraph, schema, "pair", e, [e, e], [ColumnTy.Id, ColumnTy.Id]);

            var root = egraph.Root;
            var terms = new Value[RootTerms + ChildTerms];
            Make(root, terms, 0, RootTerms, random, egraph, leaf, wrap, pair);
            for (var i = 0; i < RootTerms / 10; i++)
                root.StageUnion(terms[random.Next(RootTerms)], terms[random.Next(RootTerms)]);

            root.Freeze();
            child = root.CreateChild();
            Make(child, terms, RootTerms, ChildTerms, random, egraph, leaf, wrap, pair);
            for (var i = 0; i < ChildTerms / 10; i++)
                child.StageUnion(terms[random.Next(terms.Length)], terms[random.Next(terms.Length)]);

            child.FlushUpdates();
            if (FrozenChild)
                child.Freeze();

            rootQueries = new Value[Queries];
            childQueries = new Value[Queries];
            for (var i = 0; i < Queries; i++)
            {
                var term = terms[random.Next(terms.Length)];
                childQueries[i] = child.Find(term);
                rootQueries[i] = root.Find(terms[random.Next(RootTerms)]);
            }

            if (RootApi() != RootContext())
                throw new InvalidOperationException("The root API and the root context form disagree.");

            var atRoot = Extractor.CreateOnDemand(schema);
            var inChild = Extractor.CreateOnDemand(schema, child);
            foreach (var value in rootQueries)
                if (child.Find(value) == value && atRoot.TryGetCost(e, value, out var cost) && (inChild.TryGetCost(e, value, out var childCost) == false || childCost > cost))
                    throw new InvalidOperationException("The child costs a root class more than the root does.");

            if (Child() <= 0)
                throw new InvalidOperationException("The child extracted nothing.");
        }

        /// <summary>
        /// The root API's on-demand extractor.
        /// </summary>
        /// <returns>The sum of the costs.</returns>
        [Benchmark(Baseline = true)]
        public long RootApi() => Sum(Extractor.CreateOnDemand(schema!), rootQueries!);

        /// <summary>
        /// The context form at the root.
        /// </summary>
        /// <returns>The sum of the costs.</returns>
        [Benchmark]
        public long RootContext() => Sum(Extractor.CreateOnDemand(schema!, schema!.EGraph.Root), rootQueries!);

        /// <summary>
        /// The context form in the child.
        /// </summary>
        /// <returns>The sum of the costs.</returns>
        [Benchmark]
        public long Child() => Sum(Extractor.CreateOnDemand(schema!, child!), childQueries!);

        /// <inheritdoc />
        public long ItemsPerInvocation(string benchmarkMethod) => Queries;

        /// <summary>
        /// Costs every queried class.
        /// </summary>
        long Sum(Extractor extractor, Value[] queries)
        {
            long sum = 0;
            foreach (var value in queries)
                if (extractor.TryGetCost(sort!, value, out var cost))
                    sum += cost;

            return sum;
        }

        /// <summary>
        /// Makes <paramref name="count"/> terms in <paramref name="context"/>: a leaf when there is nothing to build on
        /// or one time in four, else a wrap or a pair over terms already made.
        /// </summary>
        static void Make(EGraphContext context, Value[] terms, int start, int count, Random random, EGraph egraph, FunctionId leaf, FunctionId wrap, FunctionId pair)
        {
            for (var i = start; i < start + count; i++)
            {
                terms[i] = i == 0 || random.Next(4) == 0
                    ? context.StageTerm(leaf, [egraph.BaseValues.Get<Int64Codec, long>(random.Next(1000))])
                    : random.Next(2) == 0
                        ? context.StageTerm(wrap, [terms[random.Next(i)]])
                        : context.StageTerm(pair, [terms[random.Next(i)], terms[random.Next(i)]]);

                if (i % 1000 == 999)
                    context.FlushUpdates();
            }

            context.FlushUpdates();
            for (var i = start; i < start + count; i++)
                terms[i] = context.Find(terms[i]);
        }

        /// <summary>
        /// Declares a constructor of cost 1 producing <paramref name="output"/> in the e-graph and the schema.
        /// </summary>
        static FunctionId Constructor(EGraph egraph, ExtractionSchema schema, string name, EqSort output, Sort[] inputs, ColumnTy[] columns)
        {
            var function = egraph.AddTable(new FunctionConfig
            {
                Schema = [.. columns, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = name,
            });
            schema.Declare(function, new FunctionSignature { Inputs = inputs, Output = output, IsConstructor = true, Cost = 1 });
            return function;
        }

    }

}
