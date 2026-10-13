using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// The release of a disposed frozen scope waits for every call in progress on it (D40), including calls that take no
    /// guard and make no transient child: an extraction on another thread.
    /// </summary>
    public sealed class DeferredReleaseTests
    {

        [Fact]
        public async Task AFrozenScopeDisposedDuringAnExtractionIsReleasedAfterTheExtraction()
        {
            var t = new NodeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var costs = CostModel.Default.With(t.Lit, node =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return 1;
            });
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            var wrapped = schema.Make(t.Wrap.Of(t.Lit.Of(7L)));
            schema.Freeze();
            var extractor = schema.CreateExtractor(costs);

            var extraction = Task.Factory.StartNew(() => extractor.Extract(wrapped).ToString(), CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            // The extraction is a call in progress on the scope: the release waits for it.
            schema.Dispose();
            Assert.True(schema.IsDisposed);
            Assert.False(schema.Engine.IsDisposed);
            Assert.Equal(1, t.Root.Engine.Context.Context.ChildCount);

            release.Set();
            Assert.Equal("(Wrap (Lit 7))", await extraction);
            Assert.True(schema.Engine.IsDisposed);
            Assert.Empty(t.Root.Children);
            Assert.Equal(0, t.Root.Engine.Context.Context.ChildCount);
        }

    }

}
