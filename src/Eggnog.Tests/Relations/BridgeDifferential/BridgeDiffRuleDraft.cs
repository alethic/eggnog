using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Generates one random, well-sorted, well-formed rule for <see cref="BridgeDiffGenerator"/>: atoms that join, then
    /// primitives, then actions shaped like the rules egglog programs write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Up to five atoms, chained: every atom after the first reuses a variable an earlier atom bound (a join on an id,
    /// an output nested into a key, as a pattern <c>(F x (G y))</c> lowers, or an integer), and columns sometimes
    /// repeat a variable (<c>(F x x)</c>) or hold a constant (an integer or one of the seeds' ids). A relation atom's
    /// output is a fresh variable, as the frontend lowers <c>(R a b)</c>, or sometimes the output of an earlier atom of
    /// the same relation, as egglog's tests/relation-query-allowed.egg joins <c>(= (R 1) (R 2))</c>. The primitives are
    /// filters (<c>!=</c>, <c>&lt;</c>) and <c>+</c> binding or checking a result.
    /// </para>
    /// <para>
    /// The actions are rewrites (build a term from the matched variables with nested constructor lookups and union it
    /// with a matched class, or set a constructor row's output to it), Datalog-style relation inserts, lattice writes
    /// of matched, constant or computed integers, id-function writes, unions, plain term creation, subsumption, and
    /// reads of functions without a default (<c>(lo x)</c> on the right-hand side), mostly at keys an atom matched, so
    /// they cannot fail, and rarely at other bound keys (<see cref="TryRiskyRead"/>), which panics where the function
    /// lacks the key. A rule without atoms builds and writes from constants only, like a top-level egglog command.
    /// </para>
    /// <para>
    /// Two rules in three come instead from templates of the rules egglog programs write (<see cref="TryTemplate"/>),
    /// when the signature fits one. One instance per rule; not thread-safe.
    /// </para>
    /// </remarks>
    sealed partial class BridgeDiffRuleDraft
    {

        /// <summary>
        /// The deepest nesting of constructor lookups a built term gets before only leaves are used.
        /// </summary>
        const int MaxDepth = 2;

        readonly Random random;
        readonly BridgeDiffFunction[] functions;
        readonly int sortCount;
        readonly IReadOnlyList<(uint Id, int Sort)> constants;
        readonly List<int> varSorts = new();
        readonly List<BridgeDiffAtom> atoms = new();
        readonly List<BridgeDiffPrim> prims = new();
        readonly List<BridgeDiffAction> actions = new();

        /// <summary>
        /// The variables no later part of the rule may read: lattice values read at keys no atom guarantees
        /// (<see cref="TryRiskyRead"/>).
        /// </summary>
        readonly HashSet<int> unreadable = new();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="random">The source of randomness.</param>
        /// <param name="functions">The program's functions.</param>
        /// <param name="sortCount">The number of eq sorts.</param>
        /// <param name="constants">The ids the seeds mint before the rules run, with their sorts, which atoms may
        /// name.</param>
        public BridgeDiffRuleDraft(Random random, BridgeDiffFunction[] functions, int sortCount, IReadOnlyList<(uint Id, int Sort)> constants)
        {
            this.random = random;
            this.functions = functions;
            this.sortCount = sortCount;
            this.constants = constants;
        }

        /// <summary>
        /// Generates the rule.
        /// </summary>
        /// <param name="name">The rule's description.</param>
        /// <returns>The rule.</returns>
        public BridgeDiffRule Generate(string name)
        {
            if (random.Next(3) > 0 && TryTemplate())
                return Finish(name);

            var atomCount = random.Next(100) switch
            {
                < 6 => 0,
                < 44 => 1,
                < 76 => 2,
                < 94 => 3,
                < 98 => 4,
                _ => 5,
            };
            for (var a = 0; a < atomCount; a++)
                AddAtom(a > 0);

            var primCount = random.Next(10) switch
            {
                < 6 => 0,
                < 9 => 1,
                _ => 2,
            };
            for (var p = 0; p < primCount; p++)
                AddPrim();

            var actionCount = 1 + random.Next(3);
            for (var k = 0; k < actionCount; k++)
                AddAction();

            if (actions.Count == 0)
                BuildTerm(random.Next(sortCount), 0);

            return Finish(name);
        }

        /// <summary>
        /// Completes the rule with random plan settings.
        /// </summary>
        BridgeDiffRule Finish(string name)
        {
            var strategy = random.Next(3) switch
            {
                0 => PlanStrategy.Gj,
                1 => PlanStrategy.PureSize,
                _ => PlanStrategy.MinCover,
            };
            return new BridgeDiffRule(name, varSorts.ToArray(), atoms.ToArray(), prims.ToArray(), actions.ToArray(), strategy, random.Next(4) == 0);
        }

        /// <summary>
        /// Adds an atom; with <paramref name="join"/>, one of its columns reuses a variable an earlier atom bound.
        /// </summary>
        void AddAtom(bool join)
        {
            var f = PickAtomFunction();
            var function = functions[f];
            var entries = new BridgeDiffEntry[function.Columns];

            var joinColumn = -1;
            var joinVar = -1;
            if (join)
            {
                var columns = Enumerable.Range(0, function.Columns).Where(c => function.SortOf(c) != BridgeDiffSort.Unit && Bound(function.SortOf(c)).Count > 0).ToList();
                if (columns.Count > 0)
                {
                    joinColumn = Pick(columns);
                    joinVar = Pick(Bound(function.SortOf(joinColumn)));
                }
            }

            for (var c = 0; c < function.Columns; c++)
            {
                var sort = function.SortOf(c);
                if (c == joinColumn)
                {
                    entries[c] = BridgeDiffEntry.Variable(joinVar, sort);
                    continue;
                }

                switch (sort)
                {
                    case BridgeDiffSort.Unit:
                    {
                        // A relation's output, a row id: a fresh variable, or the output of an earlier atom of the same
                        // relation, which only that row (or one merged with it) matches.
                        var earlier = atoms.Where(a => a.Function == f && a.Entries[^1].IsVar).Select(a => a.Entries[^1].Var).ToList();
                        entries[c] = BridgeDiffEntry.Variable(earlier.Count > 0 && random.Next(4) == 0 ? Pick(earlier) : NewVar(sort), sort);
                        break;
                    }
                    case BridgeDiffSort.I64:
                    {
                        var roll = random.Next(10);
                        var existing = Bound(sort);
                        entries[c] = roll < 2 ? BridgeDiffEntry.Const((uint)random.Next(5), sort)
                            : roll < 4 && existing.Count > 0 ? BridgeDiffEntry.Variable(Pick(existing), sort)
                            : BridgeDiffEntry.Variable(NewVar(sort), sort);
                        break;
                    }
                    default:
                    {
                        // Bound already includes this atom's earlier columns, so reuse can repeat a variable in the
                        // atom.
                        var roll = random.Next(100);
                        var existing = Bound(sort);
                        var named = constants.Where(k => k.Sort == sort).ToList();
                        entries[c] = roll < 3 && named.Count > 0 ? BridgeDiffEntry.Const(Pick(named).Id, sort)
                            : roll < 15 && existing.Count > 0 ? BridgeDiffEntry.Variable(Pick(existing), sort)
                            : BridgeDiffEntry.Variable(NewVar(sort), sort);
                        break;
                    }
                }
            }

            bool? subsumed = false;
            if (function.CanSubsume)
            {
                subsumed = random.Next(20) switch
                {
                    < 16 => false,
                    < 19 => null,
                    _ => true,
                };
            }

            atoms.Add(new BridgeDiffAtom(f, entries, subsumed));
        }

        /// <summary>
        /// Adds a left-hand-side primitive over bound variables, if the rule has the variables it needs.
        /// </summary>
        void AddPrim()
        {
            var ints = Bound(BridgeDiffSort.I64);
            switch (random.Next(3))
            {
                case 0:
                {
                    // (!= a b) over two variables of one sort.
                    var sorts = Enumerable.Range(0, sortCount).Append(BridgeDiffSort.I64).Where(s => Bound(s).Count >= 2).ToList();
                    if (sorts.Count == 0)
                        return;

                    var sort = Pick(sorts);
                    var vars = Bound(sort);
                    var a = Pick(vars);
                    var b = Pick(vars.Where(v => v != a).ToList());
                    prims.Add(new BridgeDiffPrim(BridgeDiffPrimitive.NotEqual, BridgeDiffEntry.Variable(a, sort), BridgeDiffEntry.Variable(b, sort), UnitResult()));
                    return;
                }
                case 1:
                {
                    // (< a b) over integers.
                    if (ints.Count == 0)
                        return;

                    var left = BridgeDiffEntry.Variable(Pick(ints), BridgeDiffSort.I64);
                    var right = ints.Count > 1 && random.Next(2) == 0 ? BridgeDiffEntry.Variable(Pick(ints), BridgeDiffSort.I64) : BridgeDiffEntry.Const((uint)random.Next(1, 10), BridgeDiffSort.I64);
                    if (random.Next(2) == 0)
                        (left, right) = (right, left);

                    prims.Add(new BridgeDiffPrim(BridgeDiffPrimitive.Less, left, right, UnitResult()));
                    return;
                }
                default:
                {
                    // (= r (+ a b)): binds a new r, or checks a bound one or a constant.
                    if (ints.Count == 0)
                        return;

                    var left = BridgeDiffEntry.Variable(Pick(ints), BridgeDiffSort.I64);
                    var right = random.Next(3) == 0 ? BridgeDiffEntry.Variable(Pick(ints), BridgeDiffSort.I64) : BridgeDiffEntry.Const((uint)random.Next(4), BridgeDiffSort.I64);
                    var roll = random.Next(10);
                    var result = roll < 5 ? BridgeDiffEntry.Variable(NewVar(BridgeDiffSort.I64), BridgeDiffSort.I64)
                        : roll < 8 ? BridgeDiffEntry.Variable(Pick(ints), BridgeDiffSort.I64)
                        : BridgeDiffEntry.Const((uint)random.Next(10), BridgeDiffSort.I64);
                    prims.Add(new BridgeDiffPrim(BridgeDiffPrimitive.Add, left, right, result));
                    return;
                }
            }
        }

        /// <summary>
        /// Adds one action, or a short sequence (a lookup chain followed by its union or set).
        /// </summary>
        void AddAction()
        {
            var roll = random.Next(100);
            if (roll < 30)
            {
                // A rewrite: build a term and union it with a matched class.
                var targets = BoundIds();
                if (targets.Count == 0)
                {
                    BuildTerm(random.Next(sortCount), 0);
                    return;
                }

                var target = Pick(targets);
                var sort = varSorts[target];
                var term = BuildTerm(sort, 0);
                if (random.Next(10) < 8)
                    actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [BridgeDiffEntry.Variable(term, sort), BridgeDiffEntry.Variable(target, sort)], -1));

                return;
            }

            if (roll < 40)
            {
                // A rewrite in set form, (set (G b a) id), or an id function's write.
                var candidates = Indexes(f => (f.Kind is BridgeDiffFunctionKind.Constructor or BridgeDiffFunctionKind.IdFunction) && f.KeyCount > 0 && Bound(f.Output).Count > 0);
                if (candidates.Count == 0)
                    return;

                var f = Pick(candidates);
                var keys = Keys(functions[f], 0);
                var output = BridgeDiffEntry.Variable(Pick(Bound(functions[f].Output)), functions[f].Output);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, f, default, [.. keys, output], -1));
                return;
            }

            if (roll < 55)
            {
                // A Datalog-style insert into a relation: egglog's (R a b) action, or a lookup that inserts the row and
                // binds its id.
                var relations = Indexes(f => f.Kind == BridgeDiffFunctionKind.Relation);
                if (relations.Count == 0)
                    return;

                var f = Pick(relations);
                var keys = Keys(functions[f], 0);
                if (random.Next(10) < 7)
                    actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, f, default, [.. keys, BridgeDiffEntry.Const(0, BridgeDiffSort.Unit)], -1));
                else
                    actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, f, default, keys, NewVar(BridgeDiffSort.Unit)));

                return;
            }

            if (roll < 70)
            {
                // A lattice write of a matched, constant or computed integer.
                var lattices = Indexes(f => f.Kind is BridgeDiffFunctionKind.Min or BridgeDiffFunctionKind.Max);
                if (lattices.Count == 0)
                    return;

                var f = Pick(lattices);
                var keys = Keys(functions[f], 0);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, f, default, [.. keys, Integer(allowCall: true)], -1));
                return;
            }

            if (roll < 80)
            {
                // A union of two classes of one sort, matched or built.
                var targets = BoundIds();
                var sort = targets.Count > 0 ? varSorts[Pick(targets)] : random.Next(sortCount);
                var a = IdOf(sort, 0);
                var b = IdOf(sort, 0);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [a, b], -1));
                return;
            }

            if (roll < 87)
            {
                // Subsume a row: often the very row an atom matched, as (rewrite ... :subsume) does.
                var subsumable = Indexes(f => f.CanSubsume);
                if (subsumable.Count == 0)
                    return;

                var f = Pick(subsumable);
                var matched = atoms.Where(a => a.Function == f).ToList();
                var keys = matched.Count > 0 && random.Next(3) > 0 ? Pick(matched).Entries.Take(functions[f].KeyCount).ToArray() : Keys(functions[f], 0);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Subsume, f, default, keys, -1));
                return;
            }

            if (roll < 94)
            {
                // Read a function without a default, (lo x) on the right-hand side, at a key an atom matched, so the
                // lookup cannot fail; then use what it read.
                var readable = atoms.Where(a => functions[a.Function].HasDefault == false).ToList();
                if (readable.Count > 0)
                {
                    var atom = Pick(readable);
                    var function = functions[atom.Function];
                    var read = NewVar(function.Output);
                    actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, atom.Function, default, atom.Entries.Take(function.KeyCount).ToArray(), read));
                    UseRead(function, read);
                    return;
                }
            }

            if (roll >= 94 && roll < 96 && TryRiskyRead())
                return;

            // Create a term and nothing else.
            BuildTerm(random.Next(sortCount), 0);
        }

        /// <summary>
        /// Reads a function without a default at keys no atom guarantees, bound variables of its key sorts (none for a
        /// global). Where the function lacks the key the rule panics; the runner checks that every evaluation panics in
        /// the same iteration, which does not depend on the evaluation, as a match whose read misses is new when it
        /// first exists.
        /// </summary>
        /// <remarks>
        /// An id function's output is used, as it only ever changes by a union, so a read made once stays right modulo
        /// equivalence. A lattice's is not: its value can change later without a row the rule's atoms match changing,
        /// and then naive evaluation, which re-fires the match, legitimately writes something semi-naive evaluation
        /// does not (egglog rules read lattices in their queries for this reason).
        /// </remarks>
        /// <returns><c>false</c> when no such function's keys can be filled.</returns>
        bool TryRiskyRead()
        {
            var readable = Indexes(f => f.HasDefault == false && f.Keys.All(k => Bound(k).Count > 0));
            if (readable.Count == 0)
                return false;

            var f = Pick(readable);
            var function = functions[f];
            var keys = function.Keys.Select(k => BridgeDiffEntry.Variable(Pick(Bound(k)), k)).ToArray();
            var read = NewVar(function.Output);
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, f, default, keys, read));
            if (function.OutputIsId)
                UseRead(function, read);
            else
                unreadable.Add(read);

            return true;
        }

        /// <summary>
        /// Uses variable <paramref name="read"/>, the output of a read of <paramref name="function"/>: unions an id
        /// with a class of its sort, or writes a lattice with an integer that may be it.
        /// </summary>
        void UseRead(BridgeDiffFunction function, int read)
        {
            if (function.OutputIsId)
            {
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [BridgeDiffEntry.Variable(read, function.Output), IdOf(function.Output, 0)], -1));
                return;
            }

            var lattices = Indexes(f => f.Kind is BridgeDiffFunctionKind.Min or BridgeDiffFunctionKind.Max);
            var target = Pick(lattices);
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, target, default, [.. Keys(functions[target], 0), Integer(allowCall: true)], -1));
        }

        /// <summary>
        /// Builds a term of <paramref name="sort"/> by constructor lookups (nested up to <see cref="MaxDepth"/>) and
        /// returns the variable holding its class.
        /// </summary>
        int BuildTerm(int sort, int depth)
        {
            var constructors = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.Output == sort);
            var leaves = constructors.Where(f => functions[f].Keys.All(k => BridgeDiffSort.IsEq(k) == false)).ToList();
            var choices = depth >= MaxDepth || random.Next(10) < 3 ? leaves : constructors;
            var f = Pick(choices.Count > 0 ? choices : constructors);
            var keys = Keys(functions[f], depth);
            var result = NewVar(sort);
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, f, default, keys, result));
            return result;
        }

        /// <summary>
        /// Picks the key entries of a lookup or write of <paramref name="function"/>.
        /// </summary>
        BridgeDiffEntry[] Keys(BridgeDiffFunction function, int depth)
        {
            var keys = new BridgeDiffEntry[function.KeyCount];
            for (var c = 0; c < keys.Length; c++)
            {
                var sort = function.Keys[c];
                keys[c] = sort switch
                {
                    BridgeDiffSort.I64 => Integer(allowCall: random.Next(4) == 0),
                    BridgeDiffSort.Unit => BridgeDiffEntry.Const(0, sort),
                    _ => IdOf(sort, depth),
                };
            }

            return keys;
        }

        /// <summary>
        /// Picks an id entry of <paramref name="sort"/>: usually a bound variable, else a built term.
        /// </summary>
        BridgeDiffEntry IdOf(int sort, int depth)
        {
            var existing = Bound(sort);
            if (existing.Count > 0 && (depth >= MaxDepth || random.Next(10) < 7))
                return BridgeDiffEntry.Variable(Pick(existing), sort);

            return BridgeDiffEntry.Variable(BuildTerm(sort, depth + 1), sort);
        }

        /// <summary>
        /// Picks an integer entry: a bound variable, a constant, or (with <paramref name="allowCall"/>) the result of a
        /// right-hand-side call of <c>+</c>, <c>min</c> or <c>max</c>.
        /// </summary>
        BridgeDiffEntry Integer(bool allowCall)
        {
            var ints = Bound(BridgeDiffSort.I64);
            var roll = random.Next(10);
            if (allowCall && roll < 3)
            {
                var primitive = random.Next(3) switch
                {
                    0 => BridgeDiffPrimitive.Add,
                    1 => BridgeDiffPrimitive.Min,
                    _ => BridgeDiffPrimitive.Max,
                };
                var a = ints.Count > 0 ? BridgeDiffEntry.Variable(Pick(ints), BridgeDiffSort.I64) : BridgeDiffEntry.Const((uint)random.Next(10), BridgeDiffSort.I64);
                var b = ints.Count > 1 && random.Next(2) == 0 ? BridgeDiffEntry.Variable(Pick(ints), BridgeDiffSort.I64) : BridgeDiffEntry.Const((uint)random.Next(4), BridgeDiffSort.I64);
                var result = NewVar(BridgeDiffSort.I64);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Call, -1, primitive, [a, b], result));
                return BridgeDiffEntry.Variable(result, BridgeDiffSort.I64);
            }

            if (ints.Count > 0 && roll < 7)
                return BridgeDiffEntry.Variable(Pick(ints), BridgeDiffSort.I64);

            return BridgeDiffEntry.Const((uint)random.Next(10), BridgeDiffSort.I64);
        }

        /// <summary>
        /// Picks the result entry of a filter: a new unit variable or the unit constant.
        /// </summary>
        BridgeDiffEntry UnitResult() => random.Next(2) == 0 ? BridgeDiffEntry.Variable(NewVar(BridgeDiffSort.Unit), BridgeDiffSort.Unit) : BridgeDiffEntry.Const(0, BridgeDiffSort.Unit);

        /// <summary>
        /// Picks the function of an atom, weighted towards constructors with keys.
        /// </summary>
        int PickAtomFunction()
        {
            var weights = functions.Select(f => f.Kind switch
            {
                BridgeDiffFunctionKind.Constructor => f.Keys.Any(BridgeDiffSort.IsEq) ? 9 : 2,
                BridgeDiffFunctionKind.Relation => 6,
                BridgeDiffFunctionKind.IdFunction => 3,
                _ => 4,
            }).ToArray();
            var roll = random.Next(weights.Sum());
            for (var f = 0; f < weights.Length; f++)
            {
                roll -= weights[f];
                if (roll < 0)
                    return f;
            }

            return weights.Length - 1;
        }

        /// <summary>
        /// Adds a variable of <paramref name="sort"/>. Every variable is bound where it is created (by the atom,
        /// primitive or action the caller is adding), so later parts of the rule may read it.
        /// </summary>
        int NewVar(int sort)
        {
            varSorts.Add(sort);
            return varSorts.Count - 1;
        }

        /// <summary>
        /// Lists the variables of <paramref name="sort"/> created so far, all bound, except those nothing may read
        /// (<see cref="unreadable"/>).
        /// </summary>
        List<int> Bound(int sort)
        {
            var list = new List<int>();
            for (var v = 0; v < varSorts.Count; v++)
                if (varSorts[v] == sort && unreadable.Contains(v) == false)
                    list.Add(v);

            return list;
        }

        /// <summary>
        /// Lists the variables of eq sorts created so far, except those nothing may read.
        /// </summary>
        List<int> BoundIds() => Enumerable.Range(0, varSorts.Count).Where(v => BridgeDiffSort.IsEq(varSorts[v]) && unreadable.Contains(v) == false).ToList();

        /// <summary>
        /// Lists the indexes of the functions that satisfy <paramref name="predicate"/>.
        /// </summary>
        List<int> Indexes(Func<BridgeDiffFunction, bool> predicate) => Enumerable.Range(0, functions.Length).Where(f => predicate(functions[f])).ToList();

        /// <summary>
        /// Picks an element of a non-empty list.
        /// </summary>
        T Pick<T>(IReadOnlyList<T> list) => list[random.Next(list.Count)];

    }

}
