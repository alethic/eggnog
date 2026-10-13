using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A deliberately naive model of egglog's <c>Subset</c>: a kind flag plus either a range or a list. Each operation
    /// restates the Rust semantics of core-relations/src/offsets/mod.rs in the plainest terms, so property tests can
    /// compare <see cref="Subset"/> against it, kind included.
    /// </summary>
    sealed class SubsetModel
    {

        SubsetModel(bool isDense, int start, int end, List<int> rows)
        {
            IsDense = isDense;
            Start = start;
            End = end;
            Rows = rows;
        }

        /// <summary>
        /// Gets whether the modeled subset is dense.
        /// </summary>
        public bool IsDense { get; }

        /// <summary>
        /// Gets the first row of a dense subset.
        /// </summary>
        public int Start { get; }

        /// <summary>
        /// Gets one past the last row of a dense subset.
        /// </summary>
        public int End { get; }

        /// <summary>
        /// Gets the rows of a sparse subset.
        /// </summary>
        public List<int> Rows { get; }

        /// <summary>
        /// Gets the row ids in order, whatever the kind.
        /// </summary>
        public List<int> Contents => IsDense ? Enumerable.Range(Start, End - Start).ToList() : Rows;

        /// <summary>
        /// Models <c>Subset::empty</c>.
        /// </summary>
        public static SubsetModel Empty => Dense(0, 0);

        /// <summary>
        /// Models <c>Subset::Dense</c>.
        /// </summary>
        public static SubsetModel Dense(int start, int end) => new(true, start, end, new List<int>());

        /// <summary>
        /// Models <c>Subset::Sparse</c>.
        /// </summary>
        public static SubsetModel Sparse(IEnumerable<int> rows) => new(false, 0, 0, rows.ToList());

        /// <summary>
        /// Creates a random model: a dense range, or a sparse list that may be contiguous or hold duplicates, with
        /// lengths spread so that every intersection strategy is reached.
        /// </summary>
        public static SubsetModel Random(Random random)
        {
            var size = random.Next(4) switch
            {
                0 => random.Next(0, 6),
                1 => random.Next(0, 20),
                2 => random.Next(0, 150),
                _ => random.Next(0, 1500),
            };

            switch (random.Next(5))
            {
                case 0:
                    var start = random.Next(0, 400);
                    return Dense(start, start + size);
                case 1:
                    var first = random.Next(0, 400);
                    return Sparse(Enumerable.Range(first, size));
                case 2:
                    return Sparse(OffsetTestUtil.RandomSorted(random, size, Math.Max(1, size * random.Next(1, 4)), duplicates: true));
                default:
                    return Sparse(OffsetTestUtil.RandomSorted(random, size, random.Next(1, 3000), duplicates: false));
            }
        }

        /// <summary>
        /// Builds a real subset matching this model; sparse ones randomly start at position zero of their buffer or
        /// further in.
        /// </summary>
        public Subset Realize(Random random)
        {
            if (IsDense)
                return Subset.Dense(Start, End);

            return random.Next(2) == 0 ? OffsetTestUtil.Sparse(Rows) : OffsetTestUtil.OffsetSparse(Rows);
        }

        /// <summary>
        /// Builds a borrowed subset matching this model over a fresh array.
        /// </summary>
        public SubsetRef RealizeRef(Random random)
        {
            if (IsDense)
                return SubsetRef.Dense(new OffsetRange(Start, End));

            var pad = random.Next(0, 3);
            var array = new int[pad + Rows.Count + pad];
            Rows.CopyTo(array, pad);
            return SubsetRef.Sparse(array, pad, pad + Rows.Count);
        }

        /// <summary>
        /// Models <c>Subset::intersect</c>.
        /// </summary>
        public SubsetModel Intersect(SubsetModel other)
        {
            if (IsDense && other.IsDense)
            {
                var l = Math.Max(Start, other.Start);
                var r = Math.Min(End, other.End);
                return l >= r ? Dense(l, l) : Dense(l, r);
            }

            if (IsDense)
            {
                var clipped = OffsetTestUtil.Clip(other.Rows, Start, End);
                return clipped.Count == 0 ? Empty : Sparse(clipped);
            }

            if (other.IsDense)
                return Sparse(OffsetTestUtil.Clip(Rows, other.Start, other.End));

            return Sparse(OffsetTestUtil.MultisetIntersect(Rows, other.Rows));
        }

        /// <summary>
        /// Models <c>Subset::add_row_sorted</c>, or returns <see langword="null"/> where egglog panics.
        /// </summary>
        public SubsetModel? AddRowSorted(int row)
        {
            if (IsDense)
            {
                if (End == Start)
                    return Dense(row, row + 1);

                if (End == row)
                    return Dense(Start, row + 1);

                if (row < End - 1)
                    return null;

                return Sparse(Contents.Append(row));
            }

            if (Rows.Count > 0 && Rows[^1] > row)
                return null;

            return Sparse(Rows.Append(row));
        }

        /// <summary>
        /// Models <c>Subset::retain</c>.
        /// </summary>
        public SubsetModel Retain(Func<int, bool> keep)
        {
            if (IsDense == false)
                return Sparse(Rows.Where(keep));

            var result = Empty;
            foreach (var row in Contents.Where(keep))
                result = result.AddRowSorted(row)!;

            return result;
        }

        /// <summary>
        /// Asserts that <paramref name="actual"/> has this model's kind, rows and, when dense, exact endpoints.
        /// </summary>
        public void AssertMatches(Subset actual, string context)
        {
            Assert.True(IsDense == actual.IsDense, $"{context}: expected {(IsDense ? "dense" : "sparse")}, got {actual}");
            if (IsDense)
            {
                Assert.True(actual.TryGetDense(out var range), context);
                Assert.True(Start == range.Start && End == range.End, $"{context}: expected [{Start}..{End}), got {range}");
            }

            var contents = OffsetTestUtil.Collect(actual);
            Assert.True(Contents.SequenceEqual(contents), $"{context}: expected [{string.Join(", ", Contents)}], got {actual}");
        }

        /// <inheritdoc />
        public override string ToString() => IsDense ? $"Dense[{Start}..{End})" : $"Sparse[{string.Join(", ", Rows)}]";

    }

}
