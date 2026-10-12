namespace Eggnog.Tests.Review
{

    /// <summary>
    /// A non-comparable value type for the review probes.
    /// </summary>
    /// <param name="X">The payload.</param>
    public readonly record struct Opaque(int X);

}
