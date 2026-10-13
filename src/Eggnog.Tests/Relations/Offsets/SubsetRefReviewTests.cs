using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for <see cref="SubsetRef"/> that pin behavior egglog's types guarantee and the port must enforce at
    /// run time in every build configuration.
    /// </summary>
    public class SubsetRefReviewTests
    {

        /// <summary>
        /// Runs <see cref="SubsetRef.IterBounded{TVisitor}"/> with the <c>-1</c> "None" sentinel fed back as the start
        /// position, tolerating an exception (debug assertion or argument check) but not a visit.
        /// </summary>
        static CollectingVisitor IterFromSentinel(SubsetRef subset, int to)
        {
            using var scope = new DebugAssertScope();
            var visitor = new CollectingVisitor();
            try
            {
                subset.IterBounded(-1, to, ref visitor);
            }
            catch (DebugAssertFailedException)
            {
            }
            catch (ArgumentOutOfRangeException)
            {
            }

            return visitor;
        }

        [Fact]
        public void IterBoundedDenseRejectsSentinelStartInEveryConfiguration()
        {
            // egglog returns Option<usize> and takes a usize start, so its None can never be passed back as a start.
            // The port returns -1 for None and guards the start only with Debug.Assert: in Release, passing -1 back
            // visits row Lo - 1, which is not in the subset.
            var subset = SubsetRef.Dense(new OffsetRange(10, 15));
            var visitor = IterFromSentinel(subset, 3);
            Assert.True(visitor.Rows.Contains(9) == false, $"visited [{string.Join(", ", visitor.Rows)}], but row 9 is not in {subset}");
        }

        [Fact]
        public void IterBoundedSparseRejectsSentinelStartInEveryConfiguration()
        {
            // The sparse arm checks only from > stop; in Release a negative start reads the array slot before the
            // subset's window, which in an index arena belongs to another key's subset.
            var arena = new[] { 100, 5, 7, 9 };
            var subset = SubsetRef.Sparse(arena, 1, 4);
            var visitor = IterFromSentinel(subset, 2);
            Assert.True(visitor.Rows.Contains(100) == false, $"visited [{string.Join(", ", visitor.Rows)}], but row 100 is not in {subset}");
        }

    }

}
