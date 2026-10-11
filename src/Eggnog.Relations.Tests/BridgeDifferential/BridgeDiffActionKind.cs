namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a right-hand-side action of a bridge differential rule does.
    /// </summary>
    enum BridgeDiffActionKind
    {

        /// <summary>
        /// Looks a function up and binds its output (<see cref="EGraphRuleBuilder.Lookup"/>): a constructor or a relation
        /// mints a fresh id for a missing key, which is how rules build terms and insert relation rows; a function without
        /// a default panics on a missing key.
        /// </summary>
        Lookup,

        /// <summary>
        /// Writes a row, merged with the function's merge (<see cref="EGraphRuleBuilder.Set"/>); a relation's row written
        /// without an output (<see cref="BridgeDiffFunction.IsNoOutput"/>), egglog's <c>(R a b)</c> action, is a lookup
        /// that inserts the key.
        /// </summary>
        Set,

        /// <summary>
        /// Unions two ids (<see cref="EGraphRuleBuilder.Union"/>).
        /// </summary>
        Union,

        /// <summary>
        /// Subsumes a row, inserting it subsumed if missing (<see cref="EGraphRuleBuilder.Subsume"/>).
        /// </summary>
        Subsume,

        /// <summary>
        /// Calls an integer primitive and binds its result (<see cref="EGraphRuleBuilder.CallExternalFunc"/>).
        /// </summary>
        Call,

    }

}
