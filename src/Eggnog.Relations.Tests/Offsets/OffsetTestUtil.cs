using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Builders, collectors and reference implementations shared by the offsets tests.
    /// </summary>
    static class OffsetTestUtil
    {

        /// <summary>
        /// Collects a subset's row ids through <see cref="SubsetRef.ForEach{TVisitor}(ref TVisitor)"/>, checking that
        /// the enumerator yields the same sequence and that the reported bounds and size agree with it (egglog's
        /// <c>collect</c> helper checks that a nonempty subset has bounds).
        /// </summary>
        public static List<int> Collect(SubsetRef subset)
        {
            var visitor = new CollectingVisitor();
            subset.ForEach(ref visitor);

            var enumerated = new List<int>();
            foreach (var row in subset)
                enumerated.Add(row);

            Assert.Equal(visitor.Rows, enumerated);
            Assert.Equal(visitor.Rows.Count, subset.Size);
            Assert.Equal(visitor.Rows.Count == 0, subset.IsEmpty);
            if (visitor.Rows.Count > 0)
            {
                Assert.True(subset.TryGetBounds(out var bounds), "nonempty subset should have bounds");
                Assert.Equal(visitor.Rows[0], bounds.Start);
                Assert.Equal(visitor.Rows[^1] + 1, bounds.End);
            }

            return visitor.Rows;
        }

        /// <summary>
        /// Collects an owned subset's row ids; see <see cref="Collect(SubsetRef)"/>.
        /// </summary>
        public static List<int> Collect(Subset subset)
        {
            var rows = Collect(subset.AsRef());
            var visitor = new CollectingVisitor();
            subset.ForEach(ref visitor);
            Assert.Equal(rows, visitor.Rows);
            return rows;
        }

        /// <summary>
        /// Builds a subset the way egglog's tests do: <see cref="Subset.Empty"/> plus
        /// <see cref="Subset.AddRowSorted"/> per row.
        /// </summary>
        public static Subset Build(IEnumerable<int> rows)
        {
            var subset = Subset.Empty;
            foreach (var row in rows)
                subset.AddRowSorted(row);

            return subset;
        }

        /// <summary>
        /// Builds a sparse subset holding exactly <paramref name="rows"/>, even when they are contiguous.
        /// </summary>
        public static Subset Sparse(IEnumerable<int> rows)
        {
            var array = rows.ToArray();
            var subset = SubsetRef.Sparse(array, 0, array.Length).ToOwned();
            Assert.False(subset.IsDense);
            return subset;
        }

        /// <summary>
        /// Builds a sparse subset whose run starts at a nonzero position of its buffer, by narrowing a larger sparse
        /// subset with a dense range.
        /// </summary>
        public static Subset OffsetSparse(IReadOnlyList<int> rows)
        {
            var padded = new List<int>();
            var low = rows.Count == 0 ? 1 : rows[0];
            if (low > 0)
                padded.Add(low - 1);

            padded.AddRange(rows);
            var subset = Sparse(padded);
            subset.Intersect(SubsetRef.Dense(new OffsetRange(low, int.MaxValue)));
            Assert.Equal(rows, Collect(subset));
            return subset;
        }

        /// <summary>
        /// Generates a sorted list of <paramref name="count"/> row ids below <paramref name="universe"/>; with
        /// <paramref name="duplicates"/> some row ids repeat.
        /// </summary>
        public static List<int> RandomSorted(Random random, int count, int universe, bool duplicates)
        {
            var rows = new List<int>(count);
            if (duplicates)
            {
                for (var i = 0; i < count; i++)
                    rows.Add(random.Next(universe));
            }
            else
            {
                var set = new HashSet<int>();
                count = Math.Min(count, universe);
                while (set.Count < count)
                    set.Add(random.Next(universe));

                rows.AddRange(set);
            }

            rows.Sort();
            return rows;
        }

        /// <summary>
        /// The multiset intersection of two sorted lists: each row id appears as often as in the list holding fewer
        /// copies of it. What egglog's sparse-sparse intersection computes.
        /// </summary>
        public static List<int> MultisetIntersect(IReadOnlyList<int> a, IReadOnlyList<int> b)
        {
            var counts = new Dictionary<int, int>();
            foreach (var x in b)
                counts[x] = counts.GetValueOrDefault(x) + 1;

            var result = new List<int>();
            foreach (var x in a)
            {
                if (counts.GetValueOrDefault(x) > 0)
                {
                    result.Add(x);
                    counts[x]--;
                }
            }

            return result;
        }

        /// <summary>
        /// The rows of <paramref name="rows"/> inside <c>[start, end)</c>, duplicates kept. What egglog's intersection
        /// with a dense side computes.
        /// </summary>
        public static List<int> Clip(IEnumerable<int> rows, int start, int end) => rows.Where(r => r >= start && r < end).ToList();

        /// <summary>
        /// Gets whether a sorted list is a nonempty run of consecutive row ids without repeats.
        /// </summary>
        public static bool IsContiguous(IReadOnlyList<int> rows)
        {
            for (var i = 1; i < rows.Count; i++)
                if (rows[i] != rows[i - 1] + 1)
                    return false;

            return rows.Count > 0;
        }

    }

}
