namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A user-defined composite base value, interned through <see cref="InternedCodec{T}"/>.
    /// </summary>
    /// <param name="X">The first coordinate.</param>
    /// <param name="Y">The second coordinate.</param>
    public readonly record struct BaseValueTestPoint(int X, int Y);

}
