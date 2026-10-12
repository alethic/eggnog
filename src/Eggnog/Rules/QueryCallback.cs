namespace Eggnog
{

    /// <summary>
    /// Receives one match of <see cref="Scope.ForEachMatch(Query, QueryCallback)"/>.
    /// </summary>
    /// <param name="match">The match; valid only during the call.</param>
    /// <returns><c>false</c> to stop receiving matches.</returns>
    public delegate bool QueryCallback(QueryMatch match);

}
