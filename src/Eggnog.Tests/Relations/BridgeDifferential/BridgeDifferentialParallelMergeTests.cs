using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A bulk program whose rule runs stage more than 20,000 rows into one table with conflicting keys, so the
    /// semi-naive e-graph's parallel insert has to merge, with lattice merges and subsumption that change the stored
    /// rows, and later with merges that change nothing. These are the cases egglog's parallel insert gets wrong: it
    /// discards the merge's output and reports changes that did not happen (core-relations/src/table/mod.rs:871,
    /// 902-907). The random bulk programs never reach them: their only insert above the threshold is the conflict-free
    /// bulk load.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class BridgeDifferentialParallelMergeTests
    {

        const int A = 0;
        const int I64 = BridgeDiffSort.I64;
        const int Unit = BridgeDiffSort.Unit;

        /// <summary>
        /// Parallel inserts that merge agree with the serial naive e-graph and the oracle, change flags included.
        /// </summary>
        [Fact]
        public void ParallelInsertsThatMergeAgree()
        {
            BridgeDiffFunction[] functions =
            [
                new("NumA", BridgeDiffFunctionKind.Constructor, [I64], A, true),
                new("lo", BridgeDiffFunctionKind.Min, [A], I64, false),
                new("hi", BridgeDiffFunctionKind.Max, [A], I64, false),
                new("R", BridgeDiffFunctionKind.Relation, [A], Unit, true),
            ];
            BridgeDiffSeed[] seeds = [new(BridgeDiffSeedKind.Bulk, 0, [Int(1000), Int(21_000)], 0)];

            // Every rule matches (= x (NumA k)), subsumed or not: v0 = k, v1 = x, v2 = k + 5.
            // r0, r1: the same keys with different values, in one run, so one insert merges conflicting rows.
            var r0 = Rule("r0",
                new BridgeDiffAction(BridgeDiffActionKind.Call, -1, BridgeDiffPrimitive.Add, [V(0, I64), Int(5)], 2),
                Set(1, V(2, I64)),
                Set(2, V(0, I64)));
            var r1 = Rule("r1",
                new BridgeDiffAction(BridgeDiffActionKind.Call, -1, BridgeDiffPrimitive.Add, [V(0, I64), Int(5)], 2),
                Set(1, V(0, I64)),
                Set(2, V(2, I64)));

            // r2: subsume every leaf (merges that change only the subsumption cell) and insert every R row subsumed.
            var r2 = Rule("r2",
                new BridgeDiffAction(BridgeDiffActionKind.Subsume, 0, default, [V(0, I64)], -1),
                new BridgeDiffAction(BridgeDiffActionKind.Subsume, 3, default, [V(1)], -1));

            // r3: lower and raise every lattice value, and write R rows that are not subsumed (no change).
            var r3 = Rule("r3",
                Set(1, Int(1)),
                Set(2, Int(5_000_000)),
                new BridgeDiffAction(BridgeDiffActionKind.Set, 3, default, [V(1), BridgeDiffEntry.Const(0, Unit)], -1));

            // The last two iterations re-run everything: the semi-naive rules re-match the re-stamped leaves, and their
            // 84,000 writes change nothing.
            int[][] schedule = [[0, 1], [2], [3], [0, 1, 2, 3], [0, 1, 2, 3]];
            var program = new BridgeDiffProgram(0, 1, functions, seeds, [r0, r1, r2, r3], schedule, schedule.Length, 1_000_000, 1_000_000, true);

            var stats = new BridgeDiffStats();
            BridgeDiffRunner.Run(program, stats);
            Assert.Equal(1, stats.Saturated);
            Assert.Equal(3, stats.ChangedIterations);
        }

        static BridgeDiffRule Rule(string name, params BridgeDiffAction[] actions)
        {
            return new BridgeDiffRule(name, [I64, A, I64], [new BridgeDiffAtom(0, [V(0, I64), V(1)], null)], [], actions, PlanStrategy.Gj, false);
        }

        static BridgeDiffAction Set(int function, BridgeDiffEntry value) => new(BridgeDiffActionKind.Set, function, default, [V(1), value], -1);

        static BridgeDiffEntry Int(uint value) => BridgeDiffEntry.Const(value, I64);

        static BridgeDiffEntry V(int index, int sort = A) => BridgeDiffEntry.Variable(index, sort);

    }

}
