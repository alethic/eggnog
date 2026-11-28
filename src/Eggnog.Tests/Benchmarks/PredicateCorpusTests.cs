using System;
using System.Linq;

using Eggnog.Benchmarks.Library;
using Eggnog.Benchmarks.Library.Predicates;

using Xunit;

namespace Eggnog.Tests.Benchmarks
{

    /// <summary>
    /// <see cref="PredicateCorpus"/> and <see cref="CorpusQuery"/>, the query-latency benchmarks' corpus over their copy
    /// of the predicate fixture and its queries: the corpus passes its own checks, each value of
    /// <see cref="PredicateQuery"/> asks the query it names, and a query whose answer is not the one it expects fails
    /// its ask, so that a broken engine fails a benchmark instead of producing a number.
    /// </summary>
    public sealed class PredicateCorpusTests
    {

        /// <summary>
        /// The corpus builds: every value of <see cref="PredicateQuery"/> asks the query it names, every answer is the
        /// one the corpus expects, and no query left its scope alive.
        /// </summary>
        [Fact]
        public void TheCorpusPassesItsChecks()
        {
            var corpus = new PredicateCorpus();

            Assert.Equal((int)PredicateQuery.Corpus, corpus.Queries.Count);
            Assert.Equal(corpus.Queries.Count, corpus.Queries.Select(query => query.Name).Distinct().Count());
            Assert.Equal(ScopedQueryCases.All[0].Name, corpus.Queries[(int)PredicateQuery.DeclaredFormLowers].Name);
            Assert.Equal(corpus.Queries[(int)PredicateQuery.KindByKey].Name + " with the case rules", corpus.Queries[(int)PredicateQuery.KindByKeyWithCases].Name);
        }

        /// <summary>
        /// A discriminated-schema query whose extraction renders as another term, or costs another amount, fails its
        /// ask, and leaves no scope alive; asked with its own extraction and cost, it passes.
        /// </summary>
        [Fact]
        public void AnotherExtractionFailsTheAsk()
        {
            var corpus = new PredicateCorpus();
            var c = corpus.Theory;
            var term = DiscriminatedSchema.Queries(c)[0];
            var extraction = $"(And (StrEq \"kind\" \"b\") (StrEq \"body.key\" \"{DiscriminatedSchema.Key}\"))";

            CorpusQuery.ForDiscriminated(c, corpus.Discriminated, term, false, extraction, 7).Ask();
            var otherTerm = Assert.Throws<InvalidOperationException>(() => CorpusQuery.ForDiscriminated(c, corpus.Discriminated, term, false, "(PTrue)", 7).Ask());
            Assert.Contains(" extracted ", otherTerm.Message, StringComparison.Ordinal);
            var otherCost = Assert.Throws<InvalidOperationException>(() => CorpusQuery.ForDiscriminated(c, corpus.Discriminated, term, false, extraction, 8).Ask());
            Assert.Contains(" extracted ", otherCost.Message, StringComparison.Ordinal);
            corpus.CheckNoLiveScope();
        }

        /// <summary>
        /// A case whose result does not hold fails its ask, and leaves no scope alive; asked with its own check, it
        /// passes.
        /// </summary>
        [Fact]
        public void ACaseWhoseResultDoesNotHoldFailsTheAsk()
        {
            var corpus = new PredicateCorpus();
            var c = corpus.Theory;
            var queryCase = ScopedQueryCases.All[0];

            CorpusQuery.ForCase(c, corpus.Schemas[0], queryCase).Ask();
            var failed = Assert.Throws<InvalidOperationException>(() => CorpusQuery.ForCase(c, corpus.Schemas[0], queryCase with { Holds = (_, _, _, _) => false }).Ask());
            Assert.Contains("does not hold", failed.Message, StringComparison.Ordinal);
            corpus.CheckNoLiveScope();
        }

    }

}
