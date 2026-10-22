using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// Host conjunctive queries: <c>Any</c>, <c>Count</c> and <c>ForEachMatch</c>. Queries write nothing, so they move
    /// neither the version nor an extractor, and a failure in one leaves the scope usable; matches come in content
    /// order, with classes canonical in the queried scope; <c>Dispose</c> frees the engine rule once no call uses it;
    /// and a query allocates per match no more than the engine run it wraps.
    /// </summary>
    [Collection(Relations.Tests.AllocationProbeCollection.Name)]
    public sealed class QueryTests
    {

        /// <summary>
        /// A graph with an <c>edge</c> relation over numbers and a query for its rows.
        /// </summary>
        static (NodeTheory T, Relation<long, long> Edge, Query Edges, Var<long> From, Var<long> To) Edges()
        {
            var t = new NodeTheory();
            var edge = t.Graph.DefineRelation<long, long>("edge");
            Var<long> from = null!;
            Var<long> to = null!;
            var edges = t.Graph.DefineQuery("edges", q =>
            {
                (from, to) = q.Vars<long, long>("from", "to");
                q.When(edge.Of(from, to));
            });
            return (t, edge, edges, from, to);
        }

        [Fact]
        public void AnyCountAndForEachMatchReadTheMatches()
        {
            var (t, edge, edges, from, to) = Edges();
            Assert.False(t.Root.Any(edges));
            Assert.Equal(0, t.Root.Count(edges));

            t.Root.Assume(edge.Of(3L, 1L), edge.Of(1L, 2L), edge.Of(10L, 1L), edge.Of(1L, 10L));

            Assert.True(t.Root.Any(edges));
            Assert.Equal(4, t.Root.Count(edges));
            var seen = new List<(long, long)>();
            t.Root.ForEachMatch(edges, m =>
            {
                seen.Add((m.Get(from), m.Get(to)));
                return true;
            });

            // Content order: numerically, the first column first.
            Assert.Equal(new[] { (1L, 2L), (1L, 10L), (3L, 1L), (10L, 1L) }, seen);
            Assert.Equal("edges", edges.Name);
            Assert.Equal(new Term[] { from, to }, edges.Variables);
        }

        [Fact]
        public void ForEachMatchStopsWhenTheCallbackReturnsFalse()
        {
            var (t, edge, edges, from, _) = Edges();
            t.Root.Assume(edge.Of(1L, 1L), edge.Of(2L, 2L), edge.Of(3L, 3L));
            var seen = new List<long>();
            t.Root.ForEachMatch(edges, m =>
            {
                seen.Add(m.Get(from));
                return seen.Count < 2;
            });

            Assert.Equal(new[] { 1L, 2L }, seen);
        }

        [Fact]
        public void AnyStopsTheEngineEarly()
        {
            var t = new NodeTheory();
            var visits = 0;
            var any = t.Graph.DefineQuery("any number", q =>
            {
                var x = q.Var<long>("x");
                q.When(t.Num.Of(x));
                q.Where(x, _ =>
                {
                    Interlocked.Increment(ref visits);
                    return true;
                });
            });
            var facts = new Fact[5000];
            for (var i = 0; i < facts.Length; i++)
                facts[i] = t.Num.Of((long)i);

            t.Root.Assume(facts);

            Assert.True(t.Root.Any(any));
            var afterAny = visits;
            Assert.InRange(afterAny, 1, facts.Length - 1);

            Assert.Equal(facts.Length, t.Root.Count(any));
            Assert.Equal(afterAny + facts.Length, visits);
        }

        [Fact]
        public void CountCountsDistinctFullBindings()
        {
            var (t, edge, edges, _, _) = Edges();
            t.Root.Assume(edge.Of(1L, 2L), edge.Of(1L, 3L), edge.Of(1L, 2L));
            var sources = t.Graph.DefineQuery("sources", q =>
            {
                var x = q.Var<long>("x");
                q.When(Fact.Exists(t.Lit.Of(x)));
            });

            // Two bindings of (from, to), though both have from = 1; a repeated fact is one row.
            Assert.Equal(2, t.Root.Count(edges));

            // Lit(1) and Lit(2) are one class after a union, but each binding of x is still one match.
            t.Root.Assume(t.Lit.Of(1L).Is(t.Lit.Of(2L)));
            Assert.Equal(2, t.Root.Count(sources));
        }

        [Fact]
        public void AQueryBindsClassesCanonicallyWithTheScopeAsOrigin()
        {
            var t = new NodeTheory();
            Var<Node> n = null!;
            Var<long> x = null!;
            var lits = t.Graph.DefineQuery("lits", q =>
            {
                (n, x) = q.Vars<Node, long>("n", "x");
                q.When(n.Is(t.Lit.Of(x)));
            });
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            t.Root.Union(one, two);

            var classes = new List<Node>();
            t.Root.ForEachMatch(lits, m =>
            {
                classes.Add(m.Get(n));
                return true;
            });

            Assert.Equal(2, classes.Count);
            Assert.Equal(classes[0], classes[1]);
            Assert.Equal(t.Root.Find(one), classes[0]);
            Assert.True(t.Root.AreEqual(classes[0], t.Lit.Of(2L)));
        }

        [Fact]
        public void AQueryMatchesConstantsAndFilters()
        {
            var (t, edge, _, _, _) = Edges();
            t.Root.Assume(edge.Of(1L, 2L), edge.Of(1L, 3L), edge.Of(2L, 3L), edge.Of(3L, 4L));
            Var<long> to = null!;
            var fromOne = t.Graph.DefineQuery("from one", q =>
            {
                to = q.Var<long>("to");
                q.When(edge.Of(1L, to));
                q.Where(Primitives.Greater.Of(to, 2L));
            });

            var seen = new List<long>();
            t.Root.ForEachMatch(fromOne, m =>
            {
                seen.Add(m.Get(to));
                return true;
            });

            Assert.Equal(new[] { 3L }, seen);
            Assert.Equal("(query ((edge 1 to) (> to 2)) (to))", fromOne.ToString());
        }

        [Fact]
        public void AQueryWithoutVariablesHasOneMatchOrNone()
        {
            var t = new NodeTheory();
            var hasOne = t.Graph.DefineQuery("has one", q => q.When(t.Num.Of(1L)));
            Assert.False(t.Root.Any(hasOne));
            Assert.Equal(0, t.Root.Count(hasOne));
            t.Root.Assume(t.Num.Of(1L));
            Assert.True(t.Root.Any(hasOne));
            Assert.Equal(1, t.Root.Count(hasOne));
            Assert.Empty(hasOne.Variables);
        }

        [Fact]
        public void QueriesDoNotMoveTheVersion()
        {
            var (t, edge, edges, _, _) = Edges();
            t.Root.Assume(edge.Of(1L, 2L));
            var version = t.Root.Version;

            t.Root.Any(edges);
            t.Root.Count(edges);
            t.Root.ForEachMatch(edges, _ => true);

            Assert.Equal(version, t.Root.Version);
        }

        [Fact]
        public void AnExtractorStillExtractsAfterQueries()
        {
            var (t, edge, edges, _, _) = Edges();
            t.Root.Assume(edge.Of(1L, 2L));
            var a = t.Root.Make(t.Wrap.Of(t.A.Of()));
            var extractor = t.Root.CreateExtractor();

            Assert.True(t.Root.Any(edges));
            Assert.Equal("(Wrap (A))", extractor.Extract(a).ToString());
            Assert.Equal(1, t.Root.Count(edges));
            Assert.Equal("(Wrap (A))", extractor.Extract(a).ToString());
            t.Root.ForEachMatch(edges, _ => true);
            Assert.Equal("(Wrap (A))", extractor.Extract(a).ToString());
        }

        [Fact]
        public void AFailingFilterThrowsAndLeavesTheScopeUsable()
        {
            var (t, edge, edges, _, _) = Edges();
            var boom = Primitive.Create<long, bool>("boom", x => x == 2 ? throw new InvalidOperationException("two") : true);
            var failing = t.Graph.DefineQuery("failing", q =>
            {
                var (x, y) = q.Vars<long, long>("x", "y");
                q.When(edge.Of(x, y));
                q.Where(boom.Of(y));
            });
            t.Root.Assume(edge.Of(1L, 2L));
            var version = t.Root.Version;

            var error = Assert.Throws<RuleEvaluationException>(() => t.Root.Count(failing));

            Assert.Equal("failing", error.RuleName);
            Assert.Equal("y=2", error.Bindings);
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.False(t.Root.IsFaulted);
            Assert.Null(t.Root.Failure);
            Assert.Equal(version, t.Root.Version);
            Assert.Equal(1, t.Root.Count(edges));
            Assert.Throws<RuleEvaluationException>(() => t.Root.Any(failing));
            Assert.Throws<RuleEvaluationException>(() => t.Root.ForEachMatch(failing, _ => true));

            // The scope runs and writes as before.
            t.Root.Assume(edge.Of(5L, 6L));
            Assert.Equal(2, t.Root.Count(edges));
            Assert.Equal(RunStopReason.Completed, t.Root.Run(t.Rules).StopReason);
        }

        [Fact]
        public void DisposeFreesTheRuleAndLaterUseThrows()
        {
            var (t, edge, edges, _, _) = Edges();
            t.Root.Assume(edge.Of(1L, 2L));
            Assert.Equal(1, t.Root.Count(edges));
            var variant = edges.Compiled.Variants.Default!;
            Assert.True(t.Graph.VariantsByDescription.ContainsKey(variant.Description));
            Assert.StartsWith("$query/edges#", variant.Description);

            edges.Dispose();

            Assert.True(edges.IsDisposed);
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(variant.Description));
            Assert.Null(edges.Compiled.Variants.Default);
            Assert.Throws<ArgumentException>(() => t.Graph.Engine.GetRuleInfo(variant.Id));
            Assert.Throws<ObjectDisposedException>(() => t.Root.Any(edges));
            Assert.Throws<ObjectDisposedException>(() => t.Root.Count(edges));
            Assert.Throws<ObjectDisposedException>(() => t.Root.ForEachMatch(edges, _ => true));
            edges.Dispose();

            // A disposed query's name may be defined again.
            var again = t.Graph.DefineQuery("edges", q =>
            {
                var (x, y) = q.Vars<long, long>("x", "y");
                q.When(edge.Of(x, y));
            });
            Assert.Equal(1, t.Root.Count(again));
        }

        [Fact]
        public async Task DisposalDuringACallOnAnotherThreadWaitsForIt()
        {
            var t = new NodeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var blocking = t.Graph.DefineQuery("blocking", q =>
            {
                var x = q.Var<long>("x");
                q.When(t.Num.Of(x));
                q.Where(x, _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return true;
                });
            });
            t.Root.Assume(t.Num.Of(1L));
            var variant = blocking.Compiled.Variants.Default!;

            var call = Task.Factory.StartNew(() => t.Root.Count(blocking), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            blocking.Dispose();
            Assert.True(blocking.IsDisposed);
            Assert.True(t.Graph.VariantsByDescription.ContainsKey(variant.Description));
            Assert.Equal(1, blocking.Compiled.Users);

            release.Set();
            Assert.Equal(1, await call);
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(variant.Description));
            Assert.Equal(0, blocking.Compiled.Users);
            Assert.Throws<ObjectDisposedException>(() => t.Root.Count(blocking));
        }

        [Fact]
        public void DisposalWhileAnotherThreadRunsDoesNotWaitForTheRun()
        {
            using var run = new BlockingRun();
            var seeds = run.Graph.DefineQuery("seeds", q =>
            {
                var x = q.Var<long>("x");
                q.When(run.Seed.Of(x));
            });
            var variant = seeds.Compiled.Variants.Default!;
            run.Start();

            // The run holds the engine lock inside its blocking filter: the disposal hands the variant to the graph.
            seeds.Dispose();
            Assert.True(seeds.IsDisposed);
            Assert.True(run.Graph.VariantsByDescription.ContainsKey(variant.Description));

            run.Finish();

            // The graph's next engine call frees it.
            run.Graph.Root.Run(run.Rules);
            Assert.False(run.Graph.VariantsByDescription.ContainsKey(variant.Description));
            Assert.Throws<ArgumentException>(() => run.Graph.Engine.GetRuleInfo(variant.Id));
        }

        [Fact]
        public void DisposalFromAPrimitiveIsDeferredToTheNextEngineCall()
        {
            var (t, edge, edges, _, _) = Edges();
            var variant = edges.Compiled.Variants.Default!;
            t.Rules.DefineRule("dispose from a filter", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(edge.Of(x, y));
                r.Where(x, _ =>
                {
                    edges.Dispose();
                    return true;
                });
                r.Then(t.Num.Of(y));
            });
            t.Root.Assume(edge.Of(1L, 2L));

            t.Root.Run(t.Rules);

            Assert.True(edges.IsDisposed);
            Assert.True(t.Root.Check(t.Num.Of(2L)));
            Assert.True(t.Graph.VariantsByDescription.ContainsKey(variant.Description));
            t.Root.Run(t.Rules);
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(variant.Description));
        }

        [Fact]
        public void DisposalInsideACallbackWaitsForTheCallToEnd()
        {
            var (t, edge, edges, from, _) = Edges();
            t.Root.Assume(edge.Of(1L, 2L), edge.Of(2L, 3L));
            var variant = edges.Compiled.Variants.Default!;
            var seen = new List<long>();
            t.Root.ForEachMatch(edges, m =>
            {
                edges.Dispose();
                Assert.True(t.Graph.VariantsByDescription.ContainsKey(variant.Description));
                seen.Add(m.Get(from));
                return true;
            });

            Assert.Equal(new[] { 1L, 2L }, seen);
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(variant.Description));
        }

        [Fact]
        public void AClassConstantThatLostAUnionIsMatchedCanonically()
        {
            var t = new NodeTheory();
            var link = t.Graph.DefineRelation<Node, long>("link");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            Var<long> n = null!;
            var fromB = t.Graph.DefineQuery("from b", q =>
            {
                n = q.Var<long>("n");
                q.When(link.Of(b, n));
            });
            t.Root.Assume(link.Of(a, 3L), link.Of(b, 1L));
            Assert.Equal(1, t.Root.Count(fromB));
            var original = fromB.Compiled.Variants.Default!;

            t.Root.Union(a, b);

            // The engine runs the constant in its canonical form, so the variant lowered at definition serves on.
            Assert.Equal(2, t.Root.Count(fromB));
            Assert.Same(original, fromB.Compiled.Variants.Default);
            Assert.True(t.Graph.VariantsByDescription.ContainsKey(original.Description));
        }

        [Fact]
        public void ClassConstantsAreCheckedAtEachCall()
        {
            var t = new NodeTheory();
            Node made = default;
            t.Root.Write(w =>
            {
                made = w.Make(t.A.Of());
                BatchRejection.FromAnotherThread(w, t.Num.Of(1L));
            });

            // The batch never committed: the query compiles, and refuses the class when it runs.
            var wrapped = t.Graph.DefineQuery("wrapped", q =>
            {
                var w = q.Var<Node>("w");
                q.When(w.Is(t.Wrap.Of(made)));
            });
            var error = Assert.Throws<ArgumentException>(() => t.Root.Any(wrapped));
            Assert.Contains("was made by a Write batch that did not commit", error.Message);

            var none = Assert.Throws<RuleDefinitionException>(() => t.Graph.DefineQuery("default", q =>
            {
                var w = q.Var<Node>("w");
                q.When(w.Is(t.Wrap.Of(default(Node))));
            }));
            Assert.Equal("a class constant is default(ClassId), which names no class", none.Message);

            var other = new NodeTheory();
            var foreign = other.Root.Make(other.A.Of());
            var otherGraph = Assert.Throws<RuleDefinitionException>(() => t.Graph.DefineQuery("foreign", q =>
            {
                var w = q.Var<Node>("w");
                q.When(w.Is(t.Wrap.Of(foreign)));
            }));
            Assert.Equal($"class {foreign.Id} belongs to another graph", otherGraph.Message);
        }

        [Fact]
        public void DefinitionErrorsAreRuleDefinitionExceptions()
        {
            var t = new NodeTheory();
            var unbound = Assert.Throws<RuleDefinitionException>(() => t.Graph.DefineQuery("unbound", q =>
            {
                var (x, y) = q.Vars<long, long>("x", "y");
                q.When(t.Num.Of(x));
            }));
            Assert.Equal("unbound", unbound.RuleName);
            Assert.Equal("variable 'y' is not bound by any When clause", unbound.Message);

            var twice = Assert.Throws<RuleDefinitionException>(() => t.Graph.DefineQuery("twice", q =>
            {
                q.Var<long>("x");
                q.Var<long>("x");
            }));
            Assert.Equal("variable 'x' is declared twice", twice.Message);

            QueryBuilder? escaped = null;
            t.Graph.DefineQuery("first", q =>
            {
                escaped = q;
                var x = q.Var<long>("x");
                q.When(t.Num.Of(x));
            });
            Assert.Throws<InvalidOperationException>(() => escaped!.Var<long>("late"));
            Assert.Throws<ArgumentException>(() => t.Graph.DefineQuery("first", q => q.When(t.Num.Of(1L))));
            Assert.Throws<ArgumentException>(() => t.Graph.DefineQuery("", q => q.When(t.Num.Of(1L))));
            Assert.Throws<ArgumentNullException>(() => t.Graph.DefineQuery("null", null!));

            // A failed definition registers nothing: its name is still free.
            t.Graph.DefineQuery("unbound", q => q.When(t.Num.Of(1L)));
        }

        [Fact]
        public void AQueryOfAnotherGraphOrAnotherQuerysVariableIsRefused()
        {
            var (t, edge, edges, _, _) = Edges();
            var other = new NodeTheory();
            Assert.Throws<ArgumentException>(() => other.Root.Any(edges));
            Assert.Throws<ArgumentException>(() => other.Root.ForEachMatch(edges, _ => true));

            Var<long> foreign = null!;
            t.Graph.DefineQuery("other", q =>
            {
                foreign = q.Var<long>("x");
                q.When(t.Num.Of(foreign));
            });
            t.Root.Assume(edge.Of(1L, 2L));
            Exception? caught = null;
            t.Root.ForEachMatch(edges, m =>
            {
                try
                {
                    m.Get(foreign);
                }
                catch (ArgumentException exception)
                {
                    caught = exception;
                }

                return true;
            });
            Assert.IsType<ArgumentException>(caught);
            Assert.Throws<InvalidOperationException>(() => default(QueryMatch).Get(foreign));
        }

        [Fact]
        public void ACallbackMayCallTheScope()
        {
            var (t, edge, edges, from, to) = Edges();
            t.Root.Assume(edge.Of(1L, 2L), edge.Of(2L, 3L));
            t.Root.ForEachMatch(edges, m =>
            {
                Assert.True(t.Root.Check(edge.Of(m.Get(from), m.Get(to))));
                t.Root.Assume(t.Num.Of(m.Get(to)));
                return true;
            });

            Assert.True(t.Root.Check(t.Num.Of(2L), t.Num.Of(3L)));
        }

        [Fact]
        public void ForEachMatchOrdersStringsOrdinallyAndNumbersNumerically()
        {
            var g = new EGraph();
            var pair = g.DefineRelation<long, string>("pair");
            Var<long> n = null!;
            Var<string> s = null!;
            var pairs = g.DefineQuery("pairs", q =>
            {
                (n, s) = q.Vars<long, string>("n", "s");
                q.When(pair.Of(n, s));
            });

            // Cells of negative and large numbers are interned in insertion order, which is not their numeric order;
            // strings are interned too. Content order is numeric, then ordinal ("B" < "a" < "b").
            var rows = new (long, string)[] { (5_000_000_000, "b"), (-3, "a"), (7, "b"), (-3, "B"), (long.MinValue, ""), (7, "a"), (2_147_483_648, "z") };
            g.Root.Assume(rows.Select(r => pair.Of(r.Item1, r.Item2)).ToArray());
            var seen = new List<(long, string)>();
            g.Root.ForEachMatch(pairs, m =>
            {
                seen.Add((m.Get(n), m.Get(s)));
                return true;
            });

            Assert.Equal(rows.OrderBy(r => r.Item1).ThenBy(r => r.Item2, StringComparer.Ordinal), seen);
            Assert.Equal(seen, g.Root.Rows(pair).Select(r => ((long)r.Keys[0], (string)r.Keys[1])));
        }

        [Fact]
        public void AClassReadAfterACallbackUnionIsCanonical()
        {
            var t = new NodeTheory();
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            Var<Node> n = null!;
            var lits = t.Graph.DefineQuery("lits", q =>
            {
                n = q.Var<Node>("n");
                q.When(n.Is(t.Lit.Of(q.Var<long>("x"))));
            });

            var classes = new List<Node>();
            t.Root.ForEachMatch(lits, m =>
            {
                classes.Add(m.Get(n));
                if (classes.Count == 1)
                    t.Root.Union(one, two);

                return true;
            });

            // Both matches were collected before the union; the second, read after it, is canonical, and names the
            // first's class.
            Assert.Equal(2, classes.Count);
            Assert.Equal(t.Root.Find(classes[1]), classes[1]);
            Assert.True(t.Root.AreEqual(classes[0], classes[1]));
        }

        [Fact]
        public void QueriesAllocateNothingPerMatchBeyondTheEngineRun()
        {
            const int small = 1_000;
            const int large = 21_000;
            var (a, aQuery, aVariable) = Numbers(small);
            var (b, bQuery, bVariable) = Numbers(large);
            var sink = 0L;

            double PerMatch(Func<NodeTheory, Query, Var<long>, long> call) =>
                (Allocated(() => sink += call(b, bQuery, bVariable)) - Allocated(() => sink += call(a, aQuery, aVariable))) / (double)(large - small);

            var engine = PerMatch((t, q, _) =>
            {
                var compiled = q.Compiled;
                var variant = t.Root.Engine.PrepareVariant(compiled.Variants, compiled.Program.WrittenIdConstants, false);
                t.Root.Runtime.QueryMatches = null;
                t.Root.Engine.RunQuery(variant);
                return 0;
            });
            var count = PerMatch((t, q, _) => t.Root.Count(q));
            var each = PerMatch((t, q, x) =>
            {
                var total = 0L;
                t.Root.ForEachMatch(q, m =>
                {
                    total += m.Get(x);
                    return true;
                });
                return total;
            });
            var any = PerMatch((t, q, _) => t.Root.Any(q) ? 1 : 0);

            Assert.True(count <= engine + 16, $"Count allocates {count:0.#} bytes per match; the engine run {engine:0.#}.");
            Assert.True(each <= engine + 16, $"ForEachMatch allocates {each:0.#} bytes per match; the engine run {engine:0.#}.");
            Assert.True(any <= engine + 4, $"Any allocates {any:0.#} bytes per row; a full engine run {engine:0.#}.");
            Assert.NotEqual(0, sink);
        }

        /// <summary>
        /// A graph whose <c>num</c> relation holds <paramref name="rows"/> numbers, and a query over it.
        /// </summary>
        static (NodeTheory T, Query Query, Var<long> X) Numbers(int rows)
        {
            var t = new NodeTheory();
            Var<long> x = null!;
            var query = t.Graph.DefineQuery("nums", q =>
            {
                x = q.Var<long>("x");
                q.When(t.Num.Of(x));
            });
            var facts = new Fact[rows];
            for (var i = 0; i < rows; i++)
                facts[i] = t.Num.Of((long)i);

            t.Root.Assume(facts);
            return (t, query, x);
        }

        /// <summary>
        /// The bytes the second of two calls of <paramref name="action"/> allocates on this thread: the graph is
        /// serial, so the engine runs on this thread, and the first call fills the pools the second reuses.
        /// </summary>
        static long Allocated(Action action)
        {
            action();
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        [Fact]
        public void QueriesAreRefusedInsideAWriteAndFromAPrimitive()
        {
            var (t, edge, edges, _, _) = Edges();
            t.Root.Assume(edge.Of(1L, 2L));
            t.Root.Write(w => Assert.Throws<InvalidOperationException>(() => t.Root.Count(edges)));

            Exception? fromPrimitive = null;
            t.Rules.DefineRule("query from a filter", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(edge.Of(x, y));
                r.Where(x, _ =>
                {
                    fromPrimitive = Record.Exception(() => t.Root.Count(edges));
                    Record.Exception(() => t.Graph.DefineQuery("from a filter", q => q.When(t.Num.Of(1L))));
                    return false;
                });
            });
            t.Root.Run(t.Rules);

            Assert.IsType<InvalidOperationException>(fromPrimitive);
            Assert.Equal("Not allowed from a primitive, lattice, filter, cost function or scheduler", fromPrimitive!.Message);
            Assert.DoesNotContain(t.Graph.Queries, q => q.Name == "from a filter");
        }

        [Fact]
        public void QueriesOnAFrozenScopeRunFromEightThreads()
        {
            var t = new NodeTheory();
            var edge = t.Graph.DefineRelation<long, long>("edge");
            var from = t.Graph.DefineQuery("from", q =>
            {
                var (x, y) = q.Vars<long, long>("x", "y");
                q.When(edge.Of(x, y));
                q.Where(x, x => x % 2 == 0);
            });
            var any = t.Graph.DefineQuery("any", q => q.When(edge.Of(q.Var<long>("x"), 7L)));
            for (var i = 0L; i < 200; i++)
                t.Root.Assume(edge.Of(i, i % 13));

            t.Root.Freeze();
            var version = t.Root.Version;
            var counts = new long[8 * 25];
            var anys = new bool[counts.Length];
            var sums = new long[counts.Length];
            var threads = Support.DedicatedThreads.For(8, counts.Length, i =>
            {
                counts[i] = t.Root.Count(from);
                anys[i] = t.Root.Any(any);
                var sum = 0L;
                t.Root.ForEachMatch(from, m =>
                {
                    sum += m.Get((Var<long>)from.Variables[0]);
                    return true;
                });
                sums[i] = sum;
            });

            Assert.Equal(8, threads);
            Assert.All(counts, c => Assert.Equal(100, c));
            Assert.All(anys, Assert.True);
            Assert.All(sums, s => Assert.Equal(Enumerable.Range(0, 100).Sum(i => 2L * i), s));
            Assert.Equal(version, t.Root.Version);
            Assert.Equal(0, t.Root.Engine.Context.Context.ChildCount);
        }

        [Fact]
        public void AQueryWithScopedAtomsSeesAHostWriteIntoAWritableTaggedScope()
        {
            var t = new ScopeTheory();
            var tagged = t.Graph.DefineQuery("tagged", q =>
            {
                var x = q.Var<string>("x");
                q.When(t.Fact.In(t.Outer).Of(x), t.Fact.Of(x));
            });
            var outer = t.Root.CreateChild(t.Outer);
            var inner = outer.CreateChild(t.Inner);
            Assert.Equal(0, outer.Count(tagged));

            // A host write into the writable tagged scope; the query from that scope and from a descendant.
            outer.Assume(t.Fact.Of("a"));
            Assert.Equal(1, outer.Count(tagged));
            Assert.Equal(1, inner.Count(tagged));
            inner.Assume(t.Fact.Of("d"));
            outer.Assume(t.Fact.Of("b"), t.Fact.Of("c"));
            Assert.Equal(3, inner.Count(tagged));
            Assert.Equal(3, outer.Count(tagged));
            Assert.Equal(4, inner.CountRows(t.Fact));
            Assert.Equal("(query ((fact@outer x) (fact x)) (x))", tagged.ToString());
        }

        /// <summary>
        /// The engine's own allocation for running a query's rule to completion with a collector that keeps nothing.
        /// </summary>
        static long EngineOnly(NodeTheory t, Query query) => Allocated(() =>
        {
            var compiled = query.Compiled;
            var variant = t.Root.Engine.PrepareVariant(compiled.Variants, compiled.Program.WrittenIdConstants, false);
            t.Root.Runtime.QueryMatches = null;
            t.Root.Engine.RunQuery(variant);
        });

        /// <summary>
        /// Hot paths allocate nothing per row, per match or per probe: <c>Count</c> allocates per match no more than
        /// the engine run it wraps, within 16 bytes.
        /// </summary>
        [Fact]
        public void CountAllocatesNoMoreThanTheEnginePerMatch()
        {
            const int small = 1_000;
            const int large = 21_000;
            var (a, qa, _) = Numbers(small);
            var (b, qb, _) = Numbers(large);

            var count = (Allocated(() => b.Root.Count(qb)) - Allocated(() => a.Root.Count(qa))) / (double)(large - small);
            var engine = (EngineOnly(b, qb) - EngineOnly(a, qa)) / (double)(large - small);

            Assert.True(count <= engine + 16, $"Count allocates {count:0.#} bytes per match; the engine run allocates {engine:0.#}.");
        }

        /// <summary>
        /// As <see cref="CountAllocatesNoMoreThanTheEnginePerMatch"/>, for ForEachMatch with a callback that reads each
        /// match.
        /// </summary>
        [Fact]
        public void ForEachMatchAllocatesNoMoreThanTheEnginePerMatch()
        {
            const int small = 1_000;
            const int large = 21_000;
            var (a, qa, xa) = Numbers(small);
            var (b, qb, xb) = Numbers(large);
            long sink = 0;

            var each = (Allocated(() => b.Root.ForEachMatch(qb, m => { sink += m.Get(xb); return true; }))
                - Allocated(() => a.Root.ForEachMatch(qa, m => { sink += m.Get(xa); return true; }))) / (double)(large - small);
            var engine = (EngineOnly(b, qb) - EngineOnly(a, qa)) / (double)(large - small);

            Assert.True(each <= engine + 16, $"ForEachMatch allocates {each:0.#} bytes per match; the engine run allocates {engine:0.#}.");
            Assert.NotEqual(0, sink);
        }

        /// <summary>
        /// <c>Any</c> stops the engine at the first match; per row of the scanned table it allocates no more than an
        /// engine run to completion, within 4 bytes.
        /// </summary>
        [Fact]
        public void AnyAllocatesNoMoreThanAFullEngineRunPerRow()
        {
            const int small = 1_000;
            const int large = 21_000;
            var (a, qa, _) = Numbers(small);
            var (b, qb, _) = Numbers(large);

            var any = (Allocated(() => b.Root.Any(qb)) - Allocated(() => a.Root.Any(qa))) / (double)(large - small);
            var engine = (EngineOnly(b, qb) - EngineOnly(a, qa)) / (double)(large - small);

            Assert.True(any <= engine + 4, $"Any allocates {any:0.#} bytes per row; a full engine run allocates {engine:0.#}.");
        }

        /// <summary>
        /// <c>QueryMatch.Get</c> returns a class canonical in the queried scope, and a <c>ForEachMatch</c> callback may
        /// call the scope: after a union made from the callback, later matches still return canonical classes.
        /// </summary>
        [Fact]
        public void LaterMatchesAreCanonicalAfterTheCallbackWrites()
        {
            var t = new NodeTheory();
            Var<Node> n = null!;
            var lits = t.Graph.DefineQuery("lits", q =>
            {
                n = q.Var<Node>("n");
                q.When(Fact.Exists(t.Lit.Of(q.Var<long>("x"))), n.Is(t.Lit.Of(q.Var<long>("y"))));
            });
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            var classes = new List<Node>();
            t.Root.ForEachMatch(lits, m =>
            {
                classes.Add(m.Get(n));
                if (classes.Count == 1)
                    t.Root.Union(one, two);

                return true;
            });

            foreach (var c in classes)
                Assert.Equal(t.Root.Find(c), c);
        }

    }

}
