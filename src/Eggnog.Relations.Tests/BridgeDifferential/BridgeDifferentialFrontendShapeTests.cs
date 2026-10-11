using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Runs the random bridge differential programs with their functions declared as egglog's frontend declares them
    /// (egglog/src/lib.rs:789-850, egglog/src/ast/desugar.rs:396-427): every constructor can subsume, and a relation is a
    /// constructor into a fresh, never-unioned sort (fresh-id default, union-id merge, subsumable), inserted by a lookup.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class BridgeDifferentialFrontendShapeTests
    {

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="output">The test output.</param>
        public BridgeDifferentialFrontendShapeTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// The random programs, with every constructor able to subsume, as every egglog constructor is.
        /// </summary>
        [Fact]
        public void RandomProgramsAgreeWithEveryConstructorSubsumable()
        {
            var stats = RunSeeds(1, 1500, AllConstructorsSubsume);
            output.WriteLine(stats.ToString());
        }

        /// <summary>
        /// The random programs, with relations declared as egglog's frontend desugars them: constructors into a fresh
        /// sort, subsumable, whose rows are inserted by lookups (which mint the row's id).
        /// </summary>
        [Fact]
        public void RandomProgramsAgreeWithRelationsAsConstructors()
        {
            var stats = RunSeeds(1, 1500, p => RelationsAsConstructors(AllConstructorsSubsume(p)));
            output.WriteLine(stats.ToString());
            Assert.True(stats.RelationRows == 0, "relations were not rewritten: " + stats);
        }

        /// <summary>
        /// Runs the programs of seeds <paramref name="first"/> onwards, transformed, and fails with the smallest failing
        /// seed's program and failure.
        /// </summary>
        static BridgeDiffStats RunSeeds(int first, int count, Func<BridgeDiffProgram, BridgeDiffProgram> transform)
        {
            var stats = new BridgeDiffStats();
            var failures = new ConcurrentBag<(int Seed, Exception Failure)>();
            var stop = 0;
            Parallel.For(first, first + count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, seed =>
            {
                if (Volatile.Read(ref stop) != 0)
                    return;

                var local = new BridgeDiffStats();
                try
                {
                    BridgeDiffRunner.Run(transform(BridgeDiffGenerator.Generate(seed)), local);
                }
                catch (Exception e)
                {
                    failures.Add((seed, e));
                    Volatile.Write(ref stop, 1);
                }

                stats.Add(local);
            });

            if (failures.IsEmpty == false)
            {
                var (seed, failure) = failures.MinBy(f => f.Seed);
                var program = transform(BridgeDiffGenerator.Generate(seed));
                var (shrunk, shrunkFailure) = BridgeDiffShrinker.Shrink(program, failure);
                Assert.Fail($"seed {seed}: {(shrunkFailure is BridgeDiffMismatchException ? shrunkFailure.Message : shrunkFailure.ToString())}\nShrunk program:\n{shrunk}");
            }

            return stats;
        }

        /// <summary>
        /// Makes every constructor subsumable; atoms keep matching rows that are not subsumed.
        /// </summary>
        internal static BridgeDiffProgram AllConstructorsSubsume(BridgeDiffProgram program)
        {
            var functions = program.Functions.Select(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.CanSubsume == false
                ? new BridgeDiffFunction(f.Name, f.Kind, f.Keys, f.Output, true)
                : f).ToArray();
            return program with { Functions = functions };
        }

        /// <summary>
        /// Rewrites every relation into an explicit constructor into a fresh sort of its own: an atom's output variable
        /// takes that sort (an output it does not name becomes a fresh variable of it), an insertion becomes a lookup, a
        /// lookup's result takes the sort, and a fact seed that inserts becomes a term seed. The harness declares
        /// relations this way already (<see cref="BridgeDiffFunctionKind.Relation"/>), and a relation insertion mints its
        /// row's id as a term does, so the seeds' ids keep their numbers; the rewrite checks that the relation kind and
        /// its explicit desugaring agree.
        /// </summary>
        internal static BridgeDiffProgram RelationsAsConstructors(BridgeDiffProgram program)
        {
            var functions = program.Functions.ToArray();
            var relationSort = new int[functions.Length];
            var sortCount = program.SortCount;
            for (var f = 0; f < functions.Length; f++)
            {
                relationSort[f] = -1;
                var function = functions[f];
                if (function.Kind != BridgeDiffFunctionKind.Relation)
                    continue;

                relationSort[f] = sortCount++;
                functions[f] = new BridgeDiffFunction(function.Name, BridgeDiffFunctionKind.Constructor, function.Keys, relationSort[f], true);
            }

            // Insertions into relations become term seeds, which mint one id each, as the insertions did.
            var seeds = new BridgeDiffSeed[program.Seeds.Length];
            for (var i = 0; i < seeds.Length; i++)
            {
                var seed = program.Seeds[i];
                if (seed.Kind == BridgeDiffSeedKind.Set && relationSort[seed.Function] >= 0 && seed.IsInsertion(program.Functions))
                    seed = new BridgeDiffSeed(BridgeDiffSeedKind.Term, seed.Function, seed.Args.Take(functions[seed.Function].KeyCount).ToArray(), seed.Phase);

                seeds[i] = seed;
            }

            var rules = program.Rules.Select(r => Rewrite(r, program.Functions, functions, relationSort)).ToArray();
            return program with { SortCount = sortCount, Functions = functions, Seeds = seeds, Rules = rules };
        }

        /// <summary>
        /// Rewrites one rule for <see cref="RelationsAsConstructors"/>.
        /// </summary>
        static BridgeDiffRule Rewrite(BridgeDiffRule rule, BridgeDiffFunction[] original, BridgeDiffFunction[] functions, int[] relationSort)
        {
            var varSorts = rule.VarSorts.ToList();
            int Fresh(int sort)
            {
                varSorts.Add(sort);
                return varSorts.Count - 1;
            }

            BridgeDiffEntry Retype(BridgeDiffEntry e, int sort)
            {
                if (e.IsVar == false)
                    return BridgeDiffEntry.Variable(Fresh(sort), sort);

                varSorts[e.Var] = sort;
                return BridgeDiffEntry.Variable(e.Var, sort);
            }

            var atoms = rule.Atoms.Select(atom =>
            {
                if (relationSort[atom.Function] < 0)
                    return atom;

                var entries = atom.Entries.ToArray();
                entries[^1] = Retype(entries[^1], relationSort[atom.Function]);
                return atom with { Entries = entries };
            }).ToArray();

            var actions = rule.Actions.Select(action =>
            {
                if (action.Function < 0 || relationSort[action.Function] < 0)
                    return action;

                var sort = relationSort[action.Function];
                var keys = action.Entries.Take(functions[action.Function].KeyCount).ToArray();
                return action.Kind switch
                {
                    BridgeDiffActionKind.Set when original[action.Function].IsNoOutput(action.Entries[^1]) => new BridgeDiffAction(BridgeDiffActionKind.Lookup, action.Function, default, keys, Fresh(sort)),
                    BridgeDiffActionKind.Set => action with { Entries = [.. keys, Retype(action.Entries[^1], sort)] },
                    BridgeDiffActionKind.Lookup => action with { Result = Retype(BridgeDiffEntry.Variable(action.Result, BridgeDiffSort.Unit), sort).Var },
                    _ => action,
                };
            }).ToArray();

            return rule with { VarSorts = varSorts.ToArray(), Atoms = atoms, Actions = actions };
        }

    }

}
