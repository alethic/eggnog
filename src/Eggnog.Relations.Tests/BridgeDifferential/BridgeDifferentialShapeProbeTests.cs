using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Hand-written shapes run through the three-way harness: functions without keys (egglog's globals) of every kind,
    /// and a function with four keys. The random generator produces both now, but rarely.
    /// </summary>
    public class BridgeDifferentialShapeProbeTests
    {

        const int A = 0;
        const int I64 = BridgeDiffSort.I64;

        /// <summary>
        /// A min lattice, an id function and a relation without keys, written and read by rules.
        /// </summary>
        [Fact]
        public void KeylessFunctionsAgree()
        {
            BridgeDiffFunction[] functions =
            [
                new("NumA", BridgeDiffFunctionKind.Constructor, [I64], A, false),
                new("LeafA", BridgeDiffFunctionKind.Constructor, [], A, false),
                new("g", BridgeDiffFunctionKind.Min, [], I64, false),
                new("best", BridgeDiffFunctionKind.IdFunction, [], A, false),
                new("flag", BridgeDiffFunctionKind.Relation, [], BridgeDiffSort.Unit, true),
            ];
            var seeds = new List<BridgeDiffSeed>();
            for (var i = 0u; i < 4; i++)
                seeds.Add(new BridgeDiffSeed(BridgeDiffSeedKind.Term, 0, [BridgeDiffEntry.Const(i + 1, I64)], 0));

            seeds.Add(new BridgeDiffSeed(BridgeDiffSeedKind.Set, 2, [BridgeDiffEntry.Const(7, I64)], 0));
            seeds.Add(new BridgeDiffSeed(BridgeDiffSeedKind.Set, 3, [BridgeDiffEntry.Variable(3, A)], 0));

            var r0 = new BridgeDiffRule("r0", [I64, A],
                [new BridgeDiffAtom(0, [V(0, I64), V(1)], false)],
                [],
                [new BridgeDiffAction(BridgeDiffActionKind.Set, 2, default, [V(0, I64)], -1)],
                PlanStrategy.Gj, false);
            var r1 = new BridgeDiffRule("r1", [I64, A, BridgeDiffSort.Unit],
                [new BridgeDiffAtom(0, [V(0, I64), V(1)], false)],
                [new BridgeDiffPrim(BridgeDiffPrimitive.Less, V(0, I64), BridgeDiffEntry.Const(3, I64), V(2, BridgeDiffSort.Unit))],
                [new BridgeDiffAction(BridgeDiffActionKind.Set, 3, default, [V(1)], -1)],
                PlanStrategy.Gj, false);
            var r2 = new BridgeDiffRule("r2", [A, BridgeDiffSort.Unit],
                [new BridgeDiffAtom(3, [V(0)], false)],
                [],
                [new BridgeDiffAction(BridgeDiffActionKind.Lookup, 4, default, [], 1)],
                PlanStrategy.Gj, false);
            var r3 = new BridgeDiffRule("r3", [I64, A, A],
                [new BridgeDiffAtom(2, [V(0, I64)], false), new BridgeDiffAtom(0, [V(0, I64), V(1)], false)],
                [],
                [
                    new BridgeDiffAction(BridgeDiffActionKind.Lookup, 1, default, [], 2),
                    new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [V(1), V(2)], -1),
                ],
                PlanStrategy.MinCover, false);
            var r4 = new BridgeDiffRule("r4", [BridgeDiffSort.Unit],
                [new BridgeDiffAtom(4, [V(0, BridgeDiffSort.Unit)], false)],
                [],
                [new BridgeDiffAction(BridgeDiffActionKind.Subsume, 4, default, [], -1)],
                PlanStrategy.Gj, false);
            BridgeDiffRule[] rules = [r0, r1, r2, r3, r4];
            var all = Enumerable.Range(0, rules.Length).ToArray();
            var program = new BridgeDiffProgram(0, 1, functions, [.. seeds], rules, Enumerable.Repeat(all, 10).ToArray(), 10, 10_000, 100_000, false);
            var stats = new BridgeDiffStats();
            BridgeDiffRunner.Run(program, stats);
            Assert.Equal(1, stats.Saturated);
        }

        /// <summary>
        /// A constructor with four keys (egglog functions often have more than three arguments), whose right-hand-side
        /// lookups take the engine's N-column key path. The oracle and the comparison key rows by
        /// <see cref="BridgeDiffKey"/>, of any length.
        /// </summary>
        [Fact]
        public void FourKeyFunctionsAgree()
        {
            BridgeDiffFunction[] functions =
            [
                new("NumA", BridgeDiffFunctionKind.Constructor, [I64], A, true),
                new("F", BridgeDiffFunctionKind.Constructor, [A, A, A, A], A, true),
            ];
            var seeds = new List<BridgeDiffSeed>();
            for (var i = 0u; i < 4; i++)
                seeds.Add(new BridgeDiffSeed(BridgeDiffSeedKind.Term, 0, [BridgeDiffEntry.Const(i, I64)], 0));

            seeds.Add(new BridgeDiffSeed(BridgeDiffSeedKind.Term, 1, [V(0), V(1), V(2), V(3)], 0));

            // (= r (F a b c d)) => (union r (F b a d c))
            var swap = new BridgeDiffRule("swap", [A, A, A, A, A, A],
                [new BridgeDiffAtom(1, [V(0), V(1), V(2), V(3), V(4)], false)],
                [],
                [
                    new BridgeDiffAction(BridgeDiffActionKind.Lookup, 1, default, [V(1), V(0), V(3), V(2)], 5),
                    new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [V(4), V(5)], -1),
                ],
                PlanStrategy.Gj, false);
            var program = new BridgeDiffProgram(0, 1, functions, [.. seeds], [swap], [[0], [0], [0]], 3, 10_000, 100_000, false);
            var stats = new BridgeDiffStats();
            BridgeDiffRunner.Run(program, stats);
            Assert.Equal(1, stats.Saturated);
        }

        static BridgeDiffEntry V(int index, int sort = A) => BridgeDiffEntry.Variable(index, sort);

    }

}
