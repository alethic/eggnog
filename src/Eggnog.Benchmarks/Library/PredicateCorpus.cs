using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Benchmarks.Library.Predicates;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// The predicate theory's query corpus in a graph of its own, compiled and checked: the theory, the frozen schemas
    /// <see cref="ScopedQueryCases.CompileAll"/> compiles under the frozen root (one per case, then the discriminated
    /// schema), and every query of the corpus ready to be asked (<see cref="CorpusQuery"/>), in the order of
    /// <see cref="PredicateQuery"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Building it asks every query once and checks every answer, so that a broken engine fails a benchmark's setup
    /// instead of producing a number: each case's result, by the case's own check, and each discriminated-schema
    /// query's extraction, its rendering and its cost, without the query's case rules and with them. Where the tests
    /// assert an extraction, it is theirs (<c>DiscriminatedSchemaTests</c>: <see cref="PredicateQuery.KindByKey"/>,
    /// <see cref="PredicateQuery.EveryKind"/> and <see cref="PredicateQuery.ExceptKindB"/> without the case rules;
    /// <see cref="PredicateQuery.KindsByRef"/>, <see cref="PredicateQuery.RefPerKind"/> and
    /// <see cref="PredicateQuery.Flagged"/> with them). The other six are the extractions the engine gives, which no
    /// test asserts as values; a run that gives another answer did other work, so it fails rather than produce a time
    /// that does not compare.
    /// </para>
    /// <para>
    /// Building it also checks that each value of <see cref="PredicateQuery"/> asks the query it names, by the query's
    /// name (<see cref="CorpusQuery.Name"/>): a case's, or a discriminated-schema query's predicate, with or without the
    /// case rules. Tied to the corpus only by its position, a value would time another query, unchecked, once the values
    /// or the corpus were reordered, and a comparison would set that time against the baseline's for the value.
    /// </para>
    /// <para>
    /// The graph is serial (<see cref="EGraphOptions.Default"/>), so extraction ties break the same way at every run.
    /// </para>
    /// </remarks>
    internal sealed class PredicateCorpus
    {

        /// <summary>
        /// The predicates of <see cref="DiscriminatedSchema.Queries"/>, in its order, as they render.
        /// </summary>
        static readonly string[] predicates =
        [
            $"(And (StrEq \"kind\" \"b\") (UuidEq \"body.key\" \"{DiscriminatedSchema.Key}\"))",
            $"(And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\"))",
            $"(Or (And (StrEq \"kind\" \"a\") (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")) (And (StrEq \"kind\" \"b\") (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")))",
            $"(And (And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")) (And (BoolTrue \"flag\") (InstantGt \"meta.updated\" \"{DiscriminatedSchema.Since}\")))",
            "(And (StrEq \"type\" \"Record\") (Or (StrEq \"kind\" \"a\") (Or (StrEq \"kind\" \"b\") (StrEq \"kind\" \"c\"))))",
            $"(And (Not (StrEq \"kind\" \"b\")) (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\"))",
        ];

        /// <summary>
        /// The query each value of <see cref="PredicateQuery"/> before <see cref="PredicateQuery.Corpus"/> names, by the
        /// query's name (<see cref="CorpusQuery.Name"/>): a case's name, or a discriminated-schema query's predicate,
        /// followed by " with the case rules" for the queries that run them.
        /// </summary>
        static readonly (PredicateQuery Query, string Name)[] named =
        [
            (PredicateQuery.DeclaredFormLowers, "a declared stored form lowers the UUID comparison"),
            (PredicateQuery.NoDiscriminator, "without the discriminator nothing is proven and nothing lowers"),
            (PredicateQuery.UnloweredDisjunction, "without the case rules, horn and lattice rules alone do not lower a disjunction"),
            (PredicateQuery.RefusedValueGuard, "a domain less a refused value proves the guard"),
            (PredicateQuery.UndiscriminatedUnion, "without the case rules, the branches of an undiscriminated union never meet"),
            (PredicateQuery.BeyondDeclaredBound, "a comparison beyond a declared bound is false"),
            (PredicateQuery.InsideDeclaredRange, "a bound inside the declared range is left alone"),
            (PredicateQuery.DeclaredTypeExact, "a declared type makes a weakened ordering comparison exact"),
            (PredicateQuery.NoDeclaredType, "without a declared type the comparison stays weakened"),
            (PredicateQuery.DeclaredTautology, "a declared tautology is deleted"),
            (PredicateQuery.NoPresenceClaim, "without a presence claim the comparison stays"),
            (PredicateQuery.IntegerOutsideDomain, "an integer outside a declared domain is false"),
            (PredicateQuery.IntegerInsideDomain, "an integer inside the declared domain is left alone"),
            (PredicateQuery.KindByKey, predicates[0]),
            (PredicateQuery.KindsByRef, predicates[1]),
            (PredicateQuery.RefPerKind, predicates[2]),
            (PredicateQuery.Flagged, predicates[3]),
            (PredicateQuery.EveryKind, predicates[4]),
            (PredicateQuery.ExceptKindB, predicates[5]),
            (PredicateQuery.KindByKeyWithCases, predicates[0] + " with the case rules"),
            (PredicateQuery.KindsByRefWithCases, predicates[1] + " with the case rules"),
            (PredicateQuery.RefPerKindWithCases, predicates[2] + " with the case rules"),
            (PredicateQuery.FlaggedWithCases, predicates[3] + " with the case rules"),
            (PredicateQuery.EveryKindWithCases, predicates[4] + " with the case rules"),
            (PredicateQuery.ExceptKindBWithCases, predicates[5] + " with the case rules"),
        ];

        /// <summary>
        /// Each discriminated-schema query's extraction without the query's case rules, in the order of
        /// <see cref="DiscriminatedSchema.Queries"/>: its rendering and its cost.
        /// </summary>
        static readonly (string Extraction, ulong Cost)[] withoutCases =
        [
            ($"(And (StrEq \"kind\" \"b\") (StrEq \"body.key\" \"{DiscriminatedSchema.Key}\"))", 7),
            ($"(And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\"))", 1010),
            ($"(Or (And (StrEq \"kind\" \"a\") (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")) (And (StrEq \"kind\" \"b\") (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")))", 2013),
            ($"(And (And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")) (And (BoolTrue \"flag\") (StrGt \"meta.updated\" \"{DiscriminatedSchema.Since}\")))", 1017),
            ("(StrEq \"type\" \"Record\")", 3),
            ($"(And (Not (StrEq \"kind\" \"b\")) (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\"))", 1007),
        ];

        /// <summary>
        /// Each discriminated-schema query's extraction with the query's case rules, in the order of
        /// <see cref="DiscriminatedSchema.Queries"/>: its rendering and its cost.
        /// </summary>
        static readonly (string Extraction, ulong Cost)[] withCases =
        [
            ($"(And (StrEq \"kind\" \"b\") (StrEq \"body.key\" \"{DiscriminatedSchema.Key}\"))", 7),
            ($"(And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (StrEq \"body.ref\" \"{DiscriminatedSchema.Ref}\"))", 11),
            ($"(Or (And (StrEq \"kind\" \"a\") (StrEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")) (And (StrEq \"kind\" \"b\") (StrEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")))", 15),
            ($"(And (And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (StrEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")) (And (BoolTrue \"flag\") (StrGt \"meta.updated\" \"{DiscriminatedSchema.Since}\")))", 18),
            ("(StrEq \"type\" \"Record\")", 3),
            ($"(And (Not (StrEq \"kind\" \"b\")) (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\"))", 1007),
        ];

        readonly Scope[] schemas;

        /// <summary>
        /// Builds the corpus in a new graph: defines the theory and the definitions given, compiles every schema under
        /// the frozen root, then asks every query once and checks its answer.
        /// </summary>
        /// <param name="define">Definitions made in the graph after the theory's, while the root is writable and before
        /// any schema scope is created; <c>null</c> for none.</param>
        /// <exception cref="InvalidOperationException">The corpus does not have as many cases and discriminated-schema
        /// queries as <see cref="PredicateQuery"/> names, or a value of it does not ask the query it names; a schema's or
        /// a query's run did not saturate or left a child; a case's result does not hold; an extraction is another term
        /// or has another cost; or a query left its scope alive.</exception>
        public PredicateCorpus(Action<PredicateTheory>? define = null)
        {
            Theory = new PredicateTheory(new EGraph());
            define?.Invoke(Theory);
            schemas = ScopedQueryCases.CompileAll(Theory);
            var cases = ScopedQueryCases.All;
            var terms = DiscriminatedSchema.Queries(Theory);
            if (schemas.Length != cases.Count + 1 || terms.Length != withoutCases.Length || cases.Count + 2 * terms.Length != (int)PredicateQuery.Corpus)
                throw new InvalidOperationException($"The corpus has {cases.Count} cases and {terms.Length} discriminated-schema queries, which are not the queries {nameof(PredicateQuery)} names.");

            var queries = new List<CorpusQuery>();
            for (var i = 0; i < cases.Count; i++)
                queries.Add(CorpusQuery.ForCase(Theory, schemas[i], cases[i]));

            for (var i = 0; i < terms.Length; i++)
                queries.Add(CorpusQuery.ForDiscriminated(Theory, Discriminated, terms[i], false, withoutCases[i].Extraction, withoutCases[i].Cost));

            for (var i = 0; i < terms.Length; i++)
                queries.Add(CorpusQuery.ForDiscriminated(Theory, Discriminated, terms[i], true, withCases[i].Extraction, withCases[i].Cost));

            Queries = queries.ToArray();
            if (named.Select(entry => entry.Query).Order().SequenceEqual(Enum.GetValues<PredicateQuery>().Where(value => value != PredicateQuery.Corpus)) == false)
                throw new InvalidOperationException($"The names of the queries do not name every value of {nameof(PredicateQuery)} but {nameof(PredicateQuery.Corpus)} once.");

            foreach (var (value, name) in named)
                if (Queries[(int)value].Name != name)
                    throw new InvalidOperationException($"{nameof(PredicateQuery)}.{value} asks the query {Queries[(int)value].Name}; it names {name}.");

            foreach (var query in Queries)
                query.Ask();

            CheckNoLiveScope();
        }

        /// <summary>
        /// Gets the theory, and through it the graph.
        /// </summary>
        public PredicateTheory Theory { get; }

        /// <summary>
        /// Gets the frozen schemas: one per case of <see cref="ScopedQueryCases.All"/>, in its order, then the
        /// discriminated schema.
        /// </summary>
        public IReadOnlyList<Scope> Schemas => schemas;

        /// <summary>
        /// Gets the frozen discriminated schema (<see cref="DiscriminatedSchema.Compile"/>).
        /// </summary>
        public Scope Discriminated => schemas[^1];

        /// <summary>
        /// Gets every query of the corpus, in the order of <see cref="PredicateQuery"/>.
        /// </summary>
        public IReadOnlyList<CorpusQuery> Queries { get; }

        /// <summary>
        /// Checks that no query left a scope alive: no schema has a live child, and the root's live children are the
        /// schemas.
        /// </summary>
        /// <exception cref="InvalidOperationException">A schema has a live child, or the root has a live child that is
        /// not a schema.</exception>
        public void CheckNoLiveScope()
        {
            foreach (var schema in schemas)
                if (schema.Children.Count != 0)
                    throw new InvalidOperationException($"A schema has {schema.Children.Count} live children.");

            var children = Theory.Graph.Root.Children.Count;
            if (children != schemas.Length)
                throw new InvalidOperationException($"The root has {children} live children; it compiled {schemas.Length} schemas.");
        }

    }

}
