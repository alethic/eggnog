using System;

namespace Eggnog.Tests.Predicates.Scoped
{

    /// <summary>
    /// One query of <see cref="PredicateTheory"/> that runs without a split, as data: how its schema is declared, the
    /// predicate it asks, and what must hold in the query scope once it saturates with horn, lattice and rewrite rules.
    /// <see cref="ScopedQueryCases"/> lists the cases, for the tests that run every query in another setting (two
    /// graphs, parallel runs).
    /// </summary>
    /// <param name="Name">What the case shows, for messages.</param>
    /// <param name="Declare">Declares the schema's facts and branch rules in a writable schema scope.</param>
    /// <param name="Query">The query's predicate.</param>
    /// <param name="Holds">Whether the query's result holds: given the theory, the frozen schema, the saturated query
    /// scope and the predicate's class.</param>
    public sealed record ScopedQueryCase(
        string Name,
        Action<PredicateTheory, Scope> Declare,
        Func<PredicateTheory, ConstructorTerm<Pred>> Query,
        Func<PredicateTheory, Scope, Scope, Pred, bool> Holds);

}
