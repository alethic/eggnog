using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for <see cref="ResettableOnceLock{T}"/> that expose defects in the port.
    /// </summary>
    public sealed class ResettableOnceLockReviewTests
    {

        [Fact]
        public void ReentrantGetOrUpdateDoesNotRunTheUpdateTwicePerEpoch()
        {
            // std::sync::Once (resettable_oncelock.rs:52-60) is not reentrant: calling get_or_update from inside the
            // update deadlocks, so the update can never run twice in one epoch. C#'s Monitor is reentrant, so the inner
            // call sees "not completed" and runs a second update inside the first, breaking "at most one update per
            // epoch". Either reject the reentrant call (InvalidOperationException) or run the update once.
            var lockObj = new ResettableOnceLock<int>(0);
            var runs = 0;
            Exception? failure = null;
            try
            {
                lockObj.GetOrUpdate(0, (ref int x, int _) =>
                {
                    runs++;
                    lockObj.GetOrUpdate(0, (ref int y, int _) => runs++);
                });
            }
            catch (InvalidOperationException e)
            {
                failure = e;
            }

            if (failure is null)
                Assert.Equal(1, runs);
        }

    }

}
