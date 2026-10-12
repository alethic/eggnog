namespace Eggnog
{

    /// <summary>
    /// A term a host batch will create: its constructor, its key cells, and the class id minted for it (or, for
    /// <c>C.Of(k).Is(x)</c> with a new key, the class <c>x</c>). Staged keys then id, children before parents.
    /// </summary>
    /// <param name="Head">The constructor.</param>
    /// <param name="Keys">The key cells.</param>
    /// <param name="Id">The class of the term.</param>
    internal sealed record PendingTerm(ConstructorHandle Head, uint[] Keys, uint Id);

}
