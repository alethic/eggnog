using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Context = copy (colored-contexts spec §17 M9.4 gate, I8, review finding R1-8): every context of a random program
    /// (<see cref="ContextDifferentialTests.Step"/>, retractions included) is compared with a fresh store into whose root
    /// the assertions of its chain are replayed, in order, with the same ids.
    /// </summary>
    /// <remarks>
    /// The copy mints ids up to the largest id the context can name, so ids are explicit and equal; then replays every
    /// union, row, delete and clear of the chain (ancestors' first, which is the order they took effect in) and rebuilds.
    /// The context and the copy must then agree exactly: every id's canonical id (both make a class's minimum canonical),
    /// every table's rows, and, in a child, the journal, which holds exactly the copy's displaced ids minus those displaced
    /// in the parent's view when the child was created (recorded by the harness at <see cref="SpikePair.CreateChild"/>).
    /// <c>EGGNOG_CONTEXT_SEEDS</c> sets the number of seeds (400 by default).
    /// </remarks>
    public class ContextCopyTests
    {

        /// <summary>
        /// Random programs of 150 steps, retractions included: at the end every live context equals its chain's
        /// assertions replayed at a fresh root.
        /// </summary>
        [Theory]
        [MemberData(nameof(ContextDifferentialTests.Seeds), MemberType = typeof(ContextDifferentialTests))]
        public void EveryContextIsACopyOfItsChainReplayedAtARoot(int seed)
        {
            var random = new Random(seed);
            var root = SpikePair.Root(withSpike: false);
            root.Program.CheckFoundRows = false;
            root.Program.CheckWrites = false;
            root.Program.RawChildWrites = seed % 2 == 1;
            var live = new List<SpikePair> { root };
            var freezeAt = random.Next(3) == 0 ? random.Next(50, 150) : -1;
            try
            {
                for (var step = 0; step < 150; step++)
                {
                    if (step == freezeAt && root.IsFrozen == false)
                        root.Freeze();

                    var writable = live.Where(p => p.IsFrozen == false).ToList();
                    if (writable.Count == 0)
                        break;

                    ContextDifferentialTests.Step(random, writable[random.Next(writable.Count)], live, allowChildren: true, retractions: true);
                }

                foreach (var pair in live)
                    AssertIsACopy(pair);
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.Message}{Environment.NewLine}Program:{Environment.NewLine}{root.Program.Trace}", e);
            }
        }

        /// <summary>
        /// A child that asserts nothing sees exactly its parent, whose last writes, unions and settle the child read.
        /// </summary>
        [Fact]
        public void AChildThatAssertsNothingIsItsParent()
        {
            var root = SpikePair.Root(withSpike: false);
            var schema = root.Program.Schema;
            var ids = Enumerable.Range(0, 6).Select(_ => root.NewId()).ToArray();
            root.GetOrAdd(schema.Pair, [ids[0], ids[1]]);
            root.GetOrAdd(schema.Pair, [ids[2], ids[1]]);
            root.Insert(schema.Low, [ids[3]], 4);
            root.Union(ids[0], ids[2]);
            var child = root.CreateChild();
            child.Check();
            AssertIsACopy(child);

            // The parent changes again; the child catches up and is still a copy.
            root.Union(ids[3], ids[4]);
            root.Insert(schema.Low, [ids[4]], 2);
            root.Insert(schema.Tag, [ids[5], 1]);
            child.Check();
            AssertIsACopy(child);
        }

        /// <summary>
        /// Compares <paramref name="pair"/>'s context with its chain replayed at a fresh root.
        /// </summary>
        internal static void AssertIsACopy(SpikePair pair)
        {
            pair.Rebuild();
            var engine = pair.Engine;
            var ids = pair.VisibleIds();
            var copy = Replay(pair, ids);
            var tables = pair.Program.Schema.Tables;

            // Exact canonical ids.
            foreach (var id in ids)
                Assert.True(copy.Find(id) == engine.Find(id), $"{pair.Name}: find({id}) is {engine.Find(id)} in the context and {copy.Find(id)} in the copy");

            // Exact rows.
            var copyTables = copy.Schema.Tables;
            for (var t = 0; t < tables.Count; t++)
            {
                var expected = copy.Rows(copyTables[t]).Select(r => string.Join(",", r)).OrderBy(r => r, StringComparer.Ordinal).ToArray();
                var actual = pair.Facade.Rows(tables[t]).Select(r => string.Join(",", r)).OrderBy(r => r, StringComparer.Ordinal).ToArray();
                Assert.True(expected.SequenceEqual(actual), $"{pair.Name}: table {tables[t].Name} holds [{string.Join(" | ", actual)}], the copy [{string.Join(" | ", expected)}]");
            }

            if (engine.IsRoot)
                return;

            // The journal: the copy's displaced ids, minus those displaced in the parent's view at the child's creation.
            var displaced = copy.Context.DisplacedIds.ToArray().Where(id => ids.Contains(id));
            var expectedJournal = displaced.Where(id => pair.Reference.DisplacedInParentAtCreation.Contains(id) == false).OrderBy(id => id).ToArray();
            var journal = engine.DisplacedIds.ToArray().OrderBy(id => id).ToArray();
            Assert.True(expectedJournal.SequenceEqual(journal), $"{pair.Name}: the journal is [{string.Join(",", journal)}], the copy's displaced ids less the parent's at creation [{string.Join(",", expectedJournal)}]");
        }

        /// <summary>
        /// Replays the assertions of <paramref name="pair"/>'s chain into the root of a fresh store, ids explicit.
        /// </summary>
        static SpikeContextFacade Replay(SpikePair pair, List<uint> ids)
        {
            var program = new SpikeProgram(withSpike: false);
            var copy = new SpikeContextFacade(program.Store.Root, program.Schema);
            var max = ids.Count == 0 ? 0u : ids.Max();
            for (var next = 0u; next <= max; next++)
                Assert.Equal(next, copy.NewId());

            var tables = pair.Program.Schema.Tables;
            SpikeTable Map(SpikeTable table) => program.Schema.Tables[tables.ToList().IndexOf(table)];
            foreach (var op in pair.Reference.ChainOps())
            {
                switch (op.Kind)
                {
                    case SpikeReferenceOpKind.Union:
                        copy.Union((uint)op.A, (uint)op.B);
                        break;
                    case SpikeReferenceOpKind.Insert:
                        copy.Insert(Map(op.Table!), op.Row!);
                        break;
                    case SpikeReferenceOpKind.Delete:
                        copy.Delete(Map(op.Table!), op.Row!);
                        break;
                    default:
                        copy.Clear(Map(op.Table!));
                        break;
                }
            }

            copy.Rebuild();
            return copy;
        }

    }

}
