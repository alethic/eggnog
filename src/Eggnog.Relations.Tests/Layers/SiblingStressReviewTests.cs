using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review test for M9.3 (soundness lens): concurrent children of a frozen child (each its own thread domain) write,
    /// hide, retract and clear over shared frozen layers and hidden sets; every child stays a copy of its frozen parent
    /// plus its own batches, and the frozen chain is unchanged.
    /// </summary>
    public class SiblingStressReviewTests
    {

        [Fact]
        public void ConcurrentSiblingsStayCopies()
        {
            for (var trial = 0; trial < 6; trial++)
                Run(trial);
        }

        static void Run(int trial)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f1 = store.AddTable(MaxFunction(1), name: "f1");
            var f2 = store.AddTable(MaxFunction(2), name: "f2");
            var tables = new[] { f1, f2 };
            var root = store.Root;
            var ids = Enumerable.Range(0, 12).Select(_ => root.NewId()).ToArray();
            var random = new Random(trial);
            var baseline = tables.Select(_ => new Dictionary<string, uint>()).ToArray();
            Batch(random, root, baseline, tables, ids, false);
            Batch(random, root, baseline, tables, ids, false);
            root.Freeze();
            var middle = root.CreateChild();
            for (var i = 0; i < 4; i++)
                Batch(random, middle, baseline, tables, ids, true);

            middle.Freeze();
            var middleView = LookupPaths.Check(middle);

            const int Children = 8;
            var children = Enumerable.Range(0, Children).Select(_ => middle.CreateChild()).ToArray();
            var failures = new List<Exception>();
            using var start = new Barrier(Children);
            var tasks = children.Select((child, index) => Task.Run(() =>
            {
                try
                {
                    var local = new Random(trial * 100 + index);
                    var reference = baseline.Select(r => new Dictionary<string, uint>(r)).ToArray();
                    start.SignalAndWait();
                    for (var i = 0; i < 40; i++)
                    {
                        Batch(local, child, reference, tables, ids, true);
                        Compare(child, reference, tables, ids);
                    }
                }
                catch (Exception e)
                {
                    lock (failures)
                        failures.Add(e);
                }
            })).ToArray();

            Task.WaitAll(tasks);
            Assert.Empty(failures);

            var after = LookupPaths.Check(middle);
            foreach (var table in tables)
                Assert.Equal(middleView[table].Keys.OrderBy(k => k), after[table].Keys.OrderBy(k => k));

            foreach (var child in children)
                child.Dispose();
        }

        static void Batch(Random random, Context context, Dictionary<string, uint>[] reference, TableId[] tables, uint[] ids, bool mayRetract)
        {
            if (mayRetract && random.Next(10) == 0)
            {
                var cleared = random.Next(tables.Length);
                context.ClearTable(tables[cleared]);
                reference[cleared].Clear();
                return;
            }

            var ops = new List<(int Table, uint[] Key, uint Value, bool Remove)>();
            for (var n = random.Next(1, 8); n > 0; n--)
            {
                var t = random.Next(tables.Length);
                var key = Enumerable.Range(0, t + 1).Select(_ => ids[random.Next(ids.Length)]).ToArray();
                ops.Add((t, key, (uint)random.Next(1, 30), mayRetract && random.Next(4) == 0));
            }

            context.Write(state =>
            {
                foreach (var (t, key, value, remove) in ops)
                {
                    if (remove)
                        state.StageRemove(tables[t], key);
                    else
                        state.StageInsert(tables[t], key.Append(value).Append(state.Timestamp).ToArray());
                }

                return 0;
            });

            foreach (var (t, key, _, remove) in ops)
                if (remove)
                    reference[t].Remove(KeyOf(key));

            foreach (var (t, key, value, remove) in ops)
            {
                if (remove)
                    continue;

                var text = KeyOf(key);
                if (reference[t].TryGetValue(text, out var current) == false || value > current)
                    reference[t][text] = value;
            }
        }

        static void Compare(Context context, Dictionary<string, uint>[] reference, TableId[] tables, uint[] ids)
        {
            var seen = LookupPaths.Check(context, ids.Select(id => new[] { id }));
            for (var t = 0; t < tables.Length; t++)
            {
                var actual = seen[tables[t]];
                Assert.Equal(reference[t].Count, actual.Count);
                foreach (var (key, value) in reference[t])
                {
                    Assert.True(actual.TryGetValue(key, out var row), $"key [{key}] missing");
                    Assert.Equal(value, row![t + 1]);
                }
            }
        }

    }

}
