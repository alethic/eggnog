using System;
using System.Collections.Generic;
using System.Linq;

using BenchmarkDotNet.Attributes;

using Eggnog.Benchmarks.Library.Predicates;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// What a host query (<see cref="Eggnog.Query"/>) costs on the predicate theory's frozen scopes, as the rules the
    /// scopes inherit grow: a query defined on the graph before any schema scope is created, counted on the frozen
    /// discriminated schema (<see cref="CountOnFrozenSchema"/>); a per-request child of that schema in which a query
    /// defined on the graph for the request is counted, the query and the child then disposed
    /// (<see cref="DefineInRequestChild"/>); and a query defined on the graph after its root is frozen, counted once on
    /// the root and disposed (<see cref="DefineOnFrozenRoot"/>). All three match the present paths whose types admit a
    /// string. <see cref="Rules"/> adds global rules that no run executes or matches, defined before the root freezes,
    /// so that a run shows whether a query's cost stays flat as the rules a frozen scope inherits grow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CountOnFrozenSchema"/> and <see cref="DefineInRequestChild"/> run on <see cref="PredicateCorpus"/>'s
    /// graph, on the benchmarks' copy of the tests' predicate fixture: the theory, then the host query and the
    /// unscheduled rules, then the empty root frozen and every schema of the corpus compiled under it, its every answer
    /// checked. BenchmarkDotNet chooses their invocation count. Only the public API is used. A query on a frozen scope
    /// runs in a transient child of the scope, made and released by the call; in a writable scope, such as a
    /// per-request child, it runs as an iteration of that scope; either way it writes nothing. A count on the frozen
    /// schema, a per-request child with its count, and a definition on the frozen root with its count each take one
    /// epoch of the graph's limit (<see cref="EGraph.Usage"/>) and no id, so a run stays far below the limits.
    /// </para>
    /// <para>
    /// <see cref="DefineOnFrozenRoot"/> runs on a fresh graph each iteration (<see cref="IterationSetup"/>): the theory,
    /// the host query and the unscheduled rules, as <see cref="PredicateCorpus"/>'s graph has them before its root
    /// freezes, then the empty root frozen, with no schema. An iteration runs a fixed number of invocations
    /// (<see cref="RootInvocations"/>), so that its mean is taken over the same definitions on a fresh frozen root
    /// whatever an invocation costs and whatever a disposal frees, and does not depend on the invocations that ran
    /// before it.
    /// </para>
    /// <para>
    /// A query defined after the root froze is allowed. Its definition lowers it to one engine rule and registers three
    /// external functions: its filter's call, its match collector, and the panic function of the collector's call. Its
    /// disposal frees them and its name, and from an iteration's second invocation on, the rule and the functions take
    /// the ids the previous disposal freed; the next invocation defines the query again under the same name, so an
    /// invocation that left its query alive fails the next one. Per invocation only counters grow: the graph's
    /// numbering of rule descriptions and the generation of the rule id taken, by one each, and the graph's epochs, by
    /// one for the count. Each registration and each removal copies the graph's registry of external functions whole,
    /// six copies per invocation, so the bytes of a definition and of a disposal grow with the functions the graph has
    /// registered, and so with <see cref="Rules"/>: each unscheduled rule registers one. The definition for a
    /// per-request child (<see cref="DefineInRequestChild"/>) is the same definition on the graph, and registers and
    /// frees the same.
    /// </para>
    /// <para>
    /// The setup checks every answer of the corpus; that the discriminated schema derived nothing from the unscheduled
    /// rules, the first of which would derive <c>mentioned(seq)</c> there; that the host query matches exactly the four
    /// expected paths of the discriminated schema and none at the root; that a query defined after the root froze
    /// matches in a child of the root made after the definition; and one invocation of each benchmark. Every invocation
    /// checks its count. A count of none on the empty frozen root would hold as well if no count ran, so the iteration
    /// cleanup checks that the iteration's invocations counted there: the fresh graph's epochs grew. A count of four
    /// holds as well on the frozen schema as in a child of it, so no check tells where
    /// <see cref="DefineInRequestChild"/> counted. The global cleanup checks that no schema has a live child, which
    /// shows a per-request child left alive, though BenchmarkDotNet only logs its failure; the transient child a query
    /// on a frozen scope runs in is not listed among the scope's children (<see cref="Scope.Children"/>), so no check
    /// sees one left alive. The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [LatencyColumns]
    public class PredicateHostQueryBenchmarks
    {

        /// <summary>
        /// The invocations one iteration of <see cref="DefineOnFrozenRoot"/> runs on its fresh graph. Fixed rather than
        /// chosen by BenchmarkDotNet, so that every run's iteration makes the same definitions.
        /// </summary>
        const int RootInvocations = 4096;

        /// <summary>
        /// The paths the discriminated schema holds present whose types admit a string, in ordinal order: <c>kind</c>,
        /// whose domain settles its type; <c>label</c> and <c>type</c>, declared strings; and <c>uid</c>, whose stored
        /// form describes a string or null. <c>seq</c> and <c>flag</c> hold a number and a boolean, and <c>meta</c> has
        /// no type claim.
        /// </summary>
        static readonly string[] presentStrings = ["kind", "label", "type", "uid"];

        PredicateCorpus? corpus;
        Query? query;
        PredicateTheory? fresh;
        long freshEpochs;

        /// <summary>
        /// Gets or sets the number of global rules the graph defines beyond the theory's, before its root freezes, in a
        /// ruleset no schedule runs: every schema scope inherits them, and no run executes or matches them.
        /// </summary>
        [Params(0, 100, 1000)]
        public int Rules { get; set; }

        /// <summary>
        /// Builds and checks the corpus, with the host query and the unscheduled rules defined before its schemas, and
        /// checks that the discriminated schema derived nothing from the unscheduled rules; then checks the host query's
        /// matches and one invocation of each benchmark; builds a fresh frozen root, as every iteration of
        /// <see cref="DefineOnFrozenRoot"/> does, and checks that a query defined on it matches in a child of it made
        /// after the definition.
        /// </summary>
        /// <exception cref="InvalidOperationException">A schema or a query of the corpus failed its check, a schema's
        /// compilation ran the unscheduled rules, the host query matches other paths, a query defined on the frozen root
        /// matches wrongly, or a benchmark's count is wrong, did not run, or left a per-request child alive.</exception>
        [GlobalSetup]
        public void GlobalSetup()
        {
            var rules = Rules;
            corpus = new PredicateCorpus(c => query = DefineBeforeFreeze(c, rules));

            // The first unscheduled rule matches seq, whose lower bound in the discriminated schema is 1, and no rule of
            // the theory derives mentioned(seq) there: the row exists only if the schema's compilation ran them.
            if (corpus.Discriminated.Check(corpus.Theory.Mentioned.Of("seq")))
                throw new InvalidOperationException("The discriminated schema holds mentioned(seq): its compilation ran the unscheduled rules.");

            var p = (Var<string>)query!.Variables[0];
            var matched = new List<string>();
            corpus.Discriminated.ForEachMatch(query, m =>
            {
                matched.Add(m.Get(p));
                return true;
            });
            if (matched.Order(StringComparer.Ordinal).SequenceEqual(presentStrings) == false)
                throw new InvalidOperationException($"The host query matches {string.Join(", ", matched)} in the discriminated schema; expected {string.Join(", ", presentStrings)}.");

            var atRoot = corpus.Theory.Graph.Root.Count(query);
            if (atRoot != 0)
                throw new InvalidOperationException($"The host query matches {atRoot} paths at the empty root.");

            CountOnFrozenSchema();
            DefineInRequestChild();
            corpus.CheckNoLiveScope();

            IterationSetup();
            DefineOnFrozenRoot();
            IterationCleanup();
            CheckDefinedOnFrozenRootMatches(fresh!);
        }

        /// <summary>
        /// Builds the fresh graph an iteration of <see cref="DefineOnFrozenRoot"/> runs on: the theory, the host query
        /// and the unscheduled rules, then the empty root frozen; and notes the epochs the graph has used
        /// (<see cref="EGraph.Usage"/>), for the iteration cleanup.
        /// </summary>
        [IterationSetup(Target = nameof(DefineOnFrozenRoot))]
        public void IterationSetup()
        {
            var c = new PredicateTheory(new EGraph());
            DefineBeforeFreeze(c, Rules);
            c.Graph.Root.Freeze();
            fresh = c;
            freshEpochs = c.Graph.Usage.Epochs;
        }

        /// <summary>
        /// Counts the host query's matches on the frozen discriminated schema: the query was defined before the schema
        /// scope was created, and runs in a transient child of it.
        /// </summary>
        /// <returns>The matches.</returns>
        /// <exception cref="InvalidOperationException">The count is not the number of expected paths.</exception>
        [Benchmark]
        public long CountOnFrozenSchema()
        {
            var count = corpus!.Discriminated.Count(query!);
            if (count != presentStrings.Length)
                throw new InvalidOperationException($"The host query counted {count} matches in the discriminated schema; expected {presentStrings.Length}.");

            return count;
        }

        /// <summary>
        /// Creates a per-request child of the frozen discriminated schema, defines a query of the host query's body on
        /// the graph for the request, counts its matches in the child, which holds the schema's facts, and disposes the
        /// query and the child: a request that defines its own query.
        /// </summary>
        /// <returns>The matches.</returns>
        /// <exception cref="ArgumentException">A query of the same name is still alive.</exception>
        /// <exception cref="InvalidOperationException">The count is not the number of expected paths.</exception>
        [Benchmark]
        public long DefineInRequestChild()
        {
            var c = corpus!.Theory;
            using var child = corpus.Discriminated.CreateChild();
            using var request = DefinePresentStrings(c, "request");
            var count = child.Count(request);
            if (count != presentStrings.Length)
                throw new InvalidOperationException($"A query defined for a per-request child counted {count} matches there; expected {presentStrings.Length}.");

            return count;
        }

        /// <summary>
        /// Defines a query of the host query's body on the graph, whose root is frozen, counts its matches once on the
        /// root, which holds none, and disposes it: a query made for one request.
        /// </summary>
        /// <returns>The matches: none.</returns>
        /// <exception cref="ArgumentException">A query of the same name is still alive.</exception>
        /// <exception cref="InvalidOperationException">The count is not 0.</exception>
        [Benchmark]
        [InvocationCount(RootInvocations)]
        public long DefineOnFrozenRoot()
        {
            var c = fresh!;
            using var request = DefinePresentStrings(c, "request");
            var count = c.Graph.Root.Count(request);
            if (count != 0)
                throw new InvalidOperationException($"A query defined on the frozen root counted {count} matches there; expected none.");

            return count;
        }

        /// <summary>
        /// Checks that the iteration's invocations counted on the fresh root, which their count of none cannot show:
        /// the graph's epochs grew since the iteration setup, as a count takes one and a definition or a disposal none.
        /// </summary>
        /// <exception cref="InvalidOperationException">The fresh graph's epochs did not grow.</exception>
        [IterationCleanup(Target = nameof(DefineOnFrozenRoot))]
        public void IterationCleanup()
        {
            if (fresh!.Graph.Usage.Epochs == freshEpochs)
                throw new InvalidOperationException("No invocation of the iteration counted on the fresh root: its graph's epochs did not grow.");
        }

        /// <summary>
        /// Checks that no invocation of <see cref="DefineInRequestChild"/> left its per-request child alive: no schema
        /// has a live child. A query on a frozen scope runs in a transient child, which a scope does not list among its
        /// children, so this cannot see one left alive.
        /// </summary>
        /// <exception cref="InvalidOperationException">A schema has a live child, or the root has one that is not a
        /// schema.</exception>
        [GlobalCleanup]
        public void GlobalCleanup() => corpus!.CheckNoLiveScope();

        /// <summary>
        /// Makes the definitions the graph takes after the theory's and before its root freezes: the host query, then
        /// <paramref name="rules"/> unscheduled rules.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="rules">The number of unscheduled rules.</param>
        /// <returns>The host query.</returns>
        static Query DefineBeforeFreeze(PredicateTheory c, int rules)
        {
            var defined = DefinePresentStrings(c, "present strings");
            DefineUnscheduled(c, rules);
            return defined;
        }

        /// <summary>
        /// Checks that a query defined on the graph after its root froze matches: in a child of the root made after the
        /// definition, which holds one present path whose types admit a string and one whose types do not, it counts
        /// one match.
        /// </summary>
        /// <param name="c">The theory, whose graph's root is frozen and empty.</param>
        /// <exception cref="InvalidOperationException">The query counts another number of matches.</exception>
        static void CheckDefinedOnFrozenRootMatches(PredicateTheory c)
        {
            using var request = DefinePresentStrings(c, "request");
            using var child = c.Graph.Root.CreateChild();
            child.Assume(c.Present.Of("a"), c.Types.Of("a").Is(Set.Of(["string", "null"])), c.Present.Of("n"), c.Types.Of("n").Is(Set.Of(["number"])));
            var count = child.Count(request);
            if (count != 1)
                throw new InvalidOperationException($"A query defined on the frozen root counted {count} matches in a child of it with one present string path; expected 1.");
        }

        /// <summary>
        /// Defines the query of the present paths whose types admit a string: <c>present(p)</c> and
        /// <c>types(p) = t</c>, where <c>t</c> holds <c>"string"</c>, a filter that lowers to an external function.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="name">The query's name.</param>
        /// <returns>The query.</returns>
        static Query DefinePresentStrings(PredicateTheory c, string name) => c.Graph.DefineQuery(name, q =>
        {
            var (p, t) = q.Vars<string, Set<string>>("p", "t");
            q.When(c.Present.Of(p), t.Is(c.Types.Of(p)));
            q.Where(Primitives.SetContains<string>().Of(t, "string"));
        });

        /// <summary>
        /// Defines <paramref name="count"/> global rules in a ruleset of their own, which no schedule of the theory or
        /// of this benchmark runs. Each is shaped as the theory's lattice rules are: it reads two of the theory's
        /// tables, filters on a constant of its own, so it registers one external function, and writes a third.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="count">The number of rules.</param>
        static void DefineUnscheduled(PredicateTheory c, int count)
        {
            var unscheduled = c.Graph.DefineRuleset("unscheduled");
            for (var i = 0; i < count; i++)
            {
                var bound = (long)i;
                unscheduled.DefineRule($"bound above {i}", r =>
                {
                    var (p, l) = r.Vars<string, long>("p", "l");
                    r.When(c.Present.Of(p), l.Is(c.Lo.Of(p)));
                    r.Where(Int64Primitives.Greater.Of(l, bound));
                    r.Then(c.Mentioned.Of(p));
                });
            }
        }

    }

}
