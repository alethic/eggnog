using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for <see cref="ConcurrentVec{T}"/> that expose defects in the port.
    /// </summary>
    public sealed class ConcurrentVecReviewTests
    {

        [Fact]
        public void ReentrantPushFromResizeFactoryIsNotSilentlyLost()
        {
            // egglog guards ConcurrentVec writes with a std::sync::Mutex (concurrent_vec.rs:27,63), which is not
            // reentrant: a push from inside resize_with's closure deadlocks or panics. C#'s Monitor is reentrant, so
            // the nested Push runs, publishes index 0, and then the outer loop overwrites slot 0 with the factory's
            // value: Push returned an index whose element is not the pushed item. Either reject the reentrant call
            // (InvalidOperationException, the convention for misuse) or keep the pushed element.
            var v = new ConcurrentVec<int>(0);
            var pushedIndex = -1;
            Exception? failure = null;
            try
            {
                v.ResizeWith(3, () =>
                {
                    if (pushedIndex < 0)
                        pushedIndex = v.Push(42);

                    return 7;
                });
            }
            catch (InvalidOperationException e)
            {
                failure = e;
            }

            if (failure is null)
                Assert.Equal(42, v[pushedIndex]);
        }

    }

}
