using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests that the copy-on-write <see cref="ExternalFunctionRegistry"/> answers lookups lock-free and correctly while
    /// functions are added and freed (colored-contexts spec §4.3, X16): every context of a store shares one registry.
    /// </summary>
    public class ExternalFunctionRegistryConcurrencyTests
    {

        static ExternalFunction Constant(uint value) => ExternalFunction.Create((ExecutionState _, ReadOnlySpan<uint> _, out uint result) =>
        {
            result = value;
            return true;
        });

        [Fact]
        public void StableFunctionsStayVisibleWhileOthersAreAddedAndFreed()
        {
            var registry = new ExternalFunctionRegistry();
            var stable = new Dictionary<ExternalFunctionId, ExternalFunction>();
            for (var i = 0; i < 16; i++)
            {
                var f = Constant((uint)i);
                stable.Add(registry.Add(f), f);
            }

            var stableIds = stable.Keys.ToArray();
            var stop = 0;
            using var start = new ManualResetEventSlim();
            var readers = ConcurrencyTestThread.StartMany(8, r =>
            {
                var rng = new Random(r);
                start.Wait();
                while (Volatile.Read(ref stop) == 0)
                {
                    var id = stableIds[rng.Next(stableIds.Length)];
                    Assert.Same(stable[id], registry[id]);
                    Assert.True(registry.TryGet(id, out var found));
                    Assert.Same(stable[id], found);
                    Assert.True(registry.Count >= stableIds.Length);
                }
            });

            start.Set();
            try
            {
                // A writer churns ids above the stable ones: freed ids are reused, the stable ones never move.
                var churn = new List<ExternalFunctionId>();
                var rng = new Random(99);
                for (var i = 0; i < 20_000; i++)
                {
                    if (churn.Count > 0 && rng.Next(2) == 0)
                    {
                        var at = rng.Next(churn.Count);
                        Assert.True(registry.Remove(churn[at], out _));
                        churn.RemoveAt(at);
                    }
                    else
                    {
                        var id = registry.Add(Constant(1000));
                        Assert.DoesNotContain(id, stableIds);
                        churn.Add(id);
                    }
                }

                Assert.Equal(stableIds.Length + churn.Count, registry.Count);
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                ConcurrencyTestThread.JoinAll(readers);
            }
        }

        [Fact]
        public void ConcurrentRegistrationsGetDistinctIds()
        {
            var registry = new ExternalFunctionRegistry();
            var ids = new ExternalFunctionId[8][];
            using var start = new ManualResetEventSlim();
            var writers = ConcurrencyTestThread.StartMany(8, t =>
            {
                var mine = new ExternalFunctionId[500];
                start.Wait();
                for (var i = 0; i < mine.Length; i++)
                    mine[i] = registry.Add(Constant((uint)(t * 1000 + i)));

                ids[t] = mine;
            });

            start.Set();
            ConcurrencyTestThread.JoinAll(writers);
            var all = ids.SelectMany(x => x).ToArray();
            Assert.Equal(all.Length, all.Distinct().Count());
            Assert.Equal(4000, registry.Count);
            for (var t = 0; t < 8; t++)
            {
                for (var i = 0; i < 500; i++)
                {
                    Assert.True(registry[ids[t][i]].TryInvoke(null!, ReadOnlySpan<uint>.Empty, out var value));
                    Assert.Equal((uint)(t * 1000 + i), value);
                }
            }
        }

    }

}
