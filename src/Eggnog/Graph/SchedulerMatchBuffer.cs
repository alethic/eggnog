using System;

namespace Eggnog
{

    /// <summary>
    /// The matches a scheduler's query collects for one rule in one engine call: egglog's <c>CollectMatches</c>, which
    /// appends every match, equal ones included, since a match holds only the values the head uses and two body matches
    /// may agree on those. Thread-safe to fill; read once the call has returned.
    /// </summary>
    internal sealed class SchedulerMatchBuffer
    {

        readonly object sync = new();
        uint[] cells = Array.Empty<uint>();
        int length;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="width">The number of values in a match.</param>
        public SchedulerMatchBuffer(int width)
        {
            Width = width;
        }

        /// <summary>
        /// Gets the number of values in a match.
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// Gets the matches collected, one after another, in the order added.
        /// </summary>
        public ReadOnlySpan<uint> Cells => new(cells, 0, length);

        /// <summary>
        /// Appends a match. Thread-safe.
        /// </summary>
        /// <param name="match">The match; copied.</param>
        public void Add(ReadOnlySpan<uint> match)
        {
            lock (sync)
            {
                if (length + match.Length > cells.Length)
                    Array.Resize(ref cells, Math.Max(16, Math.Max(length + match.Length, cells.Length * 2)));

                match.CopyTo(cells.AsSpan(length));
                length += match.Length;
            }
        }

    }

}
