using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The writes one lift round stages into the parent: unions, rows and terms, applied in that order
    /// (<see cref="ApplyTo"/>). Reused across rounds; cells live in one growing array, so a warm round allocates
    /// nothing but what the parent's staging does.
    /// </summary>
    internal sealed class LiftWrites
    {

        readonly List<(uint A, uint B)> unions = new();
        readonly List<(FunctionId Function, int Start, int Length)> rows = new();
        readonly List<(FunctionId Function, int Start, int Length)> terms = new();
        uint[] cells = new uint[64];
        int used;

        /// <summary>
        /// Gets whether nothing is staged.
        /// </summary>
        public bool IsEmpty => unions.Count == 0 && rows.Count == 0 && terms.Count == 0;

        /// <summary>
        /// Gets the number of unions staged.
        /// </summary>
        public int UnionCount => unions.Count;

        /// <summary>
        /// Gets the number of rows staged.
        /// </summary>
        public int RowCount => rows.Count;

        /// <summary>
        /// Gets the number of terms staged.
        /// </summary>
        public int TermCount => terms.Count;

        /// <summary>
        /// Drops everything staged.
        /// </summary>
        public void Clear()
        {
            unions.Clear();
            rows.Clear();
            terms.Clear();
            used = 0;
        }

        /// <summary>
        /// Stages the union of two parent ids.
        /// </summary>
        /// <param name="a">One id.</param>
        /// <param name="b">The other.</param>
        public void AddUnion(uint a, uint b) => unions.Add((a, b));

        /// <summary>
        /// Stages a row of <paramref name="function"/>: its keys, then its output.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="keys">The keys.</param>
        /// <param name="output">The output.</param>
        public void AddRow(FunctionId function, ReadOnlySpan<uint> keys, uint output)
        {
            var start = Reserve(keys.Length + 1);
            keys.CopyTo(cells.AsSpan(start));
            cells[start + keys.Length] = output;
            rows.Add((function, start, keys.Length + 1));
        }

        /// <summary>
        /// Stages a term of <paramref name="function"/> over <paramref name="keys"/>, with a fresh parent id.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="keys">The keys.</param>
        public void AddTerm(FunctionId function, ReadOnlySpan<uint> keys)
        {
            var start = Reserve(keys.Length);
            keys.CopyTo(cells.AsSpan(start));
            terms.Add((function, start, keys.Length));
        }

        /// <summary>
        /// Stages everything into <paramref name="parent"/>'s host writes: unions, then rows (not subsumed), then terms,
        /// for its next flush.
        /// </summary>
        /// <param name="parent">The cases' parent.</param>
        public void ApplyTo(EGraphContext parent)
        {
            foreach (var (a, b) in unions)
                parent.StageUnion(new Value(a), new Value(b));

            foreach (var (function, start, length) in rows)
                parent.StageRow(function, Values(start, length));

            foreach (var (function, start, length) in terms)
                parent.StageTerm(function, Values(start, length));
        }

        /// <summary>
        /// Gets staged cells as values.
        /// </summary>
        ReadOnlySpan<Value> Values(int start, int length) => System.Runtime.InteropServices.MemoryMarshal.Cast<uint, Value>(cells.AsSpan(start, length));

        /// <summary>
        /// Reserves <paramref name="length"/> cells and returns where they start.
        /// </summary>
        int Reserve(int length)
        {
            if (used + length > cells.Length)
                Array.Resize(ref cells, Math.Max(cells.Length * 2, used + length));

            var start = used;
            used += length;
            return start;
        }

    }

}
