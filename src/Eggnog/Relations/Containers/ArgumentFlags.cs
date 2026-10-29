using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One flag per argument of a function value, as many as it has arguments: the first 64 in a word, held inline, and
    /// the rest in further words, held only when one of them is set, so that equal flags are equal whatever made them.
    /// Immutable.
    /// </summary>
    internal readonly struct ArgumentFlags : IEquatable<ArgumentFlags>
    {

        readonly ulong low;
        readonly ulong[]? high;

        /// <summary>
        /// Initializes a new instance of the flags of the first 64 arguments.
        /// </summary>
        /// <param name="low">The flags, one bit per argument.</param>
        public ArgumentFlags(ulong low)
        {
            this.low = low;
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="words">The flags, 64 arguments a word, the first argument in the first word's lowest bit.</param>
        public ArgumentFlags(ReadOnlySpan<ulong> words)
        {
            if (words.IsEmpty)
                return;

            low = words[0];
            var last = words.Length - 1;
            while (last > 0 && words[last] == 0)
                last--;

            if (last > 0)
                high = words[1..(last + 1)].ToArray();
        }

        /// <summary>
        /// Gets the number of words that hold the flags: one, and one more for each further 64 arguments up to the last set.
        /// </summary>
        public int WordCount => 1 + (high?.Length ?? 0);

        /// <summary>
        /// Gets whether no flag is set.
        /// </summary>
        public bool IsEmpty => low == 0 && high is null;

        /// <summary>
        /// Gets whether the flag of an argument is set.
        /// </summary>
        /// <param name="index">The argument's index.</param>
        public bool this[int index] => (Word(index >> 6) & (1UL << index)) != 0;

        /// <summary>
        /// Gets a word of the flags, zero past the last.
        /// </summary>
        /// <param name="index">The word's index.</param>
        /// <returns>The word.</returns>
        public ulong Word(int index) => index == 0 ? low : high is not null && index <= high.Length ? high[index - 1] : 0;

        /// <summary>
        /// Gets the number of words that hold the flags of <paramref name="count"/> arguments.
        /// </summary>
        /// <param name="count">The number of arguments.</param>
        /// <returns>The number of words.</returns>
        public static int WordsFor(int count) => (count + 63) >> 6;

        /// <summary>
        /// Writes the flags into <paramref name="destination"/>, zero past the last.
        /// </summary>
        /// <param name="destination">The words.</param>
        public void CopyTo(Span<ulong> destination)
        {
            for (var i = 0; i < destination.Length; i++)
                destination[i] = Word(i);
        }

        /// <summary>
        /// Mixes the flags into a hash, one word per word they hold.
        /// </summary>
        /// <param name="hasher">The hasher.</param>
        public void HashInto(ref FxHasher hasher)
        {
            hasher.Write(low);
            if (high is not null)
                foreach (var word in high)
                    hasher.Write(word);
        }

        /// <inheritdoc />
        public bool Equals(ArgumentFlags other) =>
            low == other.low && (ReferenceEquals(high, other.high) || (high is not null && other.high is not null && high.AsSpan().SequenceEqual(other.high)));

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ArgumentFlags other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hasher = new FxHasher();
            HashInto(ref hasher);
            return (int)hasher.Finish();
        }

        /// <summary>
        /// Gets whether two sets of flags are equal.
        /// </summary>
        public static bool operator ==(ArgumentFlags left, ArgumentFlags right) => left.Equals(right);

        /// <summary>
        /// Gets whether two sets of flags differ.
        /// </summary>
        public static bool operator !=(ArgumentFlags left, ArgumentFlags right) => left.Equals(right) == false;

    }

}
