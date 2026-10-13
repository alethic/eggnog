using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// More of egglog's test programs (egglog/tests/*.egg) through the three-way harness, with egglog's answers checked on
    /// top: queries on relation outputs, and global lattices that must saturate.
    /// </summary>
    public class BridgeDifferentialEggProgramTests
    {

        const int I64 = BridgeDiffSort.I64;
        const int Unit = BridgeDiffSort.Unit;

        /// <summary>
        /// egglog's tests/relation-query-allowed.egg with relations declared as the harness declares them
        /// (<see cref="BridgeDiffFunctionKind.Relation"/>): <c>(= (myrel 1) (myrel 2))</c> must not match, because egglog
        /// desugars a relation into a constructor into a fresh sort (egglog/src/ast/desugar.rs:396-427), so each row has an
        /// id of its own. The harness once gave relations a unit output with the assert-eq merge, under which every row had
        /// the same output and the rule fired in all three evaluations; it now declares them as egglog does.
        /// </summary>
        [Fact]
        public void RelationOutputsAreDistinctAsTheHarnessDeclaresRelations()
        {
            BridgeDiffFunction[] functions =
            [
                new("myrel", BridgeDiffFunctionKind.Relation, [I64], Unit, true),
                new("shouldappear", BridgeDiffFunctionKind.Relation, [], Unit, true),
                new("shouldnotappear", BridgeDiffFunctionKind.Relation, [], Unit, true),
            ];
            BridgeDiffSeed[] seeds =
            [
                new(BridgeDiffSeedKind.Set, 0, [Int(1), BridgeDiffEntry.Const(0, Unit)], 0),
                new(BridgeDiffSeedKind.Set, 0, [Int(2), BridgeDiffEntry.Const(0, Unit)], 0),
            ];
            var view = Run(functions, seeds, Unit);
            Assert.Single(view.Rows(1));
            Assert.Empty(view.Rows(2));
        }

        /// <summary>
        /// The same program with relations declared as egglog's frontend declares them: constructors into fresh sorts,
        /// inserted by lookups.
        /// </summary>
        [Fact]
        public void RelationOutputsAreDistinctWithRelationsAsConstructors()
        {
            BridgeDiffFunction[] functions =
            [
                new("myrel", BridgeDiffFunctionKind.Constructor, [I64], 0, true),
                new("shouldappear", BridgeDiffFunctionKind.Constructor, [], 1, true),
                new("shouldnotappear", BridgeDiffFunctionKind.Constructor, [], 2, true),
            ];
            BridgeDiffSeed[] seeds =
            [
                new(BridgeDiffSeedKind.Term, 0, [Int(1)], 0),
                new(BridgeDiffSeedKind.Term, 0, [Int(2)], 0),
            ];
            var view = Run(functions, seeds, 0);
            Assert.Single(view.Rows(1));
            Assert.Empty(view.Rows(2));
        }

        /// <summary>
        /// egglog's tests/repro-should-saturate.egg and merge-saturates.egg: a global min lattice whose rule writes values
        /// the merge ignores changes nothing, so the first iteration reports no change and the schedule saturates.
        /// </summary>
        [Fact]
        public void GlobalLatticeWritesThatChangeNothingSaturate()
        {
            BridgeDiffFunction[] functions = [new("MyMap", BridgeDiffFunctionKind.Min, [], I64, false)];
            BridgeDiffSeed[] seeds = [new(BridgeDiffSeedKind.Set, 0, [Int(1)], 0)];

            // (rule ((MyMap)) ((set (MyMap) 1) (set (MyMap) 2)))
            var rule = new BridgeDiffRule("r0", [I64],
                [new BridgeDiffAtom(0, [BridgeDiffEntry.Variable(0, I64)], false)],
                [],
                [new BridgeDiffAction(BridgeDiffActionKind.Set, 0, default, [Int(1)], -1), new BridgeDiffAction(BridgeDiffActionKind.Set, 0, default, [Int(2)], -1)],
                PlanStrategy.Gj, false);
            var stats = new BridgeDiffStats();
            var view = BridgeDiffRunner.Run(new BridgeDiffProgram(0, 0, functions, seeds, [rule], Enumerable.Repeat(new[] { 0 }, 100).ToArray(), 100, 10_000, 100_000, false), stats);
            Assert.Equal(1, stats.Saturated);
            Assert.Equal(0, stats.ChangedIterations);
            Assert.Equal(1u, Assert.Single(view.Rows(0)).Values[0]);
        }

        /// <summary>
        /// egglog's tests/repro-equal-constant.egg and repro-equal-constant2.egg: an atom that compares a global lattice's
        /// value to a constant, <c>(= (foo) 5)</c>, matches only that value.
        /// </summary>
        [Theory]
        [InlineData(5u, 10u)]
        [InlineData(10u, 4u)]
        public void GlobalLatticeComparedToAConstant(uint constant, uint expected)
        {
            BridgeDiffFunction[] functions = [new("foo", BridgeDiffFunctionKind.Min, [], I64, false)];
            BridgeDiffSeed[] seeds = [new(BridgeDiffSeedKind.Set, 0, [Int(10)], 0)];

            // (rule ((= (foo) c)) ((set (foo) 4)))
            var rule = new BridgeDiffRule("r0", [],
                [new BridgeDiffAtom(0, [Int(constant)], false)],
                [],
                [new BridgeDiffAction(BridgeDiffActionKind.Set, 0, default, [Int(4)], -1)],
                PlanStrategy.Gj, false);
            var view = BridgeDiffRunner.Run(new BridgeDiffProgram(0, 0, functions, seeds, [rule], Enumerable.Repeat(new[] { 0 }, 3).ToArray(), 3, 10_000, 100_000, false), new BridgeDiffStats());
            Assert.Equal(expected, Assert.Single(view.Rows(0)).Values[0]);
        }

        /// <summary>
        /// Runs relation-query-allowed.egg's rules for one iteration: <c>(= (myrel 1) (myrel 1)) =&gt; (shouldappear)</c>
        /// and <c>(= (myrel 1) (myrel 2)) =&gt; (shouldnotappear)</c>, the output variable of sort
        /// <paramref name="outputSort"/>.
        /// </summary>
        static BridgeDiffView Run(BridgeDiffFunction[] functions, BridgeDiffSeed[] seeds, int outputSort)
        {
            BridgeDiffRule Rule(string name, uint other, int function)
            {
                var result = functions[function].Output;
                return new BridgeDiffRule(name, [outputSort, result],
                    [
                        new BridgeDiffAtom(0, [Int(1), BridgeDiffEntry.Variable(0, outputSort)], false),
                        new BridgeDiffAtom(0, [Int(other), BridgeDiffEntry.Variable(0, outputSort)], false),
                    ],
                    [],
                    [new BridgeDiffAction(BridgeDiffActionKind.Lookup, function, default, [], 1)],
                    PlanStrategy.Gj, false);
            }

            var rules = new[] { Rule("appear", 1, 1), Rule("notappear", 2, 2) };
            var program = new BridgeDiffProgram(0, 3, functions, seeds, rules, [[0, 1]], 1, 10_000, 100_000, false);
            return BridgeDiffRunner.Run(program, new BridgeDiffStats());
        }

        static BridgeDiffEntry Int(uint value) => BridgeDiffEntry.Const(value, I64);

    }

}
