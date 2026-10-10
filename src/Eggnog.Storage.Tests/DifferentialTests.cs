using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Eggnog.Storage.Tests.Harness;

using Xunit;

namespace Eggnog.Storage.Tests
{

    /// <summary>
    /// Random programs run against the store and the oracle together, checked at random points and at the end.
    /// </summary>
    /// <remarks>
    /// The identifier pool is kept small so that keys collide and congruence cascades; contexts nest three deep;
    /// ancestors keep changing after children are made; the root is sometimes frozen part way through.
    /// </remarks>
    public class DifferentialTests
    {

        sealed class Schema
        {

            public Schema(Store store)
            {
                Pair = store.DefineTable(TableSchema.Constructor("pair", ColumnKind.Id, ColumnKind.Id));
                Wrap = store.DefineTable(TableSchema.Constructor("wrap", ColumnKind.Id));
                Low = store.DefineTable(TableSchema.Lattice("low", Math.Min, ColumnKind.Id));
                Tag = store.DefineTable(TableSchema.Relation("tag", ColumnKind.Id, ColumnKind.Value));
                Named = store.DefineTable(TableSchema.Constructor("named", ColumnKind.Value));
            }

            public Table Pair { get; }

            public Table Wrap { get; }

            public Table Low { get; }

            public Table Tag { get; }

            public Table Named { get; }

        }

        /// <summary>
        /// The seeds to run: 400 by default, or as many as <c>EGGNOG_STORAGE_SEEDS</c> says, for a longer soak.
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var count = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_STORAGE_SEEDS"), out var n) && n > 0 ? n : 400;
            var data = new TheoryData<int>();
            for (var seed = 0; seed < count; seed++)
                data.Add(seed);

            return data;
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void RandomProgramsAgreeWithTheOracle(int seed)
        {
            Pair.ResetTrace();
            try
            {
                Run(seed);
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException(e.Message + Environment.NewLine + "Program:" + Environment.NewLine + Pair.Trace);
            }
        }

        /// <summary>
        /// Longer programs, long enough that tables fill with dead slots and get compacted, and logs get trimmed.
        /// </summary>
        [Fact]
        public void LongProgramsCompactAndStillAgree()
        {
            var before = RowSet.Compactions;

            for (var seed = 0; seed < 40; seed++)
            {
                Pair.ResetTrace();
                try
                {
                    Run(100_000 + seed, steps: 600);
                }
                catch (Exception e)
                {
                    throw new Xunit.Sdk.XunitException($"seed {100_000 + seed}: " + e.Message + Environment.NewLine + "Program:" + Environment.NewLine + Pair.Trace);
                }
            }

            Assert.True(RowSet.Compactions > before, "no program compacted anything, so compaction went untested");
        }

        static void Run(int seed, int steps = 150)
        {
            var random = new Random(seed);
            var store = new Store();
            var schema = new Schema(store);
            var root = Pair.Root(store);
            var live = new List<Pair> { root };

            var freezeAt = random.Next(3) == 0 ? random.Next(steps / 3, steps) : -1;

            for (var step = 0; step < steps; step++)
            {
                if (step == freezeAt)
                {
                    root.Store.Freeze();
                    root.Check();
                }

                var writable = live.Where(p => p.Store.IsFrozen == false).ToList();
                if (writable.Count == 0)
                    break;

                var pair = writable[random.Next(writable.Count)];
                Step(random, schema, pair, live);
            }

            foreach (var pair in live)
                pair.Check();
        }

        /// <summary>
        /// The root alone: a plain e-graph, with no contexts in play.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void RandomRootProgramsAgreeWithTheOracle(int seed)
        {
            var random = new Random(seed);
            var store = new Store();
            var schema = new Schema(store);
            var root = Pair.Root(store);
            var live = new List<Pair> { root };

            for (var step = 0; step < 150; step++)
                Step(random, schema, root, live, allowChildren: false);

            root.Check();
        }

        /// <summary>
        /// Children of a frozen root, each driven on its own thread, each agreeing with the oracle afterwards.
        /// </summary>
        [Fact]
        public void ChildrenOfAFrozenRootAgreeWhenDrivenConcurrently()
        {
            var store = new Store();
            var schema = new Schema(store);
            var root = Pair.Root(store);
            var setup = new Random(7);
            var scratch = new List<Pair> { root };

            for (var step = 0; step < 120; step++)
                Step(setup, schema, root, scratch, allowChildren: false);

            root.Store.Freeze();
            root.Check();

            var children = Enumerable.Range(0, 16).Select(_ => root.CreateChild()).ToArray();

            Parallel.For(0, children.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var random = new Random(1000 + i);
                var own = new List<Pair> { children[i] };
                for (var step = 0; step < 150; step++)
                    Step(random, schema, children[i], own, allowChildren: false);
            });

            foreach (var child in children)
                child.Check();
        }

        static void Step(Random random, Schema schema, Pair pair, List<Pair> live, bool allowChildren = true)
        {
            var ids = pair.VisibleIds();
            ulong Pick() => ids[random.Next(ids.Count)];

            var roll = random.Next(100);

            if (ids.Count < 2 || (roll < 12 && ids.Count < 14))
            {
                pair.NewId();
                return;
            }

            switch (roll)
            {
                case < 26:
                    pair.Union(Pick(), Pick());
                    break;
                case < 44:
                    pair.GetOrAdd(schema.Pair, new[] { Pick(), Pick() });
                    break;
                case < 52:
                    pair.GetOrAdd(schema.Wrap, new[] { Pick() });
                    break;
                case < 58:
                    pair.Insert(schema.Pair, new[] { Pick(), Pick() }, Pick());
                    break;
                case < 66:
                    pair.Insert(schema.Low, new[] { Pick() }, (ulong)random.Next(10));
                    break;
                case < 72:
                    pair.Insert(schema.Tag, new[] { Pick(), (ulong)random.Next(3) });
                    break;
                case < 76:
                    pair.GetOrAdd(schema.Named, new[] { (ulong)random.Next(4) });
                    break;
                case < 84:
                    if (allowChildren && pair.Depth < 3 && live.Count < 8)
                        live.Add(pair.CreateChild());
                    break;
                case < 90:
                    pair.Store.Rebuild();
                    break;
                case < 97:
                    foreach (var other in live.Where(p => p.Store.IsDisposed == false))
                        other.Check();
                    break;
                default:
                    if (allowChildren && pair.Parent is not null && pair.Children.Count == 0)
                    {
                        pair.Dispose();
                        live.Remove(pair);
                    }
                    break;
            }
        }

    }

}
