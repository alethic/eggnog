using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A randomly generated rule for <see cref="SemiNaiveDifferentialTests"/>, over the functions of a
    /// <see cref="RuleProgram"/>: the constructor <c>f(id) -&gt; id</c>, the relation <c>r(id, id)</c> (its output is
    /// always the unit value) and the lattice <c>low(id) -&gt; value</c> (minimum). Entries are a variable index
    /// (non-negative) or a constant <c>c</c> encoded as <c>-1 - c</c>.
    /// </summary>
    /// <remarks>
    /// The rule can be built into an e-graph (semi-naively or naively) and evaluated naively over a
    /// <see cref="ReferenceView"/>, so that the engine is checked against a definition with no timestamps, plans or
    /// variants.
    /// </remarks>
    sealed class RandomRule
    {

        /// <summary>
        /// The number of key columns of each function.
        /// </summary>
        public static readonly int[] KeyCounts = [1, 2, 1];

        /// <summary>
        /// The kind of each function's output: an id, the unit value, or a value of <c>low</c>.
        /// </summary>
        public static readonly char[] OutputKinds = ['I', 'U', 'V'];

        /// <summary>
        /// The function names.
        /// </summary>
        public static readonly string[] Names = ["f", "r", "low"];

        /// <summary>
        /// Gets whether each variable holds an id (otherwise a value of <c>low</c>).
        /// </summary>
        public List<bool> VarIsId { get; } = new();

        /// <summary>
        /// Gets the atoms: the function and one entry per column, keys then output.
        /// </summary>
        public List<(int Function, long[] Entries)> Atoms { get; } = new();

        /// <summary>
        /// Gets the left-hand-side filters: pairs of variables whose values must differ.
        /// </summary>
        public List<(int A, int B)> Distinct { get; } = new();

        /// <summary>
        /// Gets the actions: kind 0 unions <c>Args[0]</c> and <c>Args[1]</c>; kind 1 sets a row of <c>Function</c>; kind 2
        /// sets <c>r(Args[0], f(Args[1]))</c>, minting <c>f(Args[1])</c> if it is missing (not supported by
        /// <see cref="Evaluate"/>).
        /// </summary>
        public List<(int Kind, int Function, long[] Args)> Actions { get; } = new();

        /// <summary>
        /// Generates a rule whose constants come from <paramref name="ids"/>, <paramref name="values"/> and
        /// <paramref name="unit"/>.
        /// </summary>
        public static RandomRule Generate(Random random, IReadOnlyList<uint> ids, IReadOnlyList<uint> values, uint unit, bool mint = false)
        {
            var rule = new RandomRule();
            var atomCount = random.Next(10) switch
            {
                < 4 => 1,
                < 8 => 2,
                _ => 3,
            };
            for (var a = 0; a < atomCount; a++)
            {
                var function = random.Next(10) switch
                {
                    < 3 => 0,
                    < 8 => 1,
                    _ => 2,
                };
                var entries = new long[KeyCounts[function] + 1];
                for (var c = 0; c < entries.Length; c++)
                {
                    var kind = c < KeyCounts[function] ? 'I' : OutputKinds[function];
                    entries[c] = kind switch
                    {
                        'I' => rule.PickId(random, ids, join: a > 0 && c == 0),
                        'U' => Constant(unit),
                        _ => rule.PickValue(random),
                    };
                }

                rule.Atoms.Add((function, entries));
            }

            var idVars = Enumerable.Range(0, rule.VarIsId.Count).Where(v => rule.VarIsId[v]).ToList();
            if (idVars.Count >= 2 && random.Next(10) < 3)
                rule.Distinct.Add((idVars[random.Next(idVars.Count)], idVars[random.Next(idVars.Count)]));

            var actionCount = 1 + random.Next(2);
            for (var i = 0; i < actionCount && idVars.Count > 0; i++)
            {
                var roll = random.Next(20);
                if (mint && roll < 5)
                {
                    rule.Actions.Add((2, 1, [PickBound(random, idVars), PickBound(random, idVars)]));
                }
                else if (roll < 2)
                {
                    rule.Actions.Add((0, -1, [PickBound(random, idVars), PickBound(random, idVars)]));
                }
                else if (roll < 6)
                {
                    rule.Actions.Add((1, 0, [PickBound(random, idVars), PickBound(random, idVars)]));
                }
                else if (roll < 16)
                {
                    rule.Actions.Add((1, 1, [PickBound(random, idVars), PickBound(random, idVars), Constant(unit)]));
                }
                else
                {
                    var valueVars = Enumerable.Range(0, rule.VarIsId.Count).Where(v => rule.VarIsId[v] == false).ToList();
                    var value = valueVars.Count > 0 && random.Next(2) == 0 ? valueVars[random.Next(valueVars.Count)] : Constant(values[random.Next(values.Count)]);
                    rule.Actions.Add((1, 2, [PickBound(random, idVars), value]));
                }
            }

            return rule;
        }

        /// <summary>
        /// Builds the rule into <paramref name="program"/>'s e-graph.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="semiNaive">Whether the rule is semi-naive.</param>
        /// <param name="description">The rule's description.</param>
        /// <returns>The rule's id.</returns>
        public EGraphRuleId Build(RuleProgram program, bool semiNaive, string description)
        {
            var rb = program.EGraph.NewRule(description, semiNaive);
            var vars = VarIsId.Select((isId, i) => rb.NewVarNamed(isId ? ColumnTy.Id : program.ValueType, "v" + i)).ToArray();

            RuleEntry Entry(long e) => e >= 0 ? vars[e] : RuleEntry.Const(new Value(Decode(e)), ColumnTy.Id);

            foreach (var (function, entries) in Atoms)
                rb.QueryTable(program.Functions[function], entries.Select(Entry).ToArray(), false);

            foreach (var (a, b) in Distinct)
                rb.QueryPrim(program.Distinct, [vars[a], vars[b], rb.NewVar(ColumnTy.Id)], ColumnTy.Id);

            foreach (var (kind, function, args) in Actions)
            {
                if (kind == 0)
                {
                    rb.Union(Entry(args[0]), Entry(args[1]));
                }
                else if (kind == 2)
                {
                    var minted = rb.Lookup(program.Functions[0], [Entry(args[1])], () => "f");
                    rb.Set(program.Functions[1], [Entry(args[0]), minted, RuleEntry.Const(new Value(program.Unit), program.UnitType)]);
                }
                else
                {
                    rb.Set(program.Functions[function], args.Select(Entry).ToArray());
                }
            }

            return rb.Build();
        }

        /// <summary>
        /// Evaluates the rule over the closed <paramref name="rows"/> (a snapshot of the reference), collecting the rows
        /// and unions its matches write.
        /// </summary>
        /// <remarks>
        /// The rule's constants in id columns run in their canonical form in the snapshot (<paramref name="find"/>), as a
        /// store-backed e-graph runs every Id constant of a rule (colored-contexts spec X19, §19 item 4); value constants
        /// run as written.
        /// </remarks>
        /// <param name="rows">The rows of each function.</param>
        /// <param name="writes">Receives the rows to add, by function.</param>
        /// <param name="unions">Receives the unions.</param>
        /// <param name="find">The snapshot's canonical id of an id.</param>
        public void Evaluate(IReadOnlyList<uint[]>[] rows, List<(int Function, uint[] Row)> writes, List<(uint, uint)> unions, Func<uint, uint> find)
        {
            var binding = new uint[VarIsId.Count];
            var bound = new bool[VarIsId.Count];
            this.find = find;
            Match(rows, 0, binding, bound, writes, unions);
        }

        /// <summary>
        /// The canonical id of an id in the snapshot being evaluated.
        /// </summary>
        Func<uint, uint> find = static id => id;

        /// <summary>
        /// Gets whether column <paramref name="column"/> of function <paramref name="function"/> holds ids.
        /// </summary>
        static bool IsIdColumn(int function, int column) => column < KeyCounts[function] || OutputKinds[function] == 'I';

        /// <summary>
        /// Gets the cell a constant entry of an id column (when <paramref name="isId"/>) or a value column runs as.
        /// </summary>
        uint ConstantCell(long entry, bool isId) => isId ? find(Decode(entry)) : Decode(entry);

        /// <inheritdoc />
        public override string ToString()
        {
            static string E(long e) => e >= 0 ? "v" + e : "#" + Decode(e);
            var b = new StringBuilder();
            b.AppendJoin(", ", Atoms.Select(a => $"{Names[a.Function]}({string.Join(", ", a.Entries.Select(E))})"));
            foreach (var (x, y) in Distinct)
                b.Append($", v{x} != v{y}");

            b.Append(" => ");
            b.AppendJoin("; ", Actions.Select(a => a.Kind switch
            {
                0 => $"union({E(a.Args[0])}, {E(a.Args[1])})",
                1 => $"set {Names[a.Function]}({string.Join(", ", a.Args.Select(E))})",
                _ => $"set r({E(a.Args[0])}, f({E(a.Args[1])}))",
            }));
            return b.ToString();
        }

        /// <summary>
        /// Encodes the constant <paramref name="value"/> as an entry.
        /// </summary>
        static long Constant(uint value) => -1L - value;

        /// <summary>
        /// Decodes a constant entry.
        /// </summary>
        static uint Decode(long entry) => (uint)(-1L - entry);

        /// <summary>
        /// Picks one of the id variables the left-hand side binds.
        /// </summary>
        static long PickBound(Random random, List<int> idVars) => idVars[random.Next(idVars.Count)];

        /// <summary>
        /// Enumerates the matches of atoms <paramref name="atom"/> onwards by nested loops, applying the actions to each.
        /// </summary>
        void Match(IReadOnlyList<uint[]>[] rows, int atom, uint[] binding, bool[] bound, List<(int Function, uint[] Row)> writes, List<(uint, uint)> unions)
        {
            if (atom == Atoms.Count)
            {
                foreach (var (x, y) in Distinct)
                    if (binding[x] == binding[y])
                        return;

                uint Value(long e, bool isId) => e >= 0 ? binding[e] : ConstantCell(e, isId);
                foreach (var (kind, function, args) in Actions)
                {
                    if (kind == 0)
                        unions.Add((Value(args[0], true), Value(args[1], true)));
                    else if (kind == 1)
                        writes.Add((function, args.Select((a, c) => Value(a, IsIdColumn(function, c))).ToArray()));
                    else
                        throw new NotSupportedException("The reference does not mint ids.");
                }

                return;
            }

            var (f, entries) = Atoms[atom];
            foreach (var row in rows[f])
            {
                var newlyBound = new List<long>();
                var ok = true;
                for (var c = 0; c < entries.Length && ok; c++)
                {
                    var e = entries[c];
                    if (e < 0)
                    {
                        ok = row[c] == ConstantCell(e, IsIdColumn(f, c));
                    }
                    else if (bound[e])
                    {
                        ok = binding[e] == row[c];
                    }
                    else
                    {
                        bound[e] = true;
                        binding[e] = row[c];
                        newlyBound.Add(e);
                    }
                }

                if (ok)
                    Match(rows, atom + 1, binding, bound, writes, unions);

                foreach (var e in newlyBound)
                    bound[e] = false;
            }
        }

        /// <summary>
        /// Picks the entry of an id column of an atom: rarely a constant; an existing variable usually for the column
        /// that joins the atom to the earlier ones (<paramref name="join"/>), sometimes otherwise; else a new variable.
        /// </summary>
        long PickId(Random random, IReadOnlyList<uint> ids, bool join)
        {
            var existing = Enumerable.Range(0, VarIsId.Count).Where(v => VarIsId[v]).ToList();
            var roll = random.Next(20);
            if (roll < 1)
                return Constant(ids[random.Next(ids.Count)]);
            if (existing.Count > 0 && roll < (join ? 17 : 4))
                return existing[random.Next(existing.Count)];

            VarIsId.Add(true);
            return VarIsId.Count - 1;
        }

        /// <summary>
        /// Picks the entry of <c>low</c>'s output column: usually a new value variable.
        /// </summary>
        long PickValue(Random random)
        {
            var existing = Enumerable.Range(0, VarIsId.Count).Where(v => VarIsId[v] == false).ToList();
            if (existing.Count > 0 && random.Next(10) < 3)
                return existing[random.Next(existing.Count)];

            VarIsId.Add(false);
            return VarIsId.Count - 1;
        }

    }

}
