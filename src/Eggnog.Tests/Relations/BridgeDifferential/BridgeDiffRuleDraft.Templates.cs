using System.Linq;

namespace Eggnog.Relations.Tests
{

    sealed partial class BridgeDiffRuleDraft
    {

        /// <summary>
        /// Generates the first rule of a bulk program, a chain of unions between the bulk-loaded leaves of the program's
        /// first function: <c>(rule ((= x (NumA k)) (&lt; k c)) ((union x (NumA (+ k d)))))</c>, with <c>c</c> 20 to 400
        /// past the first key, so its first run unions a few hundred classes of a table of more than 10,000 rows, which the
        /// rebuild then handles incrementally.
        /// </summary>
        /// <param name="name">The rule's description.</param>
        /// <returns>The rule.</returns>
        public BridgeDiffRule GenerateChain(string name)
        {
            var (k, x) = (NewVar(BridgeDiffSort.I64), NewVar(functions[0].Output));
            AddTemplateAtom(0, [E(k), E(x)]);
            prims.Add(new BridgeDiffPrim(BridgeDiffPrimitive.Less, E(k), BridgeDiffEntry.Const((uint)(1000 + random.Next(20, 401)), BridgeDiffSort.I64), UnitResult()));
            var next = NewVar(BridgeDiffSort.I64);
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Call, -1, BridgeDiffPrimitive.Add, [E(k), BridgeDiffEntry.Const((uint)random.Next(1, 4), BridgeDiffSort.I64)], next));
            var y = NewVar(functions[0].Output);
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, 0, default, [E(next)], y));
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [E(x), E(y)], -1));
            return Finish(name);
        }

        /// <summary>
        /// Generates a rule of a bulk program that writes to every bulk-loaded leaf of the program's first function:
        /// <c>(rule ((= x (NumA k))) ((set (lo x) (+ k c)) (R x) (subsume (NumA k))))</c>, the lattice value <c>k + c</c>,
        /// <c>k</c> or a constant, the relation insertion and the subsumption each sometimes left out. The atom sometimes
        /// matches subsumed leaves too, so the rule keeps matching after another one subsumed them.
        /// </summary>
        /// <remarks>
        /// Two or three of these run in a bulk program's first iteration and all write its first lattice over the first
        /// sort, so a single insert into the lattice (and the relation, and the leaves) stages more than 20,000 rows per
        /// rule with conflicting keys, which the merge reduces, and which a parallel e-graph inserts in parallel. Later
        /// iterations re-confirm them without changing anything: every iteration of the naive e-graph, and those of the
        /// semi-naive one after a subsumption or a union re-stamped the leaves. The program's signature has a lattice and a
        /// relation over the first sort (<see cref="BridgeDiffGenerator"/>).
        /// </remarks>
        /// <param name="name">The rule's description.</param>
        /// <returns>The rule.</returns>
        public BridgeDiffRule GenerateBulkWrite(string name)
        {
            var leaf = functions[0];
            var sort = leaf.Output;
            var (k, x) = (NewVar(BridgeDiffSort.I64), NewVar(sort));
            atoms.Add(new BridgeDiffAtom(0, [E(k), E(x)], leaf.CanSubsume && random.Next(2) == 0 ? null : false));

            // Every bulk write targets the program's first lattice over the leaves' sort, so two of them conflict.
            var lattice = Indexes(f => (f.Kind is BridgeDiffFunctionKind.Min or BridgeDiffFunctionKind.Max) && f.KeyCount == 1 && f.Keys[0] == sort)[0];
            BridgeDiffEntry value;
            switch (random.Next(3))
            {
                case 0:
                    value = E(k);
                    break;
                case 1:
                    value = BridgeDiffEntry.Const((uint)random.Next(10), BridgeDiffSort.I64);
                    break;
                default:
                {
                    var sum = NewVar(BridgeDiffSort.I64);
                    actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Call, -1, BridgeDiffPrimitive.Add, [E(k), BridgeDiffEntry.Const((uint)random.Next(1, 10), BridgeDiffSort.I64)], sum));
                    value = E(sum);
                    break;
                }
            }

            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, lattice, default, [E(x), value], -1));

            var relations = Indexes(f => f.Kind == BridgeDiffFunctionKind.Relation && f.KeyCount == 1 && f.Keys[0] == sort);
            if (relations.Count > 0 && random.Next(2) == 0)
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, Pick(relations), default, [E(x), BridgeDiffEntry.Const(0, BridgeDiffSort.Unit)], -1));

            if (leaf.CanSubsume && random.Next(3) == 0)
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Subsume, 0, default, [E(k)], -1));

            return Finish(name);
        }

        /// <summary>
        /// Tries to fill the rule from a template of the rules egglog programs write, over the program's signature:
        /// commutativity, associativity, absorption, constant folding, distribution, Datalog transitive closure, a
        /// "universe" relation over terms, lattice propagation along terms, and unions of related classes. A few
        /// templates are tried, as the signature may not fit the first.
        /// </summary>
        /// <returns><c>true</c> if a template filled the rule; otherwise the rule is untouched.</returns>
        bool TryTemplate()
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var filled = random.Next(9) switch
                {
                    0 => Commute(),
                    1 => Associate(),
                    2 => Absorb(),
                    3 => Fold(),
                    4 => Path(),
                    5 => Universe(),
                    6 => Propagate(),
                    7 => UnionRelated(),
                    _ => Distribute(),
                };
                if (filled)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// <c>(= r (Op .. x .. y ..)) =&gt; (union r (Op .. y .. x ..))</c>, or the set form, sometimes subsuming the
        /// matched term, as <c>(rewrite ... :subsume)</c> does.
        /// </summary>
        bool Commute()
        {
            var candidates = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.KeyCount >= 2 && f.Keys.Distinct().Count() < f.KeyCount);
            if (candidates.Count == 0)
                return false;

            var f = Pick(candidates);
            var function = functions[f];
            var pairs = (from i in Enumerable.Range(0, function.KeyCount) from j in Enumerable.Range(0, function.KeyCount) where i < j && function.Keys[i] == function.Keys[j] select (i, j)).ToList();
            var (a, b) = Pick(pairs);
            var keys = function.Keys.Select(NewVar).ToArray();
            var r = NewVar(function.Output);
            AddTemplateAtom(f, [.. keys.Select(E), E(r)]);

            var swapped = keys.ToArray();
            (swapped[a], swapped[b]) = (swapped[b], swapped[a]);
            if (random.Next(2) == 0)
            {
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, f, default, [.. swapped.Select(E), E(r)], -1));
            }
            else
            {
                var t = NewVar(function.Output);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, f, default, swapped.Select(E).ToArray(), t));
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [E(t), E(r)], -1));
            }

            if (function.CanSubsume && random.Next(3) == 0)
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Subsume, f, default, keys.Select(E).ToArray(), -1));

            return true;
        }

        /// <summary>
        /// <c>(= r (Op x t)) (= t (Op y z)) =&gt; (union r (Op (Op x y) z))</c>, or the set form.
        /// </summary>
        bool Associate()
        {
            var candidates = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.KeyCount == 2 && f.Keys[0] == f.Output && f.Keys[1] == f.Output);
            if (candidates.Count == 0)
                return false;

            var f = Pick(candidates);
            var sort = functions[f].Output;
            var (x, y, z, t, r) = (NewVar(sort), NewVar(sort), NewVar(sort), NewVar(sort), NewVar(sort));
            AddTemplateAtom(f, [E(x), E(t), E(r)]);
            AddTemplateAtom(f, [E(y), E(z), E(t)]);

            var xy = NewVar(sort);
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, f, default, [E(x), E(y)], xy));
            if (random.Next(2) == 0)
            {
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, f, default, [E(xy), E(z), E(r)], -1));
            }
            else
            {
                var top = NewVar(sort);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, f, default, [E(xy), E(z)], top));
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [E(r), E(top)], -1));
            }

            return true;
        }

        /// <summary>
        /// <c>(= r (Op .. x ..)) =&gt; (union r x)</c> where <c>x</c> has the output's sort, sometimes only where another
        /// key is a leaf (<c>(Op x (Num 0))</c>, <c>(Op x (Leaf))</c>).
        /// </summary>
        bool Absorb()
        {
            var candidates = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.Keys.Contains(f.Output));
            if (candidates.Count == 0)
                return false;

            var f = Pick(candidates);
            var function = functions[f];
            var keys = function.Keys.Select(NewVar).ToArray();
            var r = NewVar(function.Output);
            AddTemplateAtom(f, [.. keys.Select(E), E(r)]);
            var x = Pick(Enumerable.Range(0, function.KeyCount).Where(k => function.Keys[k] == function.Output).ToList());

            // Constrain another id key to a leaf.
            var others = Enumerable.Range(0, function.KeyCount).Where(k => k != x && BridgeDiffSort.IsEq(function.Keys[k])).ToList();
            if (others.Count > 0 && random.Next(2) == 0)
            {
                var k = Pick(others);
                var leaves = Indexes(g => g.Kind == BridgeDiffFunctionKind.Constructor && g.Output == function.Keys[k] && g.Keys.All(s => s == BridgeDiffSort.I64));
                if (leaves.Count > 0)
                {
                    var leaf = Pick(leaves);
                    var leafKeys = functions[leaf].Keys.Select(_ => BridgeDiffEntry.Const((uint)random.Next(3), BridgeDiffSort.I64));
                    AddTemplateAtom(leaf, [.. leafKeys, E(keys[k])]);
                }
            }

            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [E(r), E(keys[x])], -1));
            return true;
        }

        /// <summary>
        /// Constant folding: <c>(= r (Op a b)) (= a (Num m)) (= b (Num n)) =&gt; (union r (Num (+ m n)))</c>, with
        /// <c>min</c> or <c>max</c> in place of <c>+</c> sometimes.
        /// </summary>
        bool Fold()
        {
            var nums = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.KeyCount == 1 && f.Keys[0] == BridgeDiffSort.I64);
            var candidates = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.Keys.Count(k => k == f.Output) >= 2 && nums.Any(n => functions[n].Output == f.Output));
            if (candidates.Count == 0)
                return false;

            var f = Pick(candidates);
            var function = functions[f];
            var sort = function.Output;
            var num = Pick(nums.Where(n => functions[n].Output == sort).ToList());
            var keys = function.Keys.Select(NewVar).ToArray();
            var r = NewVar(sort);
            AddTemplateAtom(f, [.. keys.Select(E), E(r)]);
            var positions = Enumerable.Range(0, function.KeyCount).Where(k => function.Keys[k] == sort).Take(2).ToArray();
            var (m, n) = (NewVar(BridgeDiffSort.I64), NewVar(BridgeDiffSort.I64));
            AddTemplateAtom(num, [E(m), E(keys[positions[0]])]);
            AddTemplateAtom(num, [E(n), E(keys[positions[1]])]);

            var primitive = random.Next(3) switch
            {
                0 => BridgeDiffPrimitive.Min,
                1 => BridgeDiffPrimitive.Max,
                _ => BridgeDiffPrimitive.Add,
            };
            var k = NewVar(BridgeDiffSort.I64);
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Call, -1, primitive, [E(m), E(n)], k));
            var folded = NewVar(sort);
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, num, default, [E(k)], folded));
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [E(r), E(folded)], -1));
            return true;
        }

        /// <summary>
        /// Datalog over a binary relation of one sort: transitive closure <c>(R x y) (R y z) =&gt; (R x z)</c>, a join
        /// with another such relation, symmetry, or a copy of one relation into another.
        /// </summary>
        bool Path()
        {
            var relations = Indexes(f => f.Kind == BridgeDiffFunctionKind.Relation && f.KeyCount == 2 && f.Keys[0] == f.Keys[1]);
            if (relations.Count == 0)
                return false;

            var target = Pick(relations);
            var sort = functions[target].Keys[0];
            var edges = relations.Where(e => functions[e].Keys[0] == sort).ToList();
            var edge = Pick(edges);
            var (x, y) = (NewVar(sort), NewVar(sort));
            switch (random.Next(4))
            {
                case 0:
                {
                    // Transitive closure, through the target itself or another relation.
                    var z = NewVar(sort);
                    AddTemplateAtom(target, [E(x), E(y), UnitEntry()]);
                    AddTemplateAtom(edge, [E(y), E(z), UnitEntry()]);
                    actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, target, default, [E(x), E(z), BridgeDiffEntry.Const(0, BridgeDiffSort.Unit)], -1));
                    break;
                }
                case 1:
                    // Symmetry, with the insert as a lookup.
                    AddTemplateAtom(target, [E(x), E(y), UnitEntry()]);
                    actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, target, default, [E(y), E(x)], NewVar(BridgeDiffSort.Unit)));
                    break;
                default:
                {
                    // A copy of the edges, or a filtered copy, into the target.
                    AddTemplateAtom(edge, [E(x), E(y), UnitEntry()]);
                    if (random.Next(2) == 0)
                        prims.Add(new BridgeDiffPrim(BridgeDiffPrimitive.NotEqual, E(x), E(y), UnitResult()));

                    actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, target, default, [E(x), E(y), BridgeDiffEntry.Const(0, BridgeDiffSort.Unit)], -1));
                    break;
                }
            }

            return true;
        }

        /// <summary>
        /// A relation over terms: <c>(= r (Op x ..)) =&gt; (R r)</c> or <c>(R x r)</c>, the keys taken from the term's
        /// variables of matching sorts.
        /// </summary>
        bool Universe()
        {
            var relations = Indexes(f => f.Kind == BridgeDiffFunctionKind.Relation && f.Keys.All(BridgeDiffSort.IsEq));
            if (relations.Count == 0)
                return false;

            var relation = Pick(relations);
            var constructors = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && functions[relation].Keys.All(k => f.Output == k || f.Keys.Contains(k)));
            if (constructors.Count == 0)
                return false;

            var f = Pick(constructors);
            var function = functions[f];
            var keys = function.Keys.Select(NewVar).ToArray();
            var r = NewVar(function.Output);
            AddTemplateAtom(f, [.. keys.Select(E), E(r)]);
            var pool = keys.Append(r).ToList();
            var args = functions[relation].Keys.Select(s => E(Pick(pool.Where(v => varSorts[v] == s).ToList()))).ToArray();
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, relation, default, [.. args, BridgeDiffEntry.Const(0, BridgeDiffSort.Unit)], -1));
            return true;
        }

        /// <summary>
        /// Lattice propagation along terms: <c>(= r (Op x y)) (= a (L x)) (= b (L y)) =&gt; (set (L r) (+ a b))</c> (or
        /// <c>min</c>, <c>max</c>, or <c>+</c> a constant from one key), backwards from a term to a key, or from integer
        /// leaves: <c>(= r (Num n)) =&gt; (set (L r) n)</c>.
        /// </summary>
        bool Propagate()
        {
            var lattices = Indexes(f => (f.Kind is BridgeDiffFunctionKind.Min or BridgeDiffFunctionKind.Max) && f.KeyCount == 1);
            if (lattices.Count == 0)
                return false;

            var lattice = Pick(lattices);
            var sort = functions[lattice].Keys[0];
            var nums = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.Output == sort && f.KeyCount == 1 && f.Keys[0] == BridgeDiffSort.I64);
            var constructors = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.Output == sort && f.Keys.Contains(sort));
            if (constructors.Count == 0 || (nums.Count > 0 && random.Next(3) == 0))
            {
                if (nums.Count == 0)
                    return false;

                var (n, leaf) = (NewVar(BridgeDiffSort.I64), NewVar(sort));
                AddTemplateAtom(Pick(nums), [E(n), E(leaf)]);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, lattice, default, [E(leaf), E(n)], -1));
                return true;
            }

            var f = Pick(constructors);
            var function = functions[f];
            var keys = function.Keys.Select(NewVar).ToArray();
            var r = NewVar(sort);
            AddTemplateAtom(f, [.. keys.Select(E), E(r)]);
            var positions = Enumerable.Range(0, function.KeyCount).Where(k => function.Keys[k] == sort).ToList();

            if (random.Next(4) == 0)
            {
                // Backwards: a key is at most (or at least) the term's value plus one.
                var a = NewVar(BridgeDiffSort.I64);
                AddTemplateAtom(lattice, [E(r), E(a)]);
                var bumped = NewVar(BridgeDiffSort.I64);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Call, -1, BridgeDiffPrimitive.Add, [E(a), BridgeDiffEntry.Const(1, BridgeDiffSort.I64)], bumped));
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, lattice, default, [E(keys[Pick(positions)]), E(bumped)], -1));
                return true;
            }

            var values = positions.Take(2).Select(p =>
            {
                var v = NewVar(BridgeDiffSort.I64);
                AddTemplateAtom(lattice, [E(keys[p]), E(v)]);
                return v;
            }).ToArray();
            var result = NewVar(BridgeDiffSort.I64);
            if (values.Length == 1)
            {
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Call, -1, BridgeDiffPrimitive.Add, [E(values[0]), BridgeDiffEntry.Const((uint)random.Next(3), BridgeDiffSort.I64)], result));
            }
            else
            {
                var primitive = random.Next(3) switch
                {
                    0 => BridgeDiffPrimitive.Min,
                    1 => BridgeDiffPrimitive.Max,
                    _ => BridgeDiffPrimitive.Add,
                };
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Call, -1, primitive, [E(values[0]), E(values[1])], result));
            }

            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Set, lattice, default, [E(r), E(result)], -1));
            return true;
        }

        /// <summary>
        /// Unions related classes: <c>(R x y) =&gt; (union x y)</c> over a binary relation of one sort, or
        /// <c>(= b (best x)) =&gt; (union b x)</c> over an id function to its key's sort, or two terms that share a key,
        /// <c>(= a (Op x ..)) (= b (Op' x ..)) =&gt; (union a b)</c>.
        /// </summary>
        bool UnionRelated()
        {
            var relations = Indexes(f => f.Kind == BridgeDiffFunctionKind.Relation && f.KeyCount == 2 && f.Keys[0] == f.Keys[1] && BridgeDiffSort.IsEq(f.Keys[0]));
            var bests = Indexes(f => f.Kind == BridgeDiffFunctionKind.IdFunction && f.KeyCount == 1 && f.Keys[0] == f.Output);
            var choice = random.Next(3);
            if (choice == 0 && relations.Count > 0)
            {
                var relation = Pick(relations);
                var sort = functions[relation].Keys[0];
                var (x, y) = (NewVar(sort), NewVar(sort));
                AddTemplateAtom(relation, [E(x), E(y), UnitEntry()]);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [E(x), E(y)], -1));
                return true;
            }

            if (choice == 1 && bests.Count > 0)
            {
                var best = Pick(bests);
                var sort = functions[best].Output;
                var (x, b) = (NewVar(sort), NewVar(sort));
                AddTemplateAtom(best, [E(x), E(b)]);
                actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [E(b), E(x)], -1));
                return true;
            }

            // Two terms of one sort over a shared key, of different constructors or differing in another key.
            var constructors = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.KeyCount >= 1 && BridgeDiffSort.IsEq(f.Keys[0]));
            var pairs = (from a in constructors
                         from b in constructors
                         where a <= b && functions[a].Output == functions[b].Output && functions[a].Keys[0] == functions[b].Keys[0] && (a != b || functions[a].KeyCount > 1)
                         select (a, b)).ToList();
            if (pairs.Count == 0)
                return false;

            var (first, second) = Pick(pairs);
            var shared = NewVar(functions[first].Keys[0]);
            var (left, right) = (NewVar(functions[first].Output), NewVar(functions[first].Output));
            AddTemplateAtom(first, [E(shared), .. functions[first].Keys.Skip(1).Select(k => E(NewVar(k))), E(left)]);
            AddTemplateAtom(second, [E(shared), .. functions[second].Keys.Skip(1).Select(k => E(NewVar(k))), E(right)]);
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [E(left), E(right)], -1));
            return true;
        }

        /// <summary>
        /// <c>(= r (Op1 x t)) (= t (Op2 y z)) =&gt; (union r (Op2 (Op1 x y) (Op1 x z)))</c> for two binary operators of
        /// one sort.
        /// </summary>
        bool Distribute()
        {
            var binary = Indexes(f => f.Kind == BridgeDiffFunctionKind.Constructor && f.KeyCount == 2 && f.Keys[0] == f.Output && f.Keys[1] == f.Output);
            var pairs = (from a in binary from b in binary where a != b && functions[a].Output == functions[b].Output select (a, b)).ToList();
            if (pairs.Count == 0)
                return false;

            var (outer, inner) = Pick(pairs);
            var sort = functions[outer].Output;
            var (x, y, z, t, r) = (NewVar(sort), NewVar(sort), NewVar(sort), NewVar(sort), NewVar(sort));
            AddTemplateAtom(outer, [E(x), E(t), E(r)]);
            AddTemplateAtom(inner, [E(y), E(z), E(t)]);
            var (xy, xz, top) = (NewVar(sort), NewVar(sort), NewVar(sort));
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, outer, default, [E(x), E(y)], xy));
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, outer, default, [E(x), E(z)], xz));
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Lookup, inner, default, [E(xy), E(xz)], top));
            actions.Add(new BridgeDiffAction(BridgeDiffActionKind.Union, -1, default, [E(r), E(top)], -1));
            return true;
        }

        /// <summary>
        /// Adds a template's atom, matching rows that are not subsumed (sometimes any row, for a function that can
        /// subsume).
        /// </summary>
        void AddTemplateAtom(int f, BridgeDiffEntry[] entries)
        {
            bool? subsumed = functions[f].CanSubsume && random.Next(8) == 0 ? null : false;
            atoms.Add(new BridgeDiffAtom(f, entries, subsumed));
        }

        /// <summary>
        /// Gets the entry of variable <paramref name="v"/>, with its sort.
        /// </summary>
        BridgeDiffEntry E(int v) => BridgeDiffEntry.Variable(v, varSorts[v]);

        /// <summary>
        /// Gets the output entry of a relation atom: a new variable, which binds the row's id, as the frontend lowers
        /// <c>(R a b)</c>.
        /// </summary>
        BridgeDiffEntry UnitEntry() => BridgeDiffEntry.Variable(NewVar(BridgeDiffSort.Unit), BridgeDiffSort.Unit);

    }

}
