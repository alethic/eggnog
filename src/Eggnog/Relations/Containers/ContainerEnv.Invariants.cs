using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerEnv<TContainer>
    {

        /// <inheritdoc />
        /// <remarks>
        /// Checks that every hash-cons entry carries its container's current hash, sits in the shard of that hash and is
        /// what a lookup of its container finds; that the reverse map holds exactly one entry per hash-cons entry (same
        /// container, hash and id), each in the shard of its id's hash and found by a lookup of that exact entry; and that
        /// the element index holds exactly the occurrences of every hash-cons entry's values under its id. The part of
        /// the engine's invariant checks that concerns containers at the root.
        /// </remarks>
        public override void CheckInvariants()
        {
            var expectedIndex = new Dictionary<(uint Element, uint Id), int>();
            var expectedById = new Dictionary<(uint Id, ulong Hash), int>();
            var elements = new List<uint>();
            for (var s = 0; s < toId.Length; s++)
            {
                var shard = toId[s];
                for (var slot = shard.NextFullSlot(0); slot >= 0; slot = shard.NextFullSlot(slot + 1))
                {
                    var entry = shard.GetEntry(slot);
                    if (entry.Hash != entry.Container.ComputeHash())
                        Fail($"container {entry.Id} is stored under hash {entry.Hash:x} but hashes to {entry.Container.ComputeHash():x}");

                    if (shardData.ShardOf(entry.Hash) != s)
                        Fail($"container {entry.Id} is in hash-cons shard {s} but belongs in {shardData.ShardOf(entry.Hash)}");

                    var match = new ContainerContentMatch<TContainer>(entry.Container, entry.Hash);
                    if (shard.Find(entry.Hash, ref match) != slot)
                        Fail($"looking up container {entry.Id} does not find its own entry (duplicate key or broken probe)");

                    Increment(expectedById, (entry.Id, entry.Hash));
                    elements.Clear();
                    var collector = new ContainerElementCollector(elements);
                    entry.Container.ForEachValue(ref collector);
                    foreach (var element in elements)
                        Increment(expectedIndex, (element, entry.Id));
                }
            }

            var actualById = new Dictionary<(uint Id, ulong Hash), int>();
            for (var s = 0; s < byId.Length; s++)
            {
                var shard = byId[s];
                for (var slot = shard.NextFullSlot(0); slot >= 0; slot = shard.NextFullSlot(slot + 1))
                {
                    var entry = shard.GetEntry(slot);
                    var idHash = FxHasher.Hash(entry.Id);
                    if (shardData.ShardOf(idHash) != s)
                        Fail($"the reverse-map entry of {entry.Id} is in shard {s} but belongs in {shardData.ShardOf(idHash)}");

                    var exact = new ContainerExactMatch<TContainer>(entry.Container, entry.Hash, entry.Id);
                    if (shard.Find(idHash, ref exact) < 0)
                        Fail($"the reverse-map entry of {entry.Id} cannot be found again");

                    var home = toId[shardData.ShardOf(entry.Hash)];
                    var content = new ContainerContentMatch<TContainer>(entry.Container, entry.Hash);
                    var homeSlot = home.Find(entry.Hash, ref content);
                    if (homeSlot < 0 || home.GetEntry(homeSlot).Id != entry.Id)
                        Fail($"the reverse-map entry of {entry.Id} has no matching hash-cons entry");

                    Increment(actualById, (entry.Id, entry.Hash));
                }
            }

            CompareCounts(expectedById, actualById, "reverse-map entry (id, hash)");
            CompareCounts(expectedIndex, valIndex.Snapshot(), "element-index occurrence (value, id)");
        }

        static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key)
            where TKey : notnull
        {
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        static void CompareCounts<TKey>(Dictionary<TKey, int> expected, Dictionary<TKey, int> actual, string what)
            where TKey : notnull
        {
            foreach (var (key, count) in expected)
            {
                actual.TryGetValue(key, out var found);
                if (found != count)
                    Fail($"{what} {key} is recorded {found} times but should be {count}");
            }

            foreach (var (key, count) in actual)
                if (expected.ContainsKey(key) == false)
                    Fail($"{what} {key} is recorded {count} times but should not be");
        }

        static void Fail(string message)
        {
            throw new InvalidOperationException($"Container environment of {typeof(TContainer).Name}: {message}.");
        }

    }

}
