using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Verification of the colors M10.4 fixes (colored-contexts spec §11.2, I33): the id-output function path now skips a
    /// key the parent holds, the candidate arena clears only the slots it used, and the memo is kept only for a lift that
    /// settled.
    /// </summary>
    public class LiftFixVerificationTests
    {

        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        static FunctionId Pick(ContextGraph g, MergeFn merge) => g.EGraph.AddTable(new FunctionConfig
        {
            Schema = [ColumnTy.Id, ColumnTy.Id],
            Default = DefaultVal.Fail,
            Merge = merge,
            Name = "pick",
        });

        /// <summary>
        /// An assert-eq id-output function whose parent row sits under a key the cases merge with another parent id: the
        /// lift writes the new key's row next to the meet's unions of the keys and of the outputs, and the rebuild that
        /// folds the two rows together must not see a conflict. Into the root, and into a child of a frozen root.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnAssertEqIdFunctionUnderAMergedKeyLiftsWithoutAConflict(bool childParent)
        {
            var (g, ids) = Setup(5);
            var (m, z, k, k0, e) = (ids[0], ids[1], ids[2], ids[3], ids[4]);
            var pick = Pick(g, MergeFn.AssertEq);
            g.Root.StageRow(pick, [k0, e]);
            g.Root.FlushUpdates();
            EGraphContext? scope = null;
            if (childParent)
            {
                g.Root.Freeze();
                scope = g.Root.CreateChild();
            }

            var parent = scope ?? g.Root;
            try
            {
                using var set = new CaseSet(parent);
                var one = set.AddCase();
                one.StageUnion(k, k0);
                one.StageUnion(e, m);
                one.StageUnion(e, z);
                var two = set.AddCase();
                two.StageUnion(k, k0);
                two.StageUnion(e, z);

                LiftReport? report = null;
                var thrown = Record.Exception(() => report = set.Lift(new LiftSpec(new LiftTable(pick, LiftTableKind.Function)), LiftOptions.Default));
                Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
                Assert.False(parent.IsFaulted);
                Assert.Equal(parent.Find(k), parent.Find(k0));
                Assert.Equal(parent.Find(e), parent.Find(z));
                Assert.NotEqual(parent.Find(e), parent.Find(m));
                Assert.Equal(parent.Find(e), parent.LookupId(pick, [k]));
                parent.CheckInvariants();
            }
            finally
            {
                scope?.Dispose();
            }
        }

        /// <summary>
        /// I33 completeness for an id-output function merged by a declared lattice primitive (here the larger id): the
        /// root holds <c>pick(k) = e</c>, and every case writes <c>pick(k) = p</c>, which the merge keeps, so
        /// <c>pick(k) = p</c> holds in every live case while <c>e</c> and <c>p</c> stay distinct there. The lift must write
        /// it, as it writes a base-output function's joined value over a stored one. The fix that skips every key the
        /// parent holds (to keep an assert-eq merge from conflicting) assumes each case's output is the class of the
        /// parent's, which holds for union and assert-eq merges only.
        /// </summary>
        [Fact]
        public void AnIdFunctionWithALatticeMergeLiftsAValueEveryCaseHoldsOverTheStoredOne()
        {
            var (g, ids) = Setup(3);
            var (k, e, p) = (ids[0], ids[1], ids[2]);
            var larger = Register(g.EGraph, (_, args) => Math.Max(args[0], args[1]));
            var pick = Pick(g, MergeFn.Primitive(larger, MergeFn.Old, MergeFn.New).AsLattice());
            g.Root.StageRow(pick, [k, e]);
            g.Root.FlushUpdates();

            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                @case.StageRow(pick, [k, p]);
                @case.EnsureClean();
                Assert.Equal(p, @case.LookupId(pick, [k]));
                Assert.NotEqual(@case.Find(e), @case.Find(p));
            }

            set.Lift(new LiftSpec(new LiftTable(pick, LiftTableKind.Function)), LiftOptions.Default);
            Assert.True(p == g.Root.LookupId(pick, [k]), $"pick(k) = p holds in every live case, but the root still holds pick(k) = {g.Root.LookupId(pick, [k])?.Rep} (e = {e.Rep}, p = {p.Rep})");
        }

        /// <summary>
        /// <see cref="LiftCandidates"/> against a model: random resets of random shapes, each followed by adds drawn from
        /// a small or a large key space, so that both the full clear and the entry-by-entry clear run after tables of every
        /// size. Each add must report exactly what a set of keys reports, and the entries must be the first per key.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void ACandidateArenaMatchesAModelOverRandomResets(int seed)
        {
            var random = new Random(seed);
            var arena = new LiftCandidates();
            for (var round = 0; round < 200; round++)
            {
                var width = random.Next(1, 4);
                var keyWidth = random.Next(0, width + 1);
                arena.Reset(width, keyWidth);
                var model = new Dictionary<string, uint[]>();
                var order = new List<uint[]>();
                var adds = random.Next(4) == 0 ? random.Next(1000, 5000) : random.Next(0, 20);
                var space = (uint)(random.Next(2) == 0 ? 8 : 100_000);
                for (var i = 0; i < adds; i++)
                {
                    var entry = new uint[width];
                    for (var c = 0; c < width; c++)
                        entry[c] = (uint)random.Next((int)space);

                    var key = string.Join(",", entry.Take(keyWidth));
                    var expected = model.ContainsKey(key) == false;
                    if (expected)
                    {
                        model[key] = entry;
                        order.Add(entry);
                    }

                    Assert.Equal(expected, arena.Add(entry));
                }

                Assert.Equal(order.Count, arena.Count);
                for (var i = 0; i < order.Count; i++)
                    Assert.Equal(order[i], arena[i].ToArray());
            }
        }

        /// <summary>
        /// The memo is kept after a lift whose earlier round skipped a row over the candidate limit but whose last round
        /// skipped nothing: that lift reached a state no limit can extend, so a later lift with a wider limit over the same
        /// cases is a memo hit and must find nothing new either.
        /// </summary>
        [Fact]
        public void ALiftThatSkippedOnlyInAnEarlierRoundSettlesCompletely()
        {
            var (g, ids) = Setup(4);
            var (a, b, c, d) = (ids[0], ids[1], ids[2], ids[3]);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            foreach (var @case in new[] { one, two })
            {
                @case.StageUnion(a, b);
                @case.StageUnion(b, c);
                g.Assert(@case, g.Edge, a, d);
            }

            var first = set.Lift(new LiftSpec(new LiftTable(g.Edge, LiftTableKind.Relation)), new LiftOptions { CandidateLimit = 2 });
            var second = set.Lift(new LiftSpec(new LiftTable(g.Edge, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.True(g.Root.ContainsKey(g.Edge, [a, d]), $"edge(a, d) holds in both cases (first: skipped {first.Skipped}, rounds {first.Rounds}; second: changed {second.Changed}, rounds {second.Rounds})");
            Assert.Equal(g.Root.Find(a), g.Root.Find(c));
        }

    }

}
