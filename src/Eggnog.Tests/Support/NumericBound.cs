namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A numeric bound, registered with <see cref="ValueStore.Register{T}"/> by the tests that use it: the shape of a
    /// consumer's lower or upper bound on a number, exclusive when <paramref name="Open"/>. Equality is the record's, so
    /// bounds whose values differ only in scale (<c>1.0m</c> and <c>1.00m</c>) are equal.
    /// </summary>
    /// <param name="Value">The bound's value.</param>
    /// <param name="Open">Whether the bound excludes <paramref name="Value"/>.</param>
    public readonly record struct NumericBound(decimal Value, bool Open);

}
