namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// What a step of a reference body does, after the join.
    /// </summary>
    internal enum RefStepKind
    {

        /// <summary>
        /// A computation: binds its target, or checks it when bound.
        /// </summary>
        Let,

        /// <summary>
        /// A boolean computation that must be true.
        /// </summary>
        Filter,

        /// <summary>
        /// Two slots that must be equal: binds one from the other, or checks them.
        /// </summary>
        Equal,

        /// <summary>
        /// Two slots that must differ.
        /// </summary>
        NotEqual,

    }

}
