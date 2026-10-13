using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Concurrency
{

    /// <summary>
    /// Reads, queries and extractors on a frozen scope take no guard (§6.6.3, §6.10, E5, D47): while an extraction is held
    /// inside its cost function, or a query inside its filter, on another thread, the same frozen scope serves every other
    /// read, query and extraction on this thread. Deterministic, unlike the parallel loops, which overlap only by chance.
    /// </summary>
    public sealed class FrozenScopeOverlapTests
    {

        [Fact]
        public async Task AFrozenScopeServesReadsQueriesAndExtractorsWhileAnExtractionIsInProgress()
        {
            var t = new NodeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var blocking = CostModel.Default.With(t.Lit, node =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return 1;
            });
            var lits = t.Graph.DefineQuery("lits", q => q.When(t.Lit.Of(q.Var<long>("n")).Is(q.Var<Node>("x"))));
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            var wrapped = schema.Make(t.Wrap.Of(t.Lit.Of(7L)));
            var other = schema.Make(t.Wrap.Of(t.Lit.Of(8L)));
            schema.Freeze();
            var held = schema.CreateExtractor(blocking);

            var extraction = Task.Factory.StartNew(() => held.Extract(wrapped).ToString(), CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            try
            {
                Assert.Equal("(Wrap (Lit 8))", schema.CreateExtractor().Extract(other).ToString());
                Assert.True(schema.AreEqual(wrapped, t.Wrap.Of(t.Lit.Of(7L))));
                Assert.Equal(2, schema.Count(lits));
                Assert.Equal(schema.Find(other), schema.Find(other));
            }
            finally
            {
                release.Set();
            }

            Assert.Equal("(Wrap (Lit 7))", await extraction);
        }

        [Fact]
        public async Task AFrozenScopeServesReadsQueriesAndExtractorsWhileAQueryIsInProgress()
        {
            var t = new NodeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var blocking = t.Graph.DefineQuery("blocking", q =>
            {
                var n = q.Var<long>("n");
                q.When(t.Lit.Of(n).Is(q.Var<Node>("x")));
                q.Where(n, _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return true;
                });
            });
            var lits = t.Graph.DefineQuery("lits", q => q.When(t.Lit.Of(q.Var<long>("n")).Is(q.Var<Node>("x"))));
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            var wrapped = schema.Make(t.Wrap.Of(t.Lit.Of(7L)));
            schema.Freeze();

            var count = Task.Factory.StartNew(() => schema.Count(blocking), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            try
            {
                Assert.Equal(1, schema.Count(lits));
                Assert.True(schema.AreEqual(wrapped, t.Wrap.Of(t.Lit.Of(7L))));
                Assert.Equal(1, schema.CountRows(t.Lit));
                Assert.Equal("(Wrap (Lit 7))", schema.CreateExtractor().Extract(wrapped).ToString());
            }
            finally
            {
                release.Set();
            }

            Assert.Equal(1, await count);
        }

    }

}
