namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An intern-table key whose hash code is constant, so every key collides.
    /// </summary>
    /// <param name="Value">The value that distinguishes keys.</param>
    internal readonly record struct InternTestCollidingKey(int Value)
    {

        /// <inheritdoc />
        public override int GetHashCode() => 42;

    }

}
