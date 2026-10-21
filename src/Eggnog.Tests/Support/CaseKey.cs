using System;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A key whose equality ignores case but whose order does not: a type whose comparer disagrees with its equality,
    /// which a map or a multiset of it cannot store faithfully.
    /// </summary>
    /// <param name="Text">The text.</param>
    public readonly record struct CaseKey(string Text) : IComparable<CaseKey>
    {

        /// <inheritdoc />
        public bool Equals(CaseKey other) => string.Equals(Text, other.Text, StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Text);

        /// <inheritdoc />
        public int CompareTo(CaseKey other) => string.CompareOrdinal(Text, other.Text);

    }

}
