namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The key one delete of a <see cref="ColorRunner"/> removes, on each side: the same row, named by each side's ids.
    /// </summary>
    /// <param name="Main">The key on the engine.</param>
    /// <param name="Unseeded">The key on the unseeded engine, if it runs.</param>
    /// <param name="Reference">The key in the reference, if it runs.</param>
    readonly record struct ColorKeys(uint[] Main, uint[] Unseeded, ulong[] Reference);

}
