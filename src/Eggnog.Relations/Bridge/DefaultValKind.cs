namespace Eggnog.Relations
{

    /// <summary>
    /// What a lookup of a missing key in a function does: the cases of egglog-bridge's <c>DefaultVal</c>
    /// (egglog-bridge/src/lib.rs:1000-1008). See <see cref="DefaultVal"/>.
    /// </summary>
    public enum DefaultValKind : byte
    {

        /// <summary>
        /// Insert a row whose output is a fresh e-class id from the id counter (constructors).
        /// </summary>
        FreshId,

        /// <summary>
        /// Cause an egglog-level panic (custom functions).
        /// </summary>
        Fail,

        /// <summary>
        /// Insert a row whose output is a constant.
        /// </summary>
        Const,

    }

}
