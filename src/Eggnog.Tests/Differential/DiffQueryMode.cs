namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// How a random program asks a query.
    /// </summary>
    internal enum DiffQueryMode
    {

        /// <summary>
        /// <c>Scope.Any</c>.
        /// </summary>
        Any,

        /// <summary>
        /// <c>Scope.Count</c>.
        /// </summary>
        Count,

        /// <summary>
        /// <c>Scope.ForEachMatch</c>, every match.
        /// </summary>
        ForEach,

    }

}
