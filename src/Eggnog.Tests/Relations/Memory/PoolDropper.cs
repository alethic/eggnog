namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The object of egglog's pool tests (core-relations/src/pool/tests.rs:59-104): records whether it was cleared and
    /// whether it asks to be reused.
    /// </summary>
    sealed class PoolDropper
    {

        /// <summary>
        /// Gets or sets whether the object is in its cleared state.
        /// </summary>
        public bool Cleared { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the object asks to be pooled. Clearing resets it, as in egglog.
        /// </summary>
        public bool Reuse { get; set; }

    }

}
