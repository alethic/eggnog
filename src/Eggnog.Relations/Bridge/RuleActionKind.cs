namespace Eggnog.Relations
{

    /// <summary>
    /// The cases of <see cref="RuleAction"/>: one per callback egglog-bridge's <c>RuleBuilder</c> records
    /// (egglog-bridge/src/rule.rs:164-716).
    /// </summary>
    internal enum RuleActionKind : byte
    {

        /// <summary>
        /// Bind the canonical id of an entry: egglog's <c>lookup_uf</c>, a union-find lookup that defaults to the entry.
        /// </summary>
        LookupUf,

        /// <summary>
        /// Continue only where two entry lists differ somewhere: egglog's <c>check_for_update</c>.
        /// </summary>
        AssertAnyNe,

        /// <summary>
        /// Call an external function and bind its result, panicking where it produces nothing: egglog's
        /// <c>call_external_func</c>.
        /// </summary>
        CallExternal,

        /// <summary>
        /// Call a primitive on the left-hand side: bind its result, or filter on it when the result entry is already
        /// bound; a lane where it produces nothing is dropped. egglog's <c>query_prim</c>.
        /// </summary>
        QueryPrim,

        /// <summary>
        /// Subsume the row of a key that the preceding lookup found or inserted: egglog's <c>subsume</c>, second half.
        /// </summary>
        Subsume,

        /// <summary>
        /// Look a key up, inserting a row with a fresh id or a constant output where it is missing: egglog's
        /// <c>lookup_with_subsumed</c> for <see cref="DefaultValKind.FreshId"/> and <see cref="DefaultValKind.Const"/>.
        /// </summary>
        LookupOrInsert,

        /// <summary>
        /// Look a key up, panicking where it is missing: egglog's <c>lookup_with_subsumed</c> for
        /// <see cref="DefaultValKind.Fail"/>.
        /// </summary>
        LookupWithFallback,

        /// <summary>
        /// Union two ids: egglog's <c>union</c>, an insertion into the union-find table.
        /// </summary>
        Union,

        /// <summary>
        /// Insert a row stamped with the run's timestamp: egglog's <c>set_with_subsume</c>.
        /// </summary>
        Insert,

        /// <summary>
        /// Remove the row of a key: egglog's <c>remove</c>.
        /// </summary>
        Remove,

        /// <summary>
        /// Call a panic function: egglog's <c>panic</c>.
        /// </summary>
        Panic,

    }

}
