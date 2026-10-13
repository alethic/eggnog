namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A value type that implements no comparison, for the value tests.
    /// </summary>
    /// <param name="X">The payload.</param>
    public readonly record struct Opaque(int X);

}
