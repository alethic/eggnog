namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A user value type, registered with <see cref="ValueStore.Register{T}"/> by the tests that use it.
    /// </summary>
    /// <param name="X">The first coordinate.</param>
    /// <param name="Y">The second coordinate.</param>
    public readonly record struct Point(int X, int Y);

}
