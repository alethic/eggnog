using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Random chains in which ancestors freeze after their descendants exist, which is allowed (the descendants catch
    /// up once), then the deepest context and new siblings keep writing, hiding and retracting. Every context is
    /// compared with a replayed reference and checked with <see cref="LookupPaths"/>.
    /// </summary>
    public class LateFreezePropertyTests
    {

        public static TheoryData<int> Seeds()
        {
            var data = new TheoryData<int>();
            for (var seed = 0; seed < 400; seed++)
                data.Add(seed);

            return data;
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void LateFreezesKeepEveryViewACopy(int seed)
        {
            var random = new Random(seed);
            var store = new Store(ParallelHeuristics.Serial);
            var tables = new[]
            {
                store.AddTable(MaxFunction(1), name: "f1"),
                store.AddTable(MaxFunction(2), name: "f2"),
            };
            var root = store.Root;
            var ids = Enumerable.Range(0, 6).Select(_ => root.NewId()).ToArray();
            var contexts = new List<(Context Context, Dictionary<string, uint>[] Reference)>();
            var rootReference = tables.Select(_ => new Dictionary<string, uint>()).ToArray();
            for (var batch = random.Next(1, 4); batch > 0; batch--)
                Write(random, root, rootReference, tables, ids, false);

            if (random.Next(2) == 0)
                root.Freeze();

            contexts.Add((root, rootReference));

            // A chain written top down, each level before it creates the next.
            var depth = random.Next(2, 5);
            for (var level = 1; level <= depth; level++)
            {
                var (parent, parentReference) = contexts[level - 1];
                var child = parent.CreateChild();
                var reference = parentReference.Select(r => new Dictionary<string, uint>(r)).ToArray();
                contexts.Add((child, reference));
                for (var batch = random.Next(1, 5); batch > 0; batch--)
                    Write(random, child, reference, tables, ids, parent.IsFrozen);
            }

            // Freeze a prefix of the chain while its descendants live.
            var frozenTo = random.Next(0, depth);
            for (var level = 0; level <= frozenTo; level++)
                contexts[level].Context.Freeze();

            foreach (var (context, reference) in contexts)
                Compare(context, reference, tables, ids);

            // The deepest context writes on, hiding and (under a frozen parent) retracting.
            var (deepest, deepestReference) = contexts[^1];
            for (var batch = random.Next(1, 5); batch > 0; batch--)
            {
                Write(random, deepest, deepestReference, tables, ids, contexts[^2].Context.IsFrozen);
                Compare(deepest, deepestReference, tables, ids);
            }

            // New siblings under frozen contexts, written and checked; the chain is unchanged.
            var siblings = new List<(Context, Dictionary<string, uint>[])>();
            for (var level = 0; level <= frozenTo; level++)
            {
                var (parent, parentReference) = contexts[level];
                var sibling = parent.CreateChild();
                var reference = parentReference.Select(r => new Dictionary<string, uint>(r)).ToArray();
                for (var batch = random.Next(1, 4); batch > 0; batch--)
                    Write(random, sibling, reference, tables, ids, true);

                siblings.Add((sibling, reference));
            }

            foreach (var (context, reference) in contexts.Concat(siblings))
                Compare(context, reference, tables, ids);
        }

        static void Write(Random random, Context context, Dictionary<string, uint>[] reference, TableId[] tables, uint[] ids, bool mayRetract)
        {
            if (mayRetract && random.Next(6) == 0)
            {
                var cleared = random.Next(tables.Length);
                context.ClearTable(tables[cleared]);
                reference[cleared].Clear();
                return;
            }

            var ops = new List<(int Table, uint[] Key, uint Value, bool Remove)>();
            for (var n = random.Next(1, 6); n > 0; n--)
            {
                var t = random.Next(tables.Length);
                var key = Enumerable.Range(0, t + 1).Select(_ => ids[random.Next(ids.Length)]).ToArray();
                var remove = mayRetract && random.Next(4) == 0;
                ops.Add((t, key, (uint)random.Next(1, 20), remove));
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
                Assert.True(reference[t].Count == actual.Count, $"depth {context.Depth}, table {t}: {actual.Count} visible, {reference[t].Count} expected");
                foreach (var (key, value) in reference[t])
                {
                    Assert.True(actual.TryGetValue(key, out var row), $"depth {context.Depth}, table {t}: key [{key}] missing");
                    Assert.Equal(value, row![t + 1]);
                }
            }
        }

    }

}
