namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// The query <c>PredicateQueryBenchmarks</c> asks: one query of the predicate theory's corpus, or all of them.
    /// Public because benchmark parameters are.
    /// </summary>
    /// <remarks>
    /// The corpus is, in this order: the cases of <see cref="Predicates.ScopedQueryCases.All"/>, each asked of its own
    /// schema; the queries of <see cref="Predicates.DiscriminatedSchema.Queries"/>, asked of the discriminated schema
    /// without the case rules, as the corpus asks them (<see cref="Predicates.DiscriminatedSchema.Ask"/>); and the same
    /// queries with the query's case rules (<see cref="Predicates.DiscriminatedSchema.AskWithCases"/>). The values
    /// before <see cref="Corpus"/> follow that order, and <see cref="PredicateCorpus"/> checks that each asks the query
    /// it names. Each name has at most 20 characters, the width of BenchmarkDotNet's parameter column, which shortens a
    /// longer value in the summary and in the exported reports.
    /// </remarks>
    public enum PredicateQuery
    {

        /// <summary>
        /// The case "a declared stored form lowers the UUID comparison".
        /// </summary>
        DeclaredFormLowers,

        /// <summary>
        /// The case "without the discriminator nothing is proven and nothing lowers".
        /// </summary>
        NoDiscriminator,

        /// <summary>
        /// The case "without the case rules, horn and lattice rules alone do not lower a disjunction".
        /// </summary>
        UnloweredDisjunction,

        /// <summary>
        /// The case "a domain less a refused value proves the guard".
        /// </summary>
        RefusedValueGuard,

        /// <summary>
        /// The case "without the case rules, the branches of an undiscriminated union never meet".
        /// </summary>
        UndiscriminatedUnion,

        /// <summary>
        /// The case "a comparison beyond a declared bound is false".
        /// </summary>
        BeyondDeclaredBound,

        /// <summary>
        /// The case "a bound inside the declared range is left alone".
        /// </summary>
        InsideDeclaredRange,

        /// <summary>
        /// The case "a declared type makes a weakened ordering comparison exact".
        /// </summary>
        DeclaredTypeExact,

        /// <summary>
        /// The case "without a declared type the comparison stays weakened".
        /// </summary>
        NoDeclaredType,

        /// <summary>
        /// The case "a declared tautology is deleted".
        /// </summary>
        DeclaredTautology,

        /// <summary>
        /// The case "without a presence claim the comparison stays".
        /// </summary>
        NoPresenceClaim,

        /// <summary>
        /// The case "an integer outside a declared domain is false".
        /// </summary>
        IntegerOutsideDomain,

        /// <summary>
        /// The case "an integer inside the declared domain is left alone".
        /// </summary>
        IntegerInsideDomain,

        /// <summary>
        /// One document of kind b by key, without the case rules.
        /// </summary>
        KindByKey,

        /// <summary>
        /// The documents of kinds a and b with one ref, written as one disjunction, without the case rules.
        /// </summary>
        KindsByRef,

        /// <summary>
        /// The documents of kinds a and b with one ref, spelt per kind, without the case rules.
        /// </summary>
        RefPerKind,

        /// <summary>
        /// The documents of kinds a and b with one ref that are flagged and were updated since an instant, without the
        /// case rules.
        /// </summary>
        Flagged,

        /// <summary>
        /// Documents of every kind, a tautology over <c>kind</c>, without the case rules.
        /// </summary>
        EveryKind,

        /// <summary>
        /// The documents with one ref other than those of kind b, without the case rules.
        /// </summary>
        ExceptKindB,

        /// <summary>
        /// <see cref="KindByKey"/> with the query's case rules.
        /// </summary>
        KindByKeyWithCases,

        /// <summary>
        /// <see cref="KindsByRef"/> with the query's case rules.
        /// </summary>
        KindsByRefWithCases,

        /// <summary>
        /// <see cref="RefPerKind"/> with the query's case rules.
        /// </summary>
        RefPerKindWithCases,

        /// <summary>
        /// <see cref="Flagged"/> with the query's case rules.
        /// </summary>
        FlaggedWithCases,

        /// <summary>
        /// <see cref="EveryKind"/> with the query's case rules.
        /// </summary>
        EveryKindWithCases,

        /// <summary>
        /// <see cref="ExceptKindB"/> with the query's case rules.
        /// </summary>
        ExceptKindBWithCases,

        /// <summary>
        /// Every query above, in this order, in one invocation.
        /// </summary>
        Corpus,

    }

}
