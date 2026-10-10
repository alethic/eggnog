using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for <see cref="SortedOffsetSlice"/> that pin behavior egglog's types guarantee and the port must
    /// enforce at run time.
    /// </summary>
    public class SortedOffsetSliceReviewTests
    {

        [Fact]
        public void ScanForOffsetNeverReachesOutsideTheSlice()
        {
            // egglog's scan_for_offset takes a usize cursor, so it can never look before the slice. The port takes an
            // int relative to Start and forwards Start + from to the kernel, which only rejects negative absolute
            // positions: a negative relative cursor silently searches the owner's array before the slice.
            var array = new[] { 1, 2, 10, 20, 30 };
            var slice = new SortedOffsetSlice(array, 2, 5);

            var threw = false;
            var found = false;
            var index = 0;
            try
            {
                found = slice.ScanForOffset(-2, 1, out index);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            Assert.True(threw || (found == false && index >= 0 && index <= slice.Length),
                $"row 1 is not in the slice {slice}, yet ScanForOffset(-2, 1) returned found={found}, index={index}");
        }

    }

}
