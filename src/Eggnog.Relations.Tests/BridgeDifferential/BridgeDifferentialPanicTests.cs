using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Random programs whose rules read functions without a default at keys no atom guarantees, so they may panic
    /// (egglog's <c>lookup</c> of a custom function, LookupWithFallback with a lazy panic). Which iteration panics first
    /// does not depend on the evaluation: a match whose lookup misses is new the first time it exists, so the semi-naive
    /// bridge, the naive bridge and the oracle must all panic in the same iteration, and agree before it.
    /// </summary>
    public class BridgeDifferentialPanicTests
    {

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="output">The test output.</param>
        public BridgeDifferentialPanicTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// The three evaluations panic in the same iteration.
        /// </summary>
        [Fact]
        public void PanicsHappenInTheSameIteration()
        {
            var failures = new List<string>();
            var panicked = 0;
            for (var seed = 1; seed <= 3000 && failures.Count < 3; seed++)
            {
                var program = AddRiskyReads(BridgeDiffGenerator.Generate(seed), new Random(seed));
                if (program is null)
                    continue;

                var (failure, didPanic) = Run(program);
                panicked += didPanic ? 1 : 0;
                if (failure is not null)
                    failures.Add($"seed {seed}: {failure}\n{program}");
            }

            output.WriteLine($"{panicked} programs panicked");
            Assert.True(failures.Count == 0, string.Join("\n\n", failures));
            Assert.True(panicked > 100, $"only {panicked} programs panicked");
        }

        /// <summary>
        /// Appends to one rule a read of a function without a default at bound variables of its key sorts.
        /// </summary>
        static BridgeDiffProgram? AddRiskyReads(BridgeDiffProgram program, Random random)
        {
            var readable = Enumerable.Range(0, program.Functions.Length).Where(f => program.Functions[f].HasDefault == false).ToList();
            if (readable.Count == 0 || program.Rules.Length == 0)
                return null;

            var r = random.Next(program.Rules.Length);
            var rule = program.Rules[r];
            var f = readable[random.Next(readable.Count)];
            var function = program.Functions[f];
            var keys = new BridgeDiffEntry[function.KeyCount];
            for (var k = 0; k < keys.Length; k++)
            {
                var sort = function.Keys[k];
                var candidates = rule.Atoms.SelectMany(a => a.Entries).Where(e => e.IsVar && rule.VarSorts[e.Var] == sort).Select(e => e.Var).Distinct().ToList();
                if (candidates.Count == 0)
                    return null;

                keys[k] = BridgeDiffEntry.Variable(candidates[random.Next(candidates.Count)], sort);
            }

            var varSorts = rule.VarSorts.Append(function.Output).ToArray();
            var read = new BridgeDiffAction(BridgeDiffActionKind.Lookup, f, default, keys, varSorts.Length - 1);
            var rules = program.Rules.ToArray();
            rules[r] = rule with { VarSorts = varSorts, Actions = [.. rule.Actions, read] };
            return program with { Rules = rules };
        }

        /// <summary>
        /// Runs the program in lockstep and returns the first disagreement, and whether it panicked.
        /// </summary>
        static (string? Failure, bool Panicked) Run(BridgeDiffProgram program)
        {
            var semi = new BridgeDiffBridge(program, semiNaive: true, program.Parallel);
            var naive = new BridgeDiffBridge(program, semiNaive: false, parallel: false);
            var oracle = new BridgeDiffOracle(program, semi.Unit);
            semi.RunSeeds(0);
            naive.RunSeeds(0);
            oracle.RunSeeds(0);
            semi.BuildRules();
            naive.BuildRules();
            for (var iteration = 1; iteration <= program.Iterations; iteration++)
            {
                if (program.Seeds.Any(s => s.Phase == iteration))
                {
                    semi.RunSeeds(iteration);
                    naive.RunSeeds(iteration);
                    oracle.RunSeeds(iteration);
                }

                if (oracle.TotalRows > program.RowCap)
                    return (null, false);

                var scheduled = program.Schedule[iteration - 1];
                bool? changed;
                var oraclePanicked = false;
                try
                {
                    changed = oracle.Step(scheduled);
                }
                catch (InvalidOperationException)
                {
                    changed = false;
                    oraclePanicked = true;
                }

                if (changed is null)
                    return (null, false);

                var semiPanicked = Panics(() => semi.Step(scheduled));
                var naivePanicked = Panics(() => naive.Step(scheduled));
                if (semiPanicked != oraclePanicked || naivePanicked != oraclePanicked)
                    return ($"iteration {iteration}: the oracle {(oraclePanicked ? "panicked" : "did not panic")}, the semi-naive bridge {(semiPanicked ? "panicked" : "did not")}, the naive bridge {(naivePanicked ? "panicked" : "did not")}", oraclePanicked);

                if (oraclePanicked)
                {
                    // What the panicking iteration merged is kept, rebuilt: each e-graph is still canonical.
                    try
                    {
                        var s = semi.View();
                        var n = naive.View();
                        BridgeDiffComparer.Compare(program, s, s, "the semi-naive bridge", "itself", $"after the panic in iteration {iteration}");
                        BridgeDiffComparer.Compare(program, n, n, "the naive bridge", "itself", $"after the panic in iteration {iteration}");
                    }
                    catch (BridgeDiffMismatchException e)
                    {
                        return (e.Message, true);
                    }

                    return (null, true);
                }

                try
                {
                    var expected = oracle.View();
                    BridgeDiffComparer.Compare(program, expected, semi.View(), "the oracle", "the semi-naive bridge", $"after iteration {iteration}");
                    BridgeDiffComparer.Compare(program, expected, naive.View(), "the oracle", "the naive bridge", $"after iteration {iteration}");
                }
                catch (BridgeDiffMismatchException e)
                {
                    return (e.Message, false);
                }
            }

            return (null, false);
        }

        /// <summary>
        /// Runs <paramref name="step"/> and returns whether it threw a <see cref="PanicException"/>.
        /// </summary>
        static bool Panics(Func<IterationReport> step)
        {
            try
            {
                step();
                return false;
            }
            catch (PanicException)
            {
                return true;
            }
        }

    }

}
