namespace Eggnog.Relations
{

    /// <summary>
    /// The kind of a <see cref="WriteVal"/>: the variants of egglog's <c>WriteVal</c> (core-relations/src/action/mod.rs:58-67).
    /// </summary>
    internal enum WriteValKind : byte
    {

        /// <summary>
        /// A variable or a constant (<see cref="WriteVal.Entry"/>).
        /// </summary>
        Entry,

        /// <summary>
        /// A fresh value: the previous value of <see cref="WriteVal.Counter"/>, which is incremented.
        /// </summary>
        IncrementCounter,

        /// <summary>
        /// A copy of an earlier cell of the row being built (<see cref="WriteVal.Column"/>).
        /// </summary>
        CurrentValue,

    }

}
