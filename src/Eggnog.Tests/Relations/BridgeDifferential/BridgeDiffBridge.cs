using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A bridge differential program run on an <see cref="EGraph"/>, with every rule semi-naive or every rule naive:
    /// the system under test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Functions are declared as egglog's frontend declares them (egglog/src/lib.rs:789-850): a constructor with a
    /// fresh-id default and the union-id merge; a relation the same way, as the frontend desugars it into a constructor
    /// into a fresh sort of its own (egglog/src/ast/desugar.rs:396-427), so its output column holds ids; the integer
    /// lattices with a failing default and <c>(min old new)</c> or <c>(max old new)</c>, declared lattices; an id
    /// function with a failing default and the union-id merge. Whether a function has a subsumption column is the
    /// program's choice (egglog gives every constructor and relation one; the generator gives most of them one). The
    /// primitives are <see cref="BridgeDiffPrimitives"/> registered as external functions.
    /// </para>
    /// <para>
    /// Seeds go through the e-graph's own entry points: <see cref="EGraph.AddTerm"/> for terms and relation insertions,
    /// <see cref="EGraph.AddValues"/> for the other facts (consecutive ones in one call) and for bulk loads, and a
    /// union through the union action followed by <see cref="EGraph.FlushUpdates"/>, with id arguments canonicalized
    /// first, as the frontend's are. Rules are built with <see cref="EGraphRuleBuilder"/> as the frontend lowers them:
    /// a relation atom without an output binds a fresh variable, and a relation insertion is a lookup. Every iteration
    /// runs the rules the program's schedule lists with <see cref="EGraph.RunRules"/>.
    /// </para>
    /// <para>
    /// With parallel settings the e-graph runs on four threads and always takes the parallel rule-set executor
    /// (<see cref="Database.ParallelRuleSetsOverride"/>), which egglog takes only above 10,000 rows, so that small
    /// programs exercise it too; table operations still go parallel only above the engine's thresholds.
    /// </para>
    /// </remarks>
    sealed class BridgeDiffBridge
    {

        readonly BridgeDiffProgram program;
        readonly bool semiNaive;
        readonly FunctionId[] functions;
        readonly ExternalFunctionId[] primitives;
        readonly ColumnTy i64;
        readonly ColumnTy unitType;
        readonly uint[] seedIds;
        EGraphRuleId[] rules = [];
        int nextSeed;
        int initialIds = -1;

        /// <summary>
        /// Initializes the e-graph and declares the program's functions.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="semiNaive">Whether rules are semi-naive.</param>
        /// <param name="parallel">Whether the e-graph uses four threads and the parallel rule-set executor rather than
        /// one thread.</param>
        public BridgeDiffBridge(BridgeDiffProgram program, bool semiNaive, bool parallel)
        {
            this.program = program;
            this.semiNaive = semiNaive;
            EGraph = new EGraph(parallel ? new ParallelHeuristics(4) : ParallelHeuristics.Serial);
            if (parallel)
                EGraph.Database.ParallelRuleSetsOverride = true;

            i64 = ColumnTy.Base(EGraph.BaseValues.RegisterType<Int64Codec, long>());
            unitType = ColumnTy.Base(EGraph.BaseValues.RegisterType<UnitCodec, ValueTuple>());
            Unit = EGraph.BaseValues.Get<UnitCodec, ValueTuple>(default).Rep;
            primitives = BridgeDiffPrimitives.Register(EGraph, Unit);
            functions = program.Functions.Select(Declare).ToArray();
            seedIds = Enumerable.Repeat(uint.MaxValue, program.Seeds.Length).ToArray();
        }

        /// <summary>
        /// Gets the e-graph.
        /// </summary>
        public EGraph EGraph { get; }

        /// <summary>
        /// Gets the cell of the unit value.
        /// </summary>
        public uint Unit { get; }

        /// <summary>
        /// Runs the seeds of phase <paramref name="phase"/>, which must come next.
        /// </summary>
        /// <param name="phase">The phase: 0 before the rules, <c>k</c> before iteration <c>k</c>.</param>
        public void RunSeeds(int phase)
        {
            var seeds = program.Seeds;
            var declared = program.Functions;
            while (nextSeed < seeds.Length && seeds[nextSeed].Phase == phase)
            {
                var seed = seeds[nextSeed];
                switch (seed.Kind)
                {
                    case BridgeDiffSeedKind.Term:
                        seedIds[nextSeed] = EGraph.AddTerm(functions[seed.Function], seed.Args.Select(Resolve).ToArray()).Rep;
                        nextSeed++;
                        break;
                    case BridgeDiffSeedKind.Set when seed.IsInsertion(declared):
                        // egglog's (R a b): the relation's row, with a fresh id that the merge unions with an existing
                        // one.
                        seedIds[nextSeed] = EGraph.AddTerm(functions[seed.Function], seed.Args.Take(declared[seed.Function].KeyCount).Select(Resolve).ToArray()).Rep;
                        nextSeed++;
                        break;
                    case BridgeDiffSeedKind.Union:
                        BridgeTestUtil.Union(EGraph, Resolve(seed.Args[0]), Resolve(seed.Args[1]));
                        nextSeed++;
                        break;
                    case BridgeDiffSeedKind.Bulk:
                    {
                        // Fresh ids in key order, as the oracle mints them.
                        var (first, count) = (seed.Args[0].Value, seed.Args[1].Value);
                        var rows = new List<(FunctionId, Value[])>((int)count);
                        for (var k = first; k < first + count; k++)
                            rows.Add((functions[seed.Function], [new Value(k), EGraph.FreshId()]));

                        EGraph.AddValues(rows);
                        nextSeed++;
                        break;
                    }
                    default:
                    {
                        // Consecutive facts of the phase load in one call, which stages them per table and merges once.
                        var rows = new List<(FunctionId, Value[])>();
                        while (nextSeed < seeds.Length && seeds[nextSeed].Phase == phase && seeds[nextSeed].Kind == BridgeDiffSeedKind.Set && seeds[nextSeed].IsInsertion(declared) == false)
                        {
                            rows.Add((functions[seeds[nextSeed].Function], seeds[nextSeed].Args.Select(Resolve).ToArray()));
                            nextSeed++;
                        }

                        EGraph.AddValues(rows);
                        break;
                    }
                }
            }

            if (phase == 0)
                initialIds = checked((int)EGraph.Database.ReadCounter(EGraph.IdCounter));
        }

        /// <summary>
        /// Builds the program's rules into the e-graph.
        /// </summary>
        public void BuildRules() => rules = program.Rules.Select(Build).ToArray();

        /// <summary>
        /// Runs one iteration of the given rules.
        /// </summary>
        /// <param name="ruleIndexes">The rules to run, by index in the program.</param>
        /// <returns>The iteration's report.</returns>
        /// <exception cref="PanicException">A rule panicked: a read of a function without a default missed.</exception>
        public IterationReport Step(int[] ruleIndexes) => EGraph.RunRules(ruleIndexes.Select(r => rules[r]).ToArray());

        /// <summary>
        /// Takes the e-graph's view: the canonical id of every id minted, and every function's rows.
        /// </summary>
        /// <returns>The view.</returns>
        public BridgeDiffView View()
        {
            var count = checked((int)EGraph.Database.ReadCounter(EGraph.IdCounter));
            var find = new uint[count];
            for (var id = 0; id < count; id++)
                find[id] = EGraph.GetCanonInUf(new Value((uint)id)).Rep;

            var rows = new List<BridgeDiffRow>[functions.Length];
            for (var f = 0; f < functions.Length; f++)
            {
                var list = new List<BridgeDiffRow>();
                EGraph.ForEach(functions[f], row => list.Add(new BridgeDiffRow(MemoryMarshal.Cast<Value, uint>(row.Values).ToArray(), row.Subsumed)));
                rows[f] = list;
            }

            return new BridgeDiffView(find, rows, (uint[])seedIds.Clone(), initialIds);
        }

        /// <summary>
        /// Gets the cell of a constant entry: its value, or the unit value for a constant of the unit sort.
        /// </summary>
        uint Constant(BridgeDiffEntry entry) => entry.Sort == BridgeDiffSort.Unit ? Unit : entry.Value;

        /// <summary>
        /// Gets the column type of a sort.
        /// </summary>
        ColumnTy Type(int sort) => sort switch
        {
            BridgeDiffSort.I64 => i64,
            BridgeDiffSort.Unit => unitType,
            _ => ColumnTy.Id,
        };

        /// <summary>
        /// Gets the column type of column <paramref name="column"/> of <paramref name="function"/>: the id type for an
        /// id column (a relation's output included), else its sort's.
        /// </summary>
        ColumnTy ColumnType(BridgeDiffFunction function, int column) => function.IsIdColumn(column) ? ColumnTy.Id : Type(function.SortOf(column));

        /// <summary>
        /// Declares a function as the frontend would.
        /// </summary>
        FunctionId Declare(BridgeDiffFunction function)
        {
            var schema = Enumerable.Range(0, function.Columns).Select(c => ColumnType(function, c)).ToArray();
            var (defaultVal, merge) = function.Kind switch
            {
                BridgeDiffFunctionKind.Constructor or BridgeDiffFunctionKind.Relation => (DefaultVal.FreshId, MergeFn.UnionId),
                BridgeDiffFunctionKind.Min => (DefaultVal.Fail, MergeFn.Primitive(primitives[(int)BridgeDiffPrimitive.Min], MergeFn.Old, MergeFn.New).AsLattice()),
                BridgeDiffFunctionKind.Max => (DefaultVal.Fail, MergeFn.Primitive(primitives[(int)BridgeDiffPrimitive.Max], MergeFn.Old, MergeFn.New).AsLattice()),
                _ => (DefaultVal.Fail, MergeFn.UnionId),
            };
            return EGraph.AddTable(new FunctionConfig
            {
                Schema = schema,
                Default = defaultVal,
                Merge = merge,
                Name = function.Name,
                CanSubsume = function.CanSubsume,
            });
        }

        /// <summary>
        /// Resolves a seed argument: the canonical id of an earlier term seed's class, or the constant.
        /// </summary>
        Value Resolve(BridgeDiffEntry entry)
        {
            if (entry.IsVar == false)
                return new Value(Constant(entry));

            var id = seedIds[entry.Var];
            if (id == uint.MaxValue)
                throw new InvalidOperationException($"Seed $s{entry.Var} produced no id.");

            return EGraph.GetCanonRepr(new Value(id), ColumnTy.Id);
        }

        /// <summary>
        /// Builds one rule: its atoms, then its primitives, then its actions, in order.
        /// </summary>
        EGraphRuleId Build(BridgeDiffRule rule)
        {
            var rb = EGraph.NewRule(rule.Name, semiNaive);
            rb.PlanStrategy = rule.Strategy;
            rb.NoDecomposition = rule.NoDecomposition;

            // Variables are created on first use, with the type of the column that binds them (so a unit-sorted
            // variable bound to a relation's output is an id), and a variable shrinking left unused never reaches the
            // bridge.
            var vars = new RuleVariable?[rule.VarSorts.Length];
            RuleEntry Entry(BridgeDiffEntry e, ColumnTy type)
            {
                if (e.IsVar == false)
                    return RuleEntry.Const(new Value(Constant(e)), Type(e.Sort));

                return vars[e.Var] ??= rb.NewVarNamed(type, "v" + e.Var);
            }

            RuleEntry Bound(BridgeDiffEntry e) => Entry(e, e.IsVar ? Type(rule.VarSorts[e.Var]) : Type(e.Sort));

            RuleEntry[] Keys(BridgeDiffAction action) => action.Entries.Take(program.Functions[action.Function].KeyCount).Select(Bound).ToArray();

            foreach (var atom in rule.Atoms)
            {
                var function = program.Functions[atom.Function];
                var entries = new RuleEntry[function.Columns];
                for (var c = 0; c < entries.Length; c++)
                {
                    // A relation atom without an output binds a fresh variable, as the frontend lowers (R a b).
                    entries[c] = c == function.KeyCount && function.IsNoOutput(atom.Entries[c])
                        ? rb.NewVar(ColumnTy.Id)
                        : Entry(atom.Entries[c], ColumnType(function, c));
                }

                rb.QueryTable(functions[atom.Function], entries, atom.Subsumed);
            }

            foreach (var prim in rule.Prims)
                rb.QueryPrim(primitives[(int)prim.Primitive], [Bound(prim.Left), Bound(prim.Right), Entry(prim.Result, Type(BridgeDiffPrimitives.ResultSort(prim.Primitive)))], Type(BridgeDiffPrimitives.ResultSort(prim.Primitive)));

            foreach (var action in rule.Actions)
            {
                switch (action.Kind)
                {
                    case BridgeDiffActionKind.Lookup:
                        vars[action.Result] = rb.Lookup(functions[action.Function], action.Entries.Select(Bound).ToArray(), () => $"lookup in {rule.Name} failed");
                        break;
                    case BridgeDiffActionKind.Set when program.Functions[action.Function].IsNoOutput(action.Entries[^1]):
                        // egglog's (R a b) action: a lookup that inserts the key with a fresh id.
                        rb.Lookup(functions[action.Function], Keys(action), () => $"insertion in {rule.Name} failed");
                        break;
                    case BridgeDiffActionKind.Set:
                        rb.Set(functions[action.Function], action.Entries.Select(Bound).ToArray());
                        break;
                    case BridgeDiffActionKind.Union:
                        rb.Union(Bound(action.Entries[0]), Bound(action.Entries[1]));
                        break;
                    case BridgeDiffActionKind.Subsume:
                        rb.Subsume(functions[action.Function], action.Entries.Select(Bound).ToArray());
                        break;
                    default:
                        vars[action.Result] = rb.CallExternalFunc(primitives[(int)action.Primitive], action.Entries.Select(Bound).ToArray(), i64, () => $"call in {rule.Name} failed");
                        break;
                }
            }

            return rb.Build();
        }

    }

}
