namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A pooled object that records who holds it, so concurrent tests can catch an object handed to two owners.
    /// </summary>
    sealed class PoolProbe
    {

        /// <summary>
        /// One while some caller owns the object, zero while it sits in a pool.
        /// </summary>
        public int Owned;

        /// <summary>
        /// Set by the policy's clear; the renter checks it.
        /// </summary>
        public bool Cleared = true;

        /// <summary>
        /// Arbitrary payload the owner scribbles on.
        /// </summary>
        public int Payload;

    }

}
