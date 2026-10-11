namespace Eggnog.Relations
{

    /// <summary>
    /// How a <see cref="FusedIntersectMatStage"/> reads its materialization: egglog's <c>MatScanMode</c>
    /// (core-relations/src/free_join/plan.rs:85-91).
    /// </summary>
    /// <remarks>
    /// A materialization maps a key (the values of the block's message variables) to rows of value-variable values.
    /// <see cref="Full"/>, <see cref="Value"/> and <see cref="Lookup"/> read variables bound by earlier stages, so they
    /// are reorder barriers; <see cref="KeyOnly"/> binds its key variables itself and commutes.
    /// </remarks>
    internal enum MatScanMode : byte
    {

        /// <summary>
        /// Enumerate every key and every value row (the first stage of a result block).
        /// </summary>
        Full,

        /// <summary>
        /// Enumerate the keys only, binding the message variables and probing the bag's atoms (a bag's prologue).
        /// </summary>
        KeyOnly,

        /// <summary>
        /// Look up the key built from the mode variables (bound earlier) and enumerate its value rows (result block).
        /// </summary>
        Value,

        /// <summary>
        /// Check that the key built from the mode variables (bound earlier) is present; binds nothing (an epilogue, or
        /// the result-block check that keeps a block's join constraint).
        /// </summary>
        Lookup,

    }

}
