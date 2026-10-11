using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Generates random bridge differential programs (<see cref="BridgeDiffProgram"/>) from a seed: a random signature,
    /// seed terms and facts, and rules (<see cref="BridgeDiffRuleDraft"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signature has one to three eq sorts, each with a leaf constructor (<c>NumA</c> over an integer, or a nullary
    /// <c>LeafA</c>) so terms of every sort can be built; constructors over the sorts (sometimes over an integer too,
    /// occasionally with four or five keys; in half the signatures two binary operators over the first sort); up to two
    /// relations, up to two integer lattices merged by <c>min</c> or <c>max</c>, and sometimes a function to an eq sort
    /// merged by union. Relations, lattices and id functions sometimes have no keys, as egglog's globals. As in egglog,
    /// every constructor and relation can subsume (egglog/src/lib.rs:806-809), except for one constructor in eight, the
    /// shape of egglog-bridge's own tests without subsumption.
    /// </para>
    /// <para>
    /// The seeds build four to twenty-four terms bottom-up and interleave up to twelve facts: relation rows, lattice
    /// values, id-function and constructor rows over existing classes, and unions. One program in four also has a few
    /// seeds that run between iterations, as later top-level commands would. Programs have one to seven rules, two in three
    /// from templates of common egglog rules (<see cref="BridgeDiffRuleDraft"/>), and run two to ten iterations, or until
    /// they saturate, outgrow <see cref="RowCap"/> rows or panic. Two programs in five run a random subset of their rules
    /// in each iteration, as a schedule of rule sets does, so a rule's last run falls behind the others'. One program in
    /// eight runs with parallel settings, which force the parallel rule-set executor.
    /// </para>
    /// <para>
    /// Bulk programs (<c>bulk</c>) first load 20,500 to 26,000 integer leaves of the first sort in one call, so the
    /// tables cross the engine's size thresholds: rebuilds take the incremental path (a table of more than 10,000 rows
    /// and few unions), and with parallel settings, which half of them use, inserts, index builds and rebuilds of more
    /// than 10,000 or 20,000 rows run in parallel. Their first rule is a chain of unions between leaves; two or three
    /// rules then write a lattice value of every leaf, and often insert a relation row for it or subsume it
    /// (<see cref="BridgeDiffRuleDraft.GenerateBulkWrite"/>), so that one insert merges more than 20,000 conflicting rows
    /// and later ones re-confirm them without change; up to two random rules follow. The first iteration runs every rule,
    /// and programs run two to five iterations.
    /// </para>
    /// </remarks>
    static class BridgeDiffGenerator
    {

        /// <summary>
        /// The number of rows past which no further iteration runs.
        /// </summary>
        public const int RowCap = 500;

        /// <summary>
        /// The number of rows past which no further iteration of a bulk program runs.
        /// </summary>
        public const int BulkRowCap = 160_000;

        /// <summary>
        /// The most matches the oracle enumerates in one iteration.
        /// </summary>
        public const int MatchBudget = 40_000;

        /// <summary>
        /// The most matches the oracle enumerates in one iteration of a bulk program.
        /// </summary>
        public const int BulkMatchBudget = 400_000;

        /// <summary>
        /// Generates the program of <paramref name="seed"/>.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <param name="bulk">Whether to generate a bulk program.</param>
        /// <returns>The program.</returns>
        public static BridgeDiffProgram Generate(int seed, bool bulk = false)
        {
            var random = new Random(seed);
            var sortCount = 1 + random.Next(3);
            var functions = GenerateSignature(random, sortCount, bulk);
            var iterations = bulk ? 2 + random.Next(4) : 2 + random.Next(9);
            var seeds = GenerateSeeds(random, functions, sortCount, iterations, bulk);

            // The seeds before the rules mint ids 0, 1, ... in order (one per term and relation insertion, one per bulk
            // row); atoms may name the terms' ids as constants.
            var constants = new List<(uint Id, int Sort)>();
            var minted = 0u;
            foreach (var s in seeds.Where(s => s.Phase == 0))
            {
                if (s.Kind == BridgeDiffSeedKind.Term)
                    constants.Add((minted, functions[s.Function].Output));

                minted += (uint)s.MintedIds(functions);
            }

            var rules = new List<BridgeDiffRule>();
            var ruleCount = 1 + random.Next(7);
            if (bulk)
            {
                rules.Add(new BridgeDiffRuleDraft(random, functions, sortCount, constants).GenerateChain("r0"));
                var writes = 2 + random.Next(2);
                for (var i = 0; i < writes; i++)
                    rules.Add(new BridgeDiffRuleDraft(random, functions, sortCount, constants).GenerateBulkWrite("r" + rules.Count));

                ruleCount = rules.Count + random.Next(3);
            }

            while (rules.Count < ruleCount)
                rules.Add(new BridgeDiffRuleDraft(random, functions, sortCount, constants).Generate("r" + rules.Count));

            // Two programs in five run a random subset of the rules in each iteration, so rules fall behind one another;
            // a bulk program runs every rule in its first iteration, so its writes conflict in one insert.
            var all = Enumerable.Range(0, rules.Count).ToArray();
            var partial = rules.Count > 1 && random.Next(5) < 2;
            var schedule = new int[iterations][];
            for (var i = 0; i < iterations; i++)
            {
                schedule[i] = partial && (bulk == false || i > 0) ? all.Where(_ => random.Next(5) < 3).ToArray() : all;
                if (schedule[i].Length == 0)
                    schedule[i] = [random.Next(rules.Count)];
            }

            var parallel = bulk ? random.Next(2) == 0 : random.Next(8) == 0;
            return new BridgeDiffProgram(seed, sortCount, functions, seeds, rules.ToArray(), schedule, iterations, bulk ? BulkRowCap : RowCap, bulk ? BulkMatchBudget : MatchBudget, parallel);
        }

        /// <summary>
        /// Generates the functions. A bulk program's first function is the integer leaf of the first sort, and it has a
        /// lattice and a relation over that sort.
        /// </summary>
        static BridgeDiffFunction[] GenerateSignature(Random random, int sortCount, bool bulk)
        {
            var functions = new List<BridgeDiffFunction>();
            for (var sort = 0; sort < sortCount; sort++)
            {
                // A bulk program loads integer leaves of the first sort, its first function.
                var name = BridgeDiffSort.Name(sort);
                var numeric = random.Next(3) > 0 || (bulk && sort == 0);
                var leaves = random.Next(4) == 0 ? 2 : 1;
                for (var leaf = 0; leaf < leaves; leaf++, numeric = numeric == false)
                {
                    functions.Add(numeric
                        ? new BridgeDiffFunction("Num" + name, BridgeDiffFunctionKind.Constructor, [BridgeDiffSort.I64], sort, Subsumable(random))
                        : new BridgeDiffFunction("Leaf" + name, BridgeDiffFunctionKind.Constructor, [], sort, Subsumable(random)));
                }
            }

            // Half the signatures have an algebra over the first sort, two binary operators, which the commutativity,
            // associativity, distribution and folding templates rewrite (the shape of egglog's math tests).
            var operators = random.Next(1, 4) + sortCount - 1;
            var algebra = random.Next(2) == 0 ? 2 : 0;
            for (var i = 0; i < algebra; i++)
                functions.Add(new BridgeDiffFunction("Op" + i, BridgeDiffFunctionKind.Constructor, [0, 0], 0, Subsumable(random)));

            for (var i = algebra; i < operators + algebra; i++)
            {
                // Mostly unary and binary; occasionally as wide as egglog's (Fma ty a b c) or (If ty c t e).
                var arity = random.Next(40) switch
                {
                    < 12 => 1,
                    < 33 => 2,
                    < 37 => 3,
                    < 39 => 4,
                    _ => 5,
                };
                var keys = Enumerable.Range(0, arity).Select(_ => random.Next(10) == 0 ? BridgeDiffSort.I64 : random.Next(sortCount)).ToArray();
                functions.Add(new BridgeDiffFunction("Op" + i, BridgeDiffFunctionKind.Constructor, keys, random.Next(sortCount), Subsumable(random)));
            }

            // Relations can always subsume, as egglog's (constructors into a fresh sort).
            var relations = bulk ? 1 + random.Next(2) : random.Next(3);
            for (var i = 0; i < relations; i++)
            {
                var keyCount = random.Next(10) switch
                {
                    0 => 0,
                    < 3 => 1,
                    _ => 2,
                };
                var keys = bulk && i == 0 ? [0] : Enumerable.Range(0, keyCount).Select(_ => random.Next(10) == 0 ? BridgeDiffSort.I64 : random.Next(sortCount)).ToArray();
                functions.Add(new BridgeDiffFunction("R" + i, BridgeDiffFunctionKind.Relation, keys, BridgeDiffSort.Unit, true));
            }

            var lattices = bulk ? 1 + random.Next(2) : random.Next(3);
            for (var i = 0; i < lattices; i++)
            {
                var keyCount = random.Next(10) switch
                {
                    0 => 0,
                    < 7 => 1,
                    _ => 2,
                };
                var keys = bulk && i == 0 ? [0] : Enumerable.Range(0, keyCount).Select(_ => random.Next(sortCount)).ToArray();
                var min = random.Next(2) == 0;
                functions.Add(new BridgeDiffFunction((min ? "lo" : "hi") + i, min ? BridgeDiffFunctionKind.Min : BridgeDiffFunctionKind.Max, keys, BridgeDiffSort.I64, false));
            }

            if (random.Next(4) == 0)
            {
                var key = random.Next(sortCount);
                var output = random.Next(2) == 0 ? key : random.Next(sortCount);
                functions.Add(new BridgeDiffFunction("best0", BridgeDiffFunctionKind.IdFunction, random.Next(5) == 0 ? [] : [key], output, false));
            }

            return functions.ToArray();
        }

        /// <summary>
        /// Picks whether a constructor can subsume: as in egglog, except for one in eight.
        /// </summary>
        static bool Subsumable(Random random) => random.Next(8) > 0;

        /// <summary>
        /// Generates the seeds: a bulk load for a bulk program, terms and facts before the rules, then sometimes a few
        /// between iterations.
        /// </summary>
        static BridgeDiffSeed[] GenerateSeeds(Random random, BridgeDiffFunction[] functions, int sortCount, int iterations, bool bulk)
        {
            var seeds = new List<BridgeDiffSeed>();
            var terms = Enumerable.Range(0, sortCount).Select(_ => new List<int>()).ToArray();
            if (bulk)
            {
                // Keys from 1000 up, clear of the small integers the other seeds and the rules use.
                var count = (uint)random.Next(20_500, 26_001);
                seeds.Add(new BridgeDiffSeed(BridgeDiffSeedKind.Bulk, 0, [BridgeDiffEntry.Const(1000, BridgeDiffSort.I64), BridgeDiffEntry.Const(count, BridgeDiffSort.I64)], 0));
            }

            var termsLeft = 4 + random.Next(21);
            var factsLeft = random.Next(13);
            while (termsLeft + factsLeft > 0)
            {
                var haveTerms = terms.Any(t => t.Count > 0);
                if (termsLeft > 0 && (factsLeft == 0 || haveTerms == false || random.Next(termsLeft + factsLeft) < termsLeft))
                {
                    termsLeft--;
                    AddTerm(random, functions, terms, seeds, 0);
                }
                else
                {
                    factsLeft--;
                    AddFact(random, functions, terms, seeds, 0);
                }
            }

            if (random.Next(4) == 0)
            {
                var late = Enumerable.Range(0, 1 + random.Next(3)).Select(_ => 1 + random.Next(iterations)).Order().ToArray();
                foreach (var phase in late)
                {
                    if (random.Next(2) == 0)
                        AddTerm(random, functions, terms, seeds, phase);
                    else
                        AddFact(random, functions, terms, seeds, phase);
                }
            }

            return seeds.ToArray();
        }

        /// <summary>
        /// Adds a term seed over existing classes and small integers, if some constructor's keys can be filled.
        /// </summary>
        static void AddTerm(Random random, BridgeDiffFunction[] functions, List<int>[] terms, List<BridgeDiffSeed> seeds, int phase)
        {
            var candidates = Enumerable.Range(0, functions.Length)
                .Where(f => functions[f].Kind == BridgeDiffFunctionKind.Constructor && functions[f].Keys.All(k => BridgeDiffSort.IsEq(k) == false || terms[k].Count > 0))
                .ToList();
            var f = candidates[random.Next(candidates.Count)];
            var args = functions[f].Keys.Select(k => Arg(random, terms, k)).ToArray();
            terms[functions[f].Output].Add(seeds.Count);
            seeds.Add(new BridgeDiffSeed(BridgeDiffSeedKind.Term, f, args, phase));
        }

        /// <summary>
        /// Adds a fact seed (a relation row, a lattice value, an id-function or constructor row) or a union, over
        /// existing classes, if the chosen kind can be filled. A relation row is written without an output, egglog's
        /// <c>(R a b)</c>.
        /// </summary>
        static void AddFact(Random random, BridgeDiffFunction[] functions, List<int>[] terms, List<BridgeDiffSeed> seeds, int phase)
        {
            if (random.Next(5) == 0)
            {
                var sorts = Enumerable.Range(0, terms.Length).Where(s => terms[s].Count > 0).ToList();
                if (sorts.Count == 0)
                    return;

                var sort = sorts[random.Next(sorts.Count)];
                var a = Arg(random, terms, sort);
                var b = Arg(random, terms, sort);
                if (a == b && terms[sort].Count > 1)
                    b = Arg(random, terms, sort);

                seeds.Add(new BridgeDiffSeed(BridgeDiffSeedKind.Union, -1, [a, b], phase));
                return;
            }

            var candidates = Enumerable.Range(0, functions.Length)
                .Where(f => functions[f].Kind != BridgeDiffFunctionKind.Constructor || functions[f].KeyCount > 0)
                .Where(f => Enumerable.Range(0, functions[f].Columns).All(c => BridgeDiffSort.IsEq(functions[f].SortOf(c)) == false || terms[functions[f].SortOf(c)].Count > 0))
                .ToList();
            if (candidates.Count == 0)
                return;

            var f = candidates[random.Next(candidates.Count)];
            var function = functions[f];
            var args = Enumerable.Range(0, function.Columns).Select(c => function.SortOf(c) == BridgeDiffSort.I64 && c == function.KeyCount
                ? BridgeDiffEntry.Const((uint)random.Next(10), BridgeDiffSort.I64)
                : Arg(random, terms, function.SortOf(c))).ToArray();
            seeds.Add(new BridgeDiffSeed(BridgeDiffSeedKind.Set, f, args, phase));
        }

        /// <summary>
        /// Picks a seed argument of <paramref name="sort"/>: an existing term's class, a small integer or the unit value
        /// (a relation's output, which makes the fact an insertion).
        /// </summary>
        static BridgeDiffEntry Arg(Random random, List<int>[] terms, int sort) => sort switch
        {
            BridgeDiffSort.I64 => BridgeDiffEntry.Const((uint)random.Next(5), sort),
            BridgeDiffSort.Unit => BridgeDiffEntry.Const(0, sort),
            _ => BridgeDiffEntry.Variable(terms[sort][random.Next(terms[sort].Count)], sort),
        };

    }

}
