namespace Eggnog.Relations
{

    /// <summary>
    /// The kind of an <see cref="Instr"/>: the variants of egglog's <c>Instr</c> enum
    /// (core-relations/src/action/mod.rs:885-989). The interpreter switches on it and reinterprets the instruction with
    /// <c>Unsafe.As</c> instead of making a virtual call.
    /// </summary>
    internal enum InstrKind : byte
    {

        /// <summary>
        /// Look a key up, inserting a row of defaults when it is missing: <see cref="LookupOrInsertDefaultInstr"/>.
        /// </summary>
        LookupOrInsertDefault,

        /// <summary>
        /// Look a key up, using a default when it is missing: <see cref="LookupWithDefaultInstr"/>.
        /// </summary>
        LookupWithDefault,

        /// <summary>
        /// Look a key up, halting the lane when it is missing: <see cref="LookupInstr"/>.
        /// </summary>
        Lookup,

        /// <summary>
        /// Look a key up, calling an external function when it is missing: <see cref="LookupWithFallbackInstr"/>.
        /// </summary>
        LookupWithFallback,

        /// <summary>
        /// Stage a row: <see cref="InsertInstr"/>.
        /// </summary>
        Insert,

        /// <summary>
        /// Stage a row when two operands are equal: <see cref="InsertIfEqInstr"/>.
        /// </summary>
        InsertIfEq,

        /// <summary>
        /// Stage a removal: <see cref="RemoveInstr"/>.
        /// </summary>
        Remove,

        /// <summary>
        /// Call an external function, halting the lane when it fails: <see cref="ExternalInstr"/>.
        /// </summary>
        External,

        /// <summary>
        /// Call an external function, calling a second one when the first fails: <see cref="ExternalWithFallbackInstr"/>.
        /// </summary>
        ExternalWithFallback,

        /// <summary>
        /// Keep the lanes where two operands are equal: <see cref="AssertEqInstr"/>.
        /// </summary>
        AssertEq,

        /// <summary>
        /// Keep the lanes where two operands differ: <see cref="AssertNeInstr"/>.
        /// </summary>
        AssertNe,

        /// <summary>
        /// Keep the lanes where two operand lists differ somewhere: <see cref="AssertAnyNeInstr"/>.
        /// </summary>
        AssertAnyNe,

        /// <summary>
        /// Broadcast a counter's value: <see cref="ReadCounterInstr"/>.
        /// </summary>
        ReadCounter,

    }

}
