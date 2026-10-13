using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Shrinks a failing bridge differential program: drops rules, seeds, rule parts and iterations, halves bulk loads,
    /// and simplifies the schedule and plan settings, keeping each change after which the program still fails with the
    /// same kind of exception, until nothing more helps or the budget (runs and time) is spent.
    /// </summary>
    /// <remarks>
    /// Only well-formed programs are tried: a seed is dropped only when no later seed names its class (later references
    /// are renumbered), and a rule part only when the rule stays well-formed
    /// (<see cref="BridgeDiffRule.IsWellFormed"/>). Id constants keep their numbers, so dropping a term seed may make
    /// one name another term or none; the failure decides whether that is kept.
    /// </remarks>
    static class BridgeDiffShrinker
    {

        /// <summary>
        /// Shrinks <paramref name="program"/>, which failed with <paramref name="failure"/>.
        /// </summary>
        /// <param name="program">The failing program.</param>
        /// <param name="failure">Its failure.</param>
        /// <param name="maxRuns">The most programs to try.</param>
        /// <param name="maxSeconds">The most time to spend.</param>
        /// <returns>The smallest failing program found and its failure.</returns>
        public static (BridgeDiffProgram Program, Exception Failure) Shrink(BridgeDiffProgram program, Exception failure, int maxRuns = 3000, int maxSeconds = 60)
        {
            var kind = failure.GetType();
            var clock = Stopwatch.StartNew();
            var runs = 0;
            bool Spent() => runs >= maxRuns || clock.Elapsed.TotalSeconds > maxSeconds;

            bool Try(BridgeDiffProgram candidate)
            {
                runs++;
                if (Fails(candidate, kind) is not { } f)
                    return false;

                (program, failure) = (candidate, f);
                return true;
            }

            var progress = true;
            while (progress && Spent() == false)
            {
                progress = false;

                for (var i = program.Rules.Length - 1; i >= 0 && Spent() == false; i--)
                    if (DropRule(program, i) is { } candidate)
                        progress |= Try(candidate);

                if (program.Schedule.Any(s => s.Length < program.Rules.Length) && Spent() == false)
                    progress |= Try(program with { Schedule = program.Schedule.Select(_ => Enumerable.Range(0, program.Rules.Length).ToArray()).ToArray() });

                for (var i = program.Seeds.Length - 1; i >= 0 && Spent() == false; i--)
                    if (DropSeed(program, i) is { } candidate)
                        progress |= Try(candidate);

                while (program.Iterations > 1 && Spent() == false && Try(program with { Iterations = program.Iterations - 1 }))
                    progress = true;

                for (var i = 0; i < program.Seeds.Length && Spent() == false; i++)
                {
                    var seed = program.Seeds[i];
                    if (seed.Kind == BridgeDiffSeedKind.Bulk && seed.Args[1].Value > 1)
                    {
                        var halved = seed with { Args = [seed.Args[0], BridgeDiffEntry.Const(seed.Args[1].Value / 2, BridgeDiffSort.I64)] };
                        progress |= Try(program with { Seeds = program.Seeds.Select((s, j) => j == i ? halved : s).ToArray() });
                    }
                }

                for (var r = 0; r < program.Rules.Length && Spent() == false; r++)
                {
                    foreach (var simpler in Simplify(program.Rules[r]))
                    {
                        if (Spent())
                            break;

                        if (simpler.IsWellFormed() && Try(program with { Rules = program.Rules.Select((x, j) => j == r ? simpler : x).ToArray() }))
                        {
                            progress = true;
                            break;
                        }
                    }
                }

                if (program.Parallel && Spent() == false)
                    progress |= Try(program with { Parallel = false });
            }

            return (program, failure);
        }

        /// <summary>
        /// Runs <paramref name="candidate"/> and returns its failure if it fails with an exception of
        /// <paramref name="kind"/>.
        /// </summary>
        static Exception? Fails(BridgeDiffProgram candidate, Type kind)
        {
            try
            {
                BridgeDiffRunner.Run(candidate, new BridgeDiffStats());
                return null;
            }
            catch (Exception e) when (e.GetType() == kind)
            {
                return e;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Drops rule <paramref name="index"/>, renumbering the schedule, or returns <c>null</c> when an iteration runs
        /// only that rule.
        /// </summary>
        static BridgeDiffProgram? DropRule(BridgeDiffProgram program, int index)
        {
            var schedule = program.Schedule.Select(step => step.Where(r => r != index).Select(r => r > index ? r - 1 : r).ToArray()).ToArray();
            return schedule.Take(program.Iterations).Any(step => step.Length == 0) ? null : program with { Rules = Without(program.Rules, index), Schedule = schedule };
        }

        /// <summary>
        /// Drops seed <paramref name="index"/>, renumbering later references, or returns <c>null</c> when a later seed
        /// names its class.
        /// </summary>
        static BridgeDiffProgram? DropSeed(BridgeDiffProgram program, int index)
        {
            var seeds = new List<BridgeDiffSeed>();
            for (var i = 0; i < program.Seeds.Length; i++)
            {
                if (i == index)
                    continue;

                var seed = program.Seeds[i];
                var args = new BridgeDiffEntry[seed.Args.Length];
                for (var a = 0; a < args.Length; a++)
                {
                    var arg = seed.Args[a];
                    if (arg.IsVar && arg.Var == index)
                        return null;

                    args[a] = arg.IsVar && arg.Var > index ? BridgeDiffEntry.Variable(arg.Var - 1, arg.Sort) : arg;
                }

                seeds.Add(seed with { Args = args });
            }

            return program with { Seeds = seeds.ToArray() };
        }

        /// <summary>
        /// Lists simpler versions of a rule: without one action, primitive or atom; with every atom matching rows that
        /// are not subsumed; with the default plan settings.
        /// </summary>
        static IEnumerable<BridgeDiffRule> Simplify(BridgeDiffRule rule)
        {
            for (var i = rule.Actions.Length - 1; i >= 0; i--)
                yield return rule with { Actions = Without(rule.Actions, i) };

            for (var i = rule.Prims.Length - 1; i >= 0; i--)
                yield return rule with { Prims = Without(rule.Prims, i) };

            for (var i = rule.Atoms.Length - 1; i >= 0; i--)
                yield return rule with { Atoms = Without(rule.Atoms, i) };

            if (rule.Atoms.Any(a => a.Subsumed != false))
                yield return rule with { Atoms = rule.Atoms.Select(a => a with { Subsumed = false }).ToArray() };

            if (rule.NoDecomposition)
                yield return rule with { NoDecomposition = false };

            if (rule.Strategy != PlanStrategy.Gj)
                yield return rule with { Strategy = PlanStrategy.Gj };
        }

        /// <summary>
        /// Copies an array without element <paramref name="index"/>.
        /// </summary>
        static T[] Without<T>(T[] items, int index) => items.Where((_, j) => j != index).ToArray();

    }

}
