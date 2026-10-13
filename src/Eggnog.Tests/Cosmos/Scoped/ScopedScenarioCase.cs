using System;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// One query of the scoped Cosmos suite that runs without a split, as data: how its container is declared, the
    /// WHERE clause it asks, and what must hold in the query scope once it saturates with horn, lattice and rewrite
    /// rules. The scenario test classes hold the full assertions; <see cref="ScopedScenarioCases"/> lists the cases for
    /// the tests that run every scenario in another setting (two graphs, parallel runs).
    /// </summary>
    /// <param name="Name">The scenario and test it comes from, for messages.</param>
    /// <param name="Declare">Declares the container's facts and branch rules in a writable schema scope.</param>
    /// <param name="Query">The WHERE clause.</param>
    /// <param name="Holds">Whether the query's result holds: given the theory, the frozen schema, the saturated query
    /// scope and the clause's class.</param>
    public sealed record ScopedScenarioCase(
        string Name,
        Action<CosmosTheory, Scope> Declare,
        Func<CosmosTheory, ConstructorTerm<Pred>> Query,
        Func<CosmosTheory, Scope, Scope, Pred, bool> Holds);

}
