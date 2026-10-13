using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The context differential, the M9 gate (colored-contexts spec §17 M9.4): the spike's <c>DifferentialTests</c>
    /// ported to the engine (<see cref="SpikePair"/>). Random programs of id mints, unions, constructor get-or-adds, rows of
    /// functions, lattices and relations, rebuilds, children three deep, ancestors changing after their children exist,
    /// random freezes, disposal and retractions run on the engine and the naive <see cref="SpikeReferenceContext"/> at
    /// once; half the seeds also run the spike as a second oracle (and then retract nothing, which the spike cannot).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The id pool is kept small so keys collide and congruence cascades. Retractions are attempted anywhere: where §6.7
    /// allows them (the root without live descendants, a child of a frozen parent without live descendants) they apply;
    /// elsewhere the engine must refuse without changing anything. Checks compare views (exactly with the reference,
    /// modulo renaming with the spike), lookups, column scans (through the lift reads in children) and scans since a clock
    /// reading; every rebuild runs the engine's invariant checker, and every write the lookup-path comparison.
    /// </para>
    /// <para>
    /// <c>EGGNOG_CONTEXT_SEEDS</c> sets the number of seeds (400 by default). A failing program prints itself.
    /// </para>
    /// </remarks>
    public class ContextDifferentialTests
    {

        /// <summary>
        /// The seeds to run: 400 by default, or as many as <c>EGGNOG_CONTEXT_SEEDS</c> says.
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var count = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_CONTEXT_SEEDS"), out var n) && n > 0 ? n : 400;
            var data = new TheoryData<int>();
            for (var seed = 0; seed < count; seed++)
                data.Add(seed);

            return data;
        }

        /// <summary>
        /// Random programs of 150 steps, contexts three deep: the engine agrees with the reference (and, on even seeds,
        /// with the spike) at every check.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void RandomProgramsAgreeWithTheOracles(int seed)
        {
            Run(seed, 150, withSpike: seed % 2 == 0);
        }

        /// <summary>
        /// Longer programs, where classes grow, catch-up and step 4 copy many rows, and deep chains freeze on top of each
        /// other; the coverage counters show the programs reach every branch of the child's rebuild.
        /// </summary>
        [Fact]
        public void LongProgramsAgree()
        {
            var coverage = new Coverage();
            for (var seed = 0; seed < 24; seed++)
                Run(100_000 + seed, 600, withSpike: seed % 3 == 0, coverage);

            Assert.True(coverage.CatchUpRowsExamined > 0, "no child caught up with an ancestor's rows");
            Assert.True(coverage.RowsCopied > 0, "no ancestor row was copied into a child");
#if DEBUG
            // Why a row was hidden is recorded in DEBUG builds of the engine only (TableLayer.ReasonFor).
            Assert.True(coverage.Mentions > 0, "no ancestor row was copied for naming a loser");
            Assert.True(coverage.KeyHeld > 0, "no ancestor row was copied for its key");
#endif
            Assert.True(coverage.AncestorRowsExamined > 0, "step 4 examined no ancestor row");
            Assert.True(coverage.Retractions > 0, "no child retracted a row");
            Assert.True(coverage.FrozenChildren > 0, "no child was frozen");
            Assert.Equal(0, coverage.RestampedCopies);
        }

        /// <summary>
        /// The root alone: a plain e-graph, with no contexts in play.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void RandomRootProgramsAgree(int seed)
        {
            var root = SpikePair.Root(withSpike: seed % 2 == 0);
            var live = new List<SpikePair> { root };
            var random = new Random(seed);
            try
            {
                for (var step = 0; step < 150; step++)
                    Step(random, root, live, allowChildren: false, retractions: root.SpikeContext is null);

                root.Check();
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.Message}{Environment.NewLine}Program:{Environment.NewLine}{root.Program.Trace}", e);
            }
        }

        /// <summary>
        /// Runs one program and checks every live pair at the end.
        /// </summary>
        /// <param name="seed">The program's seed.</param>
        /// <param name="steps">The number of steps.</param>
        /// <param name="withSpike">Whether the spike runs too (then nothing is retracted).</param>
        /// <param name="coverage">Accumulates what the program exercised, or <c>null</c>.</param>
        static void Run(int seed, int steps, bool withSpike, Coverage? coverage = null)
        {
            var random = new Random(seed);
            var root = SpikePair.Root(withSpike);
            root.Program.RawChildWrites = withSpike == false;
            var live = new List<SpikePair> { root };
            var freezeAt = random.Next(3) == 0 ? random.Next(steps / 3, steps) : -1;
            try
            {
                for (var step = 0; step < steps; step++)
                {
                    if (step == freezeAt && root.IsFrozen == false)
                    {
                        root.Freeze();
                        root.Check();
                    }

                    var writable = live.Where(p => p.IsFrozen == false).ToList();
                    if (writable.Count == 0)
                        break;

                    Step(random, writable[random.Next(writable.Count)], live, allowChildren: true, retractions: withSpike == false, coverage);
                }

                foreach (var pair in live)
                {
                    pair.Check();
                    coverage?.Add(pair);
                }
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.Message}{Environment.NewLine}Program:{Environment.NewLine}{root.Program.Trace}", e);
            }
        }

        /// <summary>
        /// One random step of <paramref name="pair"/>, the spike's distribution plus freezes and retractions.
        /// </summary>
        internal static void Step(Random random, SpikePair pair, List<SpikePair> live, bool allowChildren, bool retractions, Coverage? coverage = null)
        {
            var schema = pair.Program.Schema;
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
                    pair.Union((uint)Pick(), (uint)Pick());
                    break;
                case < 44:
                    pair.GetOrAdd(schema.Pair, [Pick(), Pick()]);
                    break;
                case < 52:
                    pair.GetOrAdd(schema.Wrap, [Pick()]);
                    break;
                case < 58:
                    pair.Insert(schema.Pair, [Pick(), Pick()], Pick());
                    break;
                case < 63:
                    pair.Insert(schema.Low, [Pick()], (ulong)random.Next(10));
                    break;
                case < 66:
                    pair.Insert(schema.Bits, [Pick()], 1UL << random.Next(4));
                    break;
                case < 72:
                    pair.Insert(schema.Tag, [Pick(), (ulong)random.Next(3)]);
                    break;
                case < 76:
                    pair.GetOrAdd(schema.Named, [(ulong)random.Next(4)]);
                    break;
                case < 84:
                    if (allowChildren && pair.Depth < 3 && live.Count < 8)
                        live.Add(pair.CreateChild());
                    break;
                case < 88:
                    pair.Rebuild();
                    break;
                case < 92:
                    foreach (var other in live.Where(p => p.Engine.IsDisposed == false))
                        other.Check();
                    break;
                case < 94:
                    if (allowChildren && pair.CanFreeze && pair.Parent is not null)
                    {
                        pair.Freeze();
                        pair.Check();
                    }

                    break;
                case < 98:
                    if (retractions)
                        Retract(random, pair, schema, Pick, coverage);
                    break;
                default:
                    if (allowChildren && pair.Parent is not null && pair.Children.Count == 0)
                    {
                        coverage?.Add(pair);
                        pair.Dispose();
                        live.Remove(pair);
                    }

                    break;
            }
        }

        /// <summary>
        /// A retraction: of a key (usually one the pair sees), or of a whole table.
        /// </summary>
        static void Retract(Random random, SpikePair pair, SpikeTestSchema schema, Func<ulong> pick, Coverage? coverage)
        {
            var table = schema.Tables[random.Next(schema.Tables.Count)];
            if (random.Next(8) == 0)
            {
                pair.Clear(table);
                return;
            }

            var key = new ulong[table.KeyCount];
            var rows = pair.Reference.View().Rows(table);
            if (rows.Count > 0 && random.Next(4) != 0)
            {
                rows[random.Next(rows.Count)].AsSpan(0, table.KeyCount).CopyTo(key);
            }
            else
            {
                for (var i = 0; i < key.Length; i++)
                    key[i] = table.IsId[i] ? pick() : (ulong)random.Next(4);
            }

            if (pair.Facade.CanRetract && pair.Parent is not null && coverage is not null)
                coverage.Retractions++;

            pair.Delete(table, key);
        }

    }

}
