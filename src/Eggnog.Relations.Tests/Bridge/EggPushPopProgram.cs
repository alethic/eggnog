using System;
using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An egglog program translated by hand into <see cref="EGraph"/> and <see cref="EGraphContext"/> calls, for
    /// <see cref="EggPushPopTests"/> (colored-contexts spec §17 M10.3; map §6's M10 gate). There is no <c>.egg</c> reader
    /// (rule-layer D14); this keeps the translations short and close to the source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Commands map as follows. <c>(push)</c> is <see cref="EGraphContext.CreateChild"/> on the current context and
    /// <c>(pop)</c> is <see cref="EGraphContext.Dispose"/>. A top-level term is <see cref="EGraphContext.AddTerm"/>, a
    /// top-level <c>union</c> is <see cref="EGraphContext.StageUnion"/> then <see cref="EGraphContext.FlushUpdates"/>,
    /// <c>print-size</c> is <see cref="EGraphContext.TableSize"/>, and a <c>check</c> of terms looks each term up without
    /// inserting it (<see cref="TryTerm"/>), as egglog's check queries them. <c>(run n :until facts)</c> is
    /// <see cref="Run"/>, which follows egglog's <c>run_schedule</c> (src/lib.rs:965-1046): up to n iterations, each
    /// preceded by the <c>until</c> check, stopping after an iteration whose rule application changed nothing.
    /// </para>
    /// <para>
    /// A global <c>(let $x (C))</c> of a nullary constructor stays a C# variable: egglog keeps it in a nullary function
    /// <c>$x</c>, which <c>print-size</c> does not list, and a rule that mentions <c>$x</c> matches <c>C()</c> instead,
    /// which binds the same class. Definitions made inside a push are made on the e-graph while the child lives (they
    /// are never sealed, colored-contexts §4.3) and outlive the pop, unused; egglog drops them with the popped copy.
    /// </para>
    /// </remarks>
    sealed class EggPushPopProgram
    {

        readonly List<EGraphContext> stack = new();

        /// <summary>
        /// Initializes an empty serial e-graph (decision 4: golden tests run serially).
        /// </summary>
        public EggPushPopProgram()
        {
            EGraph = new EGraph(ParallelHeuristics.Serial);
            I64 = ColumnTy.Base(EGraph.BaseValues.RegisterType<Int64Codec, long>());
            UnitType = ColumnTy.Base(EGraph.BaseValues.RegisterType<UnitCodec, ValueTuple>());
            Unit = EGraph.BaseValues.Get<UnitCodec, ValueTuple>(default);
            stack.Add(EGraph.Root);
        }

        public EGraph EGraph { get; }

        public ColumnTy I64 { get; }

        public ColumnTy UnitType { get; }

        public Value Unit { get; }

        /// <summary>
        /// Gets the context commands run in: the root, or the innermost live push.
        /// </summary>
        public EGraphContext Current => stack[^1];

        /// <summary>
        /// Gets the number of live pushes.
        /// </summary>
        public int Depth => stack.Count - 1;

        /// <summary>
        /// <c>(push)</c>: a child of the current context becomes current.
        /// </summary>
        public void Push() => stack.Add(Current.CreateChild());

        /// <summary>
        /// <c>(pop)</c>: the current child is disposed and its parent becomes current again.
        /// </summary>
        public void Pop()
        {
            Assert.True(stack.Count > 1, "pop without push");
            var child = stack[^1];
            child.CheckInvariants();
            stack.RemoveAt(stack.Count - 1);
            child.Dispose();
        }

        /// <summary>
        /// <c>(constructor name (keys...) Sort)</c>, or a <c>datatype</c> variant: fresh-id default, union-id merge.
        /// </summary>
        public FunctionId Constructor(string name, params ColumnTy[] keys)
        {
            return EGraph.AddTable(new FunctionConfig
            {
                Schema = [.. keys, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = name,
            });
        }

        /// <summary>
        /// <c>(relation name (keys...))</c>: a unit output with a constant default and an assert-eq merge, as egglog
        /// lowers relations.
        /// </summary>
        public FunctionId Relation(string name, params ColumnTy[] keys)
        {
            return EGraph.AddTable(new FunctionConfig
            {
                Schema = [.. keys, UnitType],
                Default = DefaultVal.Const(Unit),
                Merge = MergeFn.AssertEq,
                Name = name,
            });
        }

        /// <summary>
        /// Registers an external function from a lambda over raw cells that returns <c>null</c> to produce nothing.
        /// </summary>
        public ExternalFunctionId Primitive(Func<ExecutionState, uint[], uint?> body) => BridgeTestUtil.Register(EGraph, body);

        /// <summary>
        /// Boxes an i64.
        /// </summary>
        public Value Int(long value) => EGraph.BaseValues.Get<Int64Codec, long>(value);

        /// <summary>
        /// A top-level term (or <c>let</c>): inserts it in the current context and returns its canonical id.
        /// </summary>
        public Value Term(FunctionId function, params Value[] keys) => Current.AddTerm(function, keys);

        /// <summary>
        /// A top-level relation fact: stages the row and flushes.
        /// </summary>
        public void Fact(FunctionId relation, params Value[] keys)
        {
            Current.StageRow(relation, [.. keys, Unit]);
            Current.FlushUpdates();
        }

        /// <summary>
        /// A top-level <c>(union a b)</c>: stages the union and flushes, which rebuilds.
        /// </summary>
        public void Union(Value a, Value b)
        {
            Current.StageUnion(a, b);
            Current.FlushUpdates();
        }

        /// <summary>
        /// Looks a term up in the current context without inserting it: <c>null</c> when an argument is missing or no row
        /// has the key. What a <c>check</c> of a term matches.
        /// </summary>
        public Value? TryTerm(FunctionId function, params Value?[] keys)
        {
            var key = new Value[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                if (keys[i] is not { } k)
                    return null;

                key[i] = k;
            }

            return Current.TryLookup(function, key, out var value) ? Current.Find(value) : null;
        }

        /// <summary>
        /// <c>(check (= a b))</c> over looked-up terms: both exist and are equal in the current context.
        /// </summary>
        public bool Equal(Value? a, Value? b) => a is { } x && b is { } y && Current.Find(x) == Current.Find(y);

        /// <summary>
        /// <c>(run limit :until facts)</c> on <paramref name="rules"/> (egglog's whole default ruleset) in the current
        /// context: before each iteration the <paramref name="until"/> facts are checked and end the run when they hold;
        /// an iteration whose rule application changed nothing ends it too (egglog's <c>can_stop</c>, which is
        /// <c>!changed</c> of the iteration report, egglog-reports/src/lib.rs:248-249). Returns the iterations run.
        /// </summary>
        public int Run(EGraphRuleId[] rules, int limit, Func<bool>? until = null)
        {
            var iterations = 0;
            for (var i = 0; i < limit; i++)
            {
                if (until is not null && until())
                    break;

                var report = Current.RunRules(rules);
                iterations++;
                if (report.Changed == false)
                    break;
            }

            return iterations;
        }

        /// <summary>
        /// <c>(print-size f)</c> in the current context.
        /// </summary>
        public int Size(FunctionId function) => Current.TableSize(function);

        /// <summary>
        /// Plans every rule (if a run has not) and asserts it may inherit watermarks (§10.2): the condition under which
        /// egglog's push, which copies <c>last_run_at</c>, coincides with watermark inheritance.
        /// </summary>
        public void AssertAllInherit(IEnumerable<EGraphRuleId> rules)
        {
            foreach (var rule in rules)
            {
                var info = EGraph.GetRuleInfo(rule);
                info.GetOrBuildCachedPlan(EGraph.Database);
                Assert.True(info.AllowInheritance, $"rule {info.Description} does not inherit watermarks");
            }
        }

    }

}
