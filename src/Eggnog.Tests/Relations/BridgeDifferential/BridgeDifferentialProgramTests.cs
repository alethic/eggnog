using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Hand-written programs run through the bridge differential harness (<see cref="BridgeDiffRunner"/>): egglog test
    /// programs and the bridge's own tests, rewritten as <see cref="BridgeDiffProgram"/>s so that the semi-naive bridge,
    /// the naive bridge and the oracle must agree on them, and their known answers checked on top. Also checks that the
    /// harness is not vacuous: the comparison accepts a renaming and rejects real differences.
    /// </summary>
    public class BridgeDifferentialProgramTests
    {

        const int A = 0;
        const int I64 = BridgeDiffSort.I64;

        /// <summary>
        /// egglog's tests/merge-during-rebuild.egg: a <c>min</c> merge triggered by a rebuild, when unions make two keys of
        /// <c>distance</c> meet; the smaller distance must survive.
        /// </summary>
        [Fact]
        public void MergeDuringRebuildKeepsTheSmallerDistance()
        {
            BridgeDiffFunction[] functions =
            [
                new("Node", BridgeDiffFunctionKind.Constructor, [I64], A, false),
                new("distance", BridgeDiffFunctionKind.Min, [A, A], I64, false),
            ];
            BridgeDiffSeed[] seeds =
            [
                Term(0, Int(0)), Term(0, Int(1)), Term(0, Int(2)), Term(0, Int(3)),
                Set(1, Seed(2), Seed(3), Int(1)),
                Set(1, Seed(0), Seed(1), Int(2)),
                Union(0, 2),
                Union(1, 3),
            ];
            var view = Run(Program(functions, seeds, [], 0));

            var row = Assert.Single(view.Rows(1));
            Assert.Equal([view.Find(view.SeedId(0)), view.Find(view.SeedId(1)), 1u], row.Values);
        }

        /// <summary>
        /// egglog's tests/subsume-relation.egg: a subsumed relation row stays subsumed when a union makes it meet a row that
        /// is not, and rules that match only rows that are not subsumed stop seeing it.
        /// </summary>
        [Fact]
        public void SubsumptionSurvivesTheMergeOfARelationRow()
        {
            BridgeDiffFunction[] functions =
            [
                new("LeafA", BridgeDiffFunctionKind.Constructor, [], A, false),
                new("LeafB", BridgeDiffFunctionKind.Constructor, [], A, false),
                new("LeafC", BridgeDiffFunctionKind.Constructor, [], A, false),
                new("R", BridgeDiffFunctionKind.Relation, [A, A], BridgeDiffSort.Unit, true),
                new("Copy", BridgeDiffFunctionKind.Relation, [A, A], BridgeDiffSort.Unit, false),
            ];
            BridgeDiffSeed[] seeds =
            [
                Term(0), Term(1), Term(2),
                Set(3, Seed(0), Seed(2), Unit()),
                Union(1, 2) with { Phase = 2 },
            ];

            // r0: (subsume (R (A) (B))), a rule without atoms; r1: (R x y) => (Copy x y), over rows that are not subsumed.
            var r0 = new BridgeDiffRule("r0", [A, A],
                [],
                [],
                [
                    new BridgeDiffAction(BridgeDiffActionKind.Lookup, 0, default, [], 0),
                    new BridgeDiffAction(BridgeDiffActionKind.Lookup, 1, default, [], 1),
                    new BridgeDiffAction(BridgeDiffActionKind.Subsume, 3, default, [Var(0), Var(1)], -1),
                ],
                PlanStrategy.Gj,
                false);
            var r1 = new BridgeDiffRule("r1", [A, A, BridgeDiffSort.Unit],
                [new BridgeDiffAtom(3, [Var(0), Var(1), Var(2, BridgeDiffSort.Unit)], false)],
                [],
                [new BridgeDiffAction(BridgeDiffActionKind.Set, 4, default, [Var(0), Var(1), Unit()], -1)],
                PlanStrategy.MinCover,
                false);
            var view = Run(Program(functions, seeds, [r0, r1], 3));

            // R(A, C) and the subsumed R(A, B) merged when B and C did: one row, subsumed. Copy saw R(A, C) before.
            var (a, c) = (view.Find(view.SeedId(0)), view.Find(view.SeedId(2)));
            var row = Assert.Single(view.Rows(3));
            Assert.Equal([a, c], row.Values.Take(2));
            Assert.True(row.Subsumed);
            Assert.Equal([a, c], Assert.Single(view.Rows(4)).Values.Take(2));
        }

        /// <summary>
        /// The bridge's <c>ac</c> test (egglog-bridge/src/tests.rs:30-104) with its subsumption variant: commutativity and
        /// associativity saturate, and the left-associated sum of five numbers meets the right-associated one.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AssociativityAndCommutativitySaturate(bool canSubsume)
        {
            BridgeDiffFunction[] functions =
            [
                new("Num", BridgeDiffFunctionKind.Constructor, [I64], A, canSubsume),
                new("Add", BridgeDiffFunctionKind.Constructor, [A, A], A, canSubsume),
            ];
            var seeds = new List<BridgeDiffSeed>();
            for (var i = 0u; i < 5; i++)
                seeds.Add(Term(0, Int(i)));

            // Left: (+ 4 (+ 3 (+ 2 (+ 1 0)))); right: (+ (+ (+ (+ 4 0) 1) 2) 3).
            var previous = 0;
            for (var i = 1; i < 5; i++)
            {
                seeds.Add(Term(1, Seed(i), Seed(previous)));
                previous = seeds.Count - 1;
            }

            var left = previous;
            previous = 4;
            for (var i = 0; i < 4; i++)
            {
                seeds.Add(Term(1, Seed(previous), Seed(i)));
                previous = seeds.Count - 1;
            }

            var right = previous;

            // (= id (Add x y)) => (set (Add y x) id); (= id (Add x t)) (= t (Add y z)) => (set (Add (Add x y) z) id).
            var comm = new BridgeDiffRule("comm", [A, A, A],
                [new BridgeDiffAtom(1, [Var(0), Var(1), Var(2)], false)],
                [],
                [new BridgeDiffAction(BridgeDiffActionKind.Set, 1, default, [Var(1), Var(0), Var(2)], -1)],
                PlanStrategy.MinCover,
                false);
            var assoc = new BridgeDiffRule("assoc", [A, A, A, A, A, A],
                [new BridgeDiffAtom(1, [Var(0), Var(3), Var(4)], false), new BridgeDiffAtom(1, [Var(1), Var(2), Var(3)], false)],
                [],
                [
                    new BridgeDiffAction(BridgeDiffActionKind.Lookup, 1, default, [Var(0), Var(1)], 5),
                    new BridgeDiffAction(BridgeDiffActionKind.Set, 1, default, [Var(5), Var(2), Var(4)], -1),
                ],
                PlanStrategy.Gj,
                false);
            var program = Program(functions, [.. seeds], [comm, assoc], 30) with { RowCap = 100_000, MatchBudget = 1_000_000 };
            var stats = new BridgeDiffStats();
            var view = BridgeDiffRunner.Run(program, stats);

            Assert.Equal(1, stats.Saturated);
            Assert.Equal(view.Find(view.SeedId(left)), view.Find(view.SeedId(right)));
        }

        /// <summary>
        /// Transitive closure, Datalog-style, over a chain and a cycle: semi-naive evaluation must find every path naive
        /// evaluation finds, in as many iterations.
        /// </summary>
        [Fact]
        public void TransitiveClosureSaturates()
        {
            BridgeDiffFunction[] functions =
            [
                new("Node", BridgeDiffFunctionKind.Constructor, [I64], A, false),
                new("edge", BridgeDiffFunctionKind.Relation, [A, A], BridgeDiffSort.Unit, false),
                new("path", BridgeDiffFunctionKind.Relation, [A, A], BridgeDiffSort.Unit, false),
            ];
            var seeds = new List<BridgeDiffSeed>();
            for (var i = 0u; i < 8; i++)
                seeds.Add(Term(0, Int(i)));

            for (var i = 0; i < 7; i++)
                seeds.Add(Set(1, Seed(i), Seed(i + 1), Unit()));

            seeds.Add(Set(1, Seed(7), Seed(4), Unit()));

            // (edge x y) => (path x y); (path x y) (edge y z) => (path x z).
            var copy = new BridgeDiffRule("copy", [A, A],
                [new BridgeDiffAtom(1, [Var(0), Var(1), Unit()], false)],
                [],
                [new BridgeDiffAction(BridgeDiffActionKind.Set, 2, default, [Var(0), Var(1), Unit()], -1)],
                PlanStrategy.MinCover,
                false);
            var step = new BridgeDiffRule("step", [A, A, A],
                [new BridgeDiffAtom(2, [Var(0), Var(1), Unit()], false), new BridgeDiffAtom(1, [Var(1), Var(2), Unit()], false)],
                [],
                [new BridgeDiffAction(BridgeDiffActionKind.Set, 2, default, [Var(0), Var(2), Unit()], -1)],
                PlanStrategy.Gj,
                false);
            var stats = new BridgeDiffStats();
            var view = BridgeDiffRunner.Run(Program(functions, [.. seeds], [copy, step], 20), stats);

            // Nodes 0..3 reach every later node; nodes 4..7 reach 4..7.
            Assert.Equal(1, stats.Saturated);
            Assert.Equal(4 * 7 - 6 + 16, view.Rows(2).Count);
        }

        /// <summary>
        /// Shortest distances by a <c>min</c> lattice propagated along terms, with unions arriving between iterations, as
        /// later top-level commands would; one rule runs only every other iteration.
        /// </summary>
        [Fact]
        public void LatticePropagationAgreesUnderAPartialSchedule()
        {
            BridgeDiffFunction[] functions =
            [
                new("Leaf", BridgeDiffFunctionKind.Constructor, [I64], A, false),
                new("Succ", BridgeDiffFunctionKind.Constructor, [A], A, false),
                new("dist", BridgeDiffFunctionKind.Min, [A], I64, false),
            ];
            var seeds = new List<BridgeDiffSeed> { Term(0, Int(0)), Term(0, Int(1)) };
            for (var i = 0; i < 6; i++)
                seeds.Add(Term(1, Seed(seeds.Count - 1)));

            seeds.Add(Set(2, Seed(0), Int(0)));
            seeds.Add(Union(0, 4) with { Phase = 3 });

            // (= r (Succ x)) (= d (dist x)) => (set (dist r) (+ d 1)); (= x (Leaf k)) => (set (dist x) (+ k 10)).
            var succ = new BridgeDiffRule("succ", [A, A, I64, I64],
                [new BridgeDiffAtom(1, [Var(0), Var(1)], false), new BridgeDiffAtom(2, [Var(0), Var(2, I64)], false)],
                [],
                [
                    new BridgeDiffAction(BridgeDiffActionKind.Call, -1, BridgeDiffPrimitive.Add, [Var(2, I64), Int(1)], 3),
                    new BridgeDiffAction(BridgeDiffActionKind.Set, 2, default, [Var(1), Var(3, I64)], -1),
                ],
                PlanStrategy.PureSize,
                false);
            var leaf = new BridgeDiffRule("leaf", [I64, A, I64],
                [new BridgeDiffAtom(0, [Var(0, I64), Var(1)], false)],
                [],
                [
                    new BridgeDiffAction(BridgeDiffActionKind.Call, -1, BridgeDiffPrimitive.Add, [Var(0, I64), Int(10)], 2),
                    new BridgeDiffAction(BridgeDiffActionKind.Set, 2, default, [Var(1), Var(2, I64)], -1),
                ],
                PlanStrategy.MinCover,
                false);
            int[][] schedule = Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? new[] { 0, 1 } : [0]).ToArray();
            var program = Program(functions, [.. seeds], [succ, leaf], 12) with { Schedule = schedule };
            var view = BridgeDiffRunner.Run(program, new BridgeDiffStats());

            // Eight terms, seven classes once Leaf 0 and Succ^3(Leaf 1) merge, each with a distance.
            Assert.Equal(7, view.Rows(2).Count);
            Assert.Equal(0u, view.Rows(2).Single(r => r.Values[0] == view.Find(view.SeedId(0))).Values[1]);
        }

        /// <summary>
        /// The comparison accepts two views that differ only in how rule-minted ids are numbered, and rejects a merged
        /// class, a different output, a different subsumption flag, a missing row and a row left non-canonical.
        /// </summary>
        [Fact]
        public void TheComparisonIsModuloRenamingAndNothingElse()
        {
            BridgeDiffFunction[] functions =
            [
                new("Leaf", BridgeDiffFunctionKind.Constructor, [], A, true),
                new("F", BridgeDiffFunctionKind.Constructor, [A], A, true),
                new("G", BridgeDiffFunctionKind.Constructor, [A], A, false),
            ];
            var program = Program(functions, [Term(0)], [], 0);

            // Seeds minted id 0 (Leaf); rules minted F(0) and G(0), numbered 1 and 2 in one view, 2 and 1 in the other.
            BridgeDiffView View(uint[] find, uint f, uint g, bool subsumed = false, uint? fKey = null) => new(
                find,
                [[new BridgeDiffRow([0], false)], [new BridgeDiffRow([fKey ?? 0, f], subsumed)], [new BridgeDiffRow([0, g], false)]],
                [0],
                1);

            var expected = View([0, 1, 2], 1, 2);
            BridgeDiffComparer.Compare(program, expected, View([0, 1, 2], 2, 1), "a", "b", "test");
            BridgeDiffComparer.Compare(program, expected, expected, "a", "b", "test");

            void Rejects(BridgeDiffView actual) => Assert.Throws<BridgeDiffMismatchException>(() => BridgeDiffComparer.Compare(program, expected, actual, "a", "b", "test"));
            Rejects(View([0, 1, 1], 1, 1));
            Rejects(View([0, 1, 2], 1, 0));
            Rejects(View([0, 1, 2], 1, 2, subsumed: true));
            Rejects(View([0, 0, 2], 0, 2, fKey: 1));
            Rejects(new BridgeDiffView([0, 1, 2], [[new BridgeDiffRow([0], false)], [], [new BridgeDiffRow([0, 2], false)]], [0], 1));
        }

        static BridgeDiffView Run(BridgeDiffProgram program) => BridgeDiffRunner.Run(program, new BridgeDiffStats());

        static BridgeDiffProgram Program(BridgeDiffFunction[] functions, BridgeDiffSeed[] seeds, BridgeDiffRule[] rules, int iterations)
        {
            var all = Enumerable.Range(0, rules.Length).ToArray();
            return new BridgeDiffProgram(0, 1, functions, seeds, rules, Enumerable.Repeat(all, iterations).ToArray(), iterations, 10_000, 100_000, false);
        }

        static BridgeDiffSeed Term(int function, params BridgeDiffEntry[] args) => new(BridgeDiffSeedKind.Term, function, args, 0);

        static BridgeDiffSeed Set(int function, params BridgeDiffEntry[] row) => new(BridgeDiffSeedKind.Set, function, row, 0);

        static BridgeDiffSeed Union(int a, int b) => new(BridgeDiffSeedKind.Union, -1, [Seed(a), Seed(b)], 0);

        static BridgeDiffEntry Seed(int index) => BridgeDiffEntry.Variable(index, A);

        static BridgeDiffEntry Var(int index, int sort = A) => BridgeDiffEntry.Variable(index, sort);

        static BridgeDiffEntry Int(uint value) => BridgeDiffEntry.Const(value, I64);

        static BridgeDiffEntry Unit() => BridgeDiffEntry.Const(0, BridgeDiffSort.Unit);

    }

}
