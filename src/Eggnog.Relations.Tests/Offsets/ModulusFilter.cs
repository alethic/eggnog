namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An allocation-free <see cref="IOffsetFilter"/> that keeps the row ids divisible by a modulus.
    /// </summary>
    /// <param name="modulus">The modulus.</param>
    readonly struct ModulusFilter(int modulus) : IOffsetFilter
    {

        /// <inheritdoc />
        public bool Keep(int row) => row % modulus == 0;

    }

}
