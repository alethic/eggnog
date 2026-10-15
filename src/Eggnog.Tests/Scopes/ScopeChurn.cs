using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using Eggnog.Tests.Predicates;
using Eggnog.Tests.Predicates.Scoped;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A long-lived graph serving query scopes on the predicate theory (<see cref="PredicateTheory"/>): a frozen empty
    /// root, one frozen schema scope, and per derivation a query child that makes its predicate, assumes it as the where
    /// clause, runs, reads a snapshot and is disposed. The schema declares <c>kind</c> as a discriminator with a
    /// domain of <c>a</c>, <c>b</c>, <c>c</c> (and as many more values as asked), a branch rule on each of the three, and
    /// <c>n</c> as a number within [0, 100].
    /// </summary>
    public sealed class ScopeChurn
    {

        /// <summary>
        /// Initializes the graph and its frozen schema.
        /// </summary>
        /// <param name="domain">The number of values of <c>kind</c>'s domain, at least 3: a split query opens one case
        /// per value.</param>
        public ScopeChurn(int domain = 3)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(domain, 3);
            Theory = new PredicateTheory(new EGraph());
            var c = Theory;
            NeRows = Graph.DefineQuery("ne rows", q =>
            {
                var (p, v) = q.Vars<string, string>("p", "v");
                q.When(c.Ne.Of(p, v));
            });
            Graph.Root.Freeze();
            Schema = Graph.Root.CreateChild(c.Schema);
            var values = new[] { "a", "b", "c" }.Concat(Enumerable.Range(3, domain - 3).Select(i => "v" + i.ToString(CultureInfo.InvariantCulture))).ToArray();
            Schema.Assume(
                c.Present.Of("kind"), c.Discriminator.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of(values)),
                c.Types.Of("n").Is(ValueSet.Of("number")), c.Lo.Of("n").Is(0L), c.Hi.Of("n").Is(100L));
            c.DeclareBranch(Schema, "kind", "a", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(Schema, "kind", "b", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(Schema, "kind", "c", c.Form.Of("at", "instant-ms"));
            Schema.Run(c.Facts);
            Schema.Freeze();
        }

        /// <summary>
        /// Gets the theory.
        /// </summary>
        public PredicateTheory Theory { get; }

        /// <summary>
        /// Gets the graph.
        /// </summary>
        public EGraph Graph => Theory.Graph;

        /// <summary>
        /// Gets the frozen schema scope.
        /// </summary>
        public Scope Schema { get; }

        /// <summary>
        /// Gets the reusable query of the snapshot read: every <c>ne</c> row.
        /// </summary>
        public Query NeRows { get; }

        /// <summary>
        /// Gets the schedule of a plain query: the Horn, lattice and rewrite rulesets to saturation.
        /// </summary>
        public Schedule Plain => Schedule.Saturate(Theory.Horn, Theory.Lattice, Theory.Rewrite);

        /// <summary>
        /// Gets the schedule of a split query: the theory's <see cref="PredicateTheory.Saturation"/>, which splits on a
        /// mentioned discriminator.
        /// </summary>
        public Schedule Split => Theory.Saturation;

        /// <summary>
        /// Runs one derivation: a query child of the schema makes <paramref name="predicate"/>, assumes it as the where
        /// clause, optionally defines a local rule, runs <paramref name="schedule"/>, reads the snapshot
        /// (<see cref="ReadSnapshot"/>) and is disposed.
        /// </summary>
        /// <param name="predicate">The query's predicate.</param>
        /// <param name="schedule">The schedule; <c>null</c> for <see cref="Plain"/>.</param>
        /// <param name="localConstant">The constant of a local rule the query defines, or <c>null</c> for none.</param>
        /// <returns>The run's report.</returns>
        public RunReport Derive(ConstructorTerm<Pred> predicate, Schedule? schedule = null, string? localConstant = null)
        {
            var c = Theory;
            using var query = Schema.CreateChild(c.Query);
            var p = query.Make(predicate);
            query.Assume(c.WhereClause.Of(p));
            if (localConstant is not null)
                DefineLocalRule(query, "local", localConstant);

            var report = query.Run(schedule ?? Plain);
            ReadSnapshot(query);
            return report;
        }

        /// <summary>
        /// Defines a local rule on <paramref name="scope"/> that reads the domain of <c>kind</c>, filters it with a
        /// primitive (a call site the rule owns, registered as an engine external function) and writes
        /// <c>ne(kind, constant)</c>.
        /// </summary>
        /// <param name="scope">The scope, not frozen.</param>
        /// <param name="name">The rule's name.</param>
        /// <param name="constant">The rule's constant, interned when the rule is defined.</param>
        /// <returns>The rule.</returns>
        public Rule DefineLocalRule(Scope scope, string name, string constant)
        {
            var c = Theory;
            return scope.DefineRule(c.Horn, name, r =>
            {
                var d = r.Var<ValueSet<string>>("d");
                r.When(c.Present.Of("kind"), d.Is(c.Dom.Of("kind")));
                r.Where(Primitives.Contains<string>().Of(d, "a"));
                r.Then(c.Ne.Of("kind", constant));
            });
        }

        /// <summary>
        /// Runs <see cref="Derive"/> and returns a weak reference to the query's engine context, taken while it was
        /// live. Not inlined, so no local of the caller keeps the context reachable.
        /// </summary>
        /// <param name="predicate">The query's predicate.</param>
        /// <returns>The weak reference.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public WeakReference DeriveAndTrack(ConstructorTerm<Pred> predicate)
        {
            var c = Theory;
            WeakReference context;
            using (var query = Schema.CreateChild(c.Query))
            {
                context = new WeakReference(query.Engine.Context);
                var p = query.Make(predicate);
                query.Assume(c.WhereClause.Of(p));
                query.Run(Plain);
                ReadSnapshot(query);
            }

            return context;
        }

        /// <summary>
        /// Runs a pass of sibling schemas: <paramref name="siblings"/> schema scopes beside <see cref="Schema"/>, each
        /// declaring <c>kind</c> and defining <paramref name="rules"/> local rules (<see cref="DefineLocalRule"/>, the
        /// constant of rule <c>j</c> being <c>x{j}</c>, so a second pass interns nothing new), frozen, queried once, and
        /// disposed while its query is live, so that its release waits for the query's disposal. Returns weak
        /// references to every sibling scope and every compiled variant of its local rules, taken while they were live.
        /// Not inlined, so no local of the caller keeps them reachable.
        /// </summary>
        /// <param name="siblings">The number of siblings.</param>
        /// <param name="rules">The number of local rules of each.</param>
        /// <returns>The weak references.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public List<WeakReference> RunSiblings(int siblings, int rules)
        {
            var c = Theory;
            var tracked = new List<WeakReference>(siblings * (rules + 1));
            for (var s = 0; s < siblings; s++)
            {
                var sibling = Graph.Root.CreateChild(c.Schema);
                tracked.Add(new WeakReference(sibling));
                sibling.Assume(c.Present.Of("kind"), c.Discriminator.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("a", "b", "c")));
                for (var j = 0; j < rules; j++)
                {
                    var rule = DefineLocalRule(sibling, "rule " + j.ToString(CultureInfo.InvariantCulture), "x" + j.ToString(CultureInfo.InvariantCulture));
                    tracked.Add(new WeakReference(rule.Compiled.Variants.Default));
                }

                sibling.Freeze();
                var query = sibling.CreateChild(c.Query);
                var p = query.Make(c.StrEq.Of("kind", "a"));
                query.Assume(c.WhereClause.Of(p));
                query.Run(Plain);

                // The sibling is retired while its query is live: its release waits for the query's.
                sibling.Dispose();
                ReadSnapshot(query);
                query.Dispose();
            }

            return tracked;
        }

        /// <summary>
        /// The read that ends every derivation: whether <c>kind</c> is present, its domain and types, and every
        /// <c>ne</c> row through the reusable <see cref="NeRows"/>.
        /// </summary>
        /// <param name="query">The query scope.</param>
        /// <returns>The number of things read, so that nothing is optimized away.</returns>
        public int ReadSnapshot(Scope query)
        {
            var c = Theory;
            var read = query.Check(c.Present.Of("kind")) ? 1 : 0;
            if (query.TryGet(c.Dom.Of("kind"), out var dom))
                read += dom.Count;

            if (query.TryGet(c.Types.Of("kind"), out var types))
                read += types.Count;

            query.ForEachMatch(NeRows, m =>
            {
                read++;
                return true;
            });
            return read;
        }

    }

}
