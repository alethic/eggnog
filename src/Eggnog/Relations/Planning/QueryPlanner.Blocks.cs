using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal static partial class QueryPlanner
    {

        /// <summary>
        /// Plans one bag of the chain into a materializing block. egglog's <c>plan_single_bag</c>
        /// (core-relations/src/free_join/plan.rs:844-973).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each variable of the bag, in ascending order: if a later bag uses it, it is a message variable; otherwise, if
        /// the action reads it or an earlier block passes it as a message variable, it is a value variable (this is its
        /// last bag); otherwise it is neither. Message and value variables are marked used, so the generic-join planner
        /// enumerates them even when they occur once.
        /// </para>
        /// <para>
        /// Earlier blocks, in descending order, whose message variables all lie in this bag and that have not been used
        /// yet constrain this bag: the first becomes a <see cref="MatScanMode.KeyOnly"/> prologue that binds those
        /// variables from the materialization's keys and probes every occurrence of them in the bag; the others become
        /// <see cref="MatScanMode.Lookup"/> epilogues.
        /// </para>
        /// </remarks>
        /// <param name="bag">The bag; its variables' used flags are updated.</param>
        /// <param name="blocks">The blocks planned so far.</param>
        /// <param name="contributed">Per block, whether it already constrains a later bag; updated.</param>
        /// <param name="usage">Per variable, the number of bags not yet planned that hold it; updated.</param>
        /// <param name="strategy">The strategy.</param>
        /// <returns>The bag's header specs, its stages, and what it materializes.</returns>
        static (JoinHeaderSpec[] Headers, JoinStage[] Stages, MatSpec Spec) PlanSingleBag(PlanningContext bag, List<PlanBlock> blocks, bool[] contributed, int[] usage, PlanStrategy strategy)
        {
            var messageVars = new List<Variable>();
            var valueVars = new List<Variable>();
            foreach (var (variable, info) in bag.Variables)
            {
                usage[variable.Index]--;
                if (usage[variable.Index] > 0)
                {
                    // A public variable: pass it on.
                    info.UsedInRhs = true;
                    messageVars.Add(variable);
                    continue;
                }

                // Neither used later, nor passed in by an earlier block, nor read by the action: not expanded.
                if (info.UsedInRhs == false && IsMessageVariableOfAny(blocks, variable) == false)
                    continue;

                valueVars.Add(variable);
                info.UsedInRhs = true;
            }

            var stripped = bag.Clone();
            FusedIntersectMatStage? prologue = null;
            var epilogue = new List<JoinStage>();
            for (var i = blocks.Count - 1; i >= 0; i--)
            {
                var prev = blocks[i].Spec.MessageVariables;
                if (prev.Length == 0 || contributed[i] || ContainsAll(bag, prev) == false)
                    continue;

                contributed[i] = true;
                if (prologue is null)
                {
                    var bind = new StageBinding[prev.Length];
                    for (var j = 0; j < prev.Length; j++)
                        bind[j] = new StageBinding(new ColumnId(j), prev[j]);

                    // Group every occurrence of every message variable by atom, in first-seen order.
                    var probeAtoms = new List<AtomId>();
                    var probeColumns = new List<List<ColumnId>>();
                    var probeKeys = new List<List<ColumnId>>();
                    for (var col = 0; col < prev.Length; col++)
                    {
                        foreach (var occ in bag.Variables[prev[col]].Occurrences)
                        {
                            var k = probeAtoms.IndexOf(occ.Atom);
                            if (k < 0)
                            {
                                probeAtoms.Add(occ.Atom);
                                probeColumns.Add(new List<ColumnId>());
                                probeKeys.Add(new List<ColumnId>());
                                k = probeAtoms.Count - 1;
                            }

                            foreach (var c in occ.Columns)
                            {
                                probeColumns[k].Add(c);
                                probeKeys[k].Add(new ColumnId(col));
                            }
                        }
                    }

                    var probes = new Probe[probeAtoms.Count];
                    for (var k = 0; k < probes.Length; k++)
                        probes[k] = new Probe(new ScanSpec(new SubAtom(probeAtoms[k], probeColumns[k].ToArray()), []), probeKeys[k].ToArray());

                    prologue = new FusedIntersectMatStage(new MatId(i), MatScanMode.KeyOnly, [], bind, probes);
                    stripped.Variables.Retain((v, _) => Array.IndexOf(prev, v) < 0);
                }
                else
                {
                    epilogue.Add(new FusedIntersectMatStage(new MatId(i), MatScanMode.Lookup, (Variable[])prev.Clone(), [], []));
                }
            }

            var (headers, stages) = PlanStages(stripped, strategy);
            if (prologue is not null)
                stages.Insert(0, prologue);
            stages.AddRange(epilogue);

            return (headers, stages.ToArray(), new MatSpec(messageVars.ToArray(), valueVars.ToArray()));
        }

        static bool IsMessageVariableOfAny(List<PlanBlock> blocks, Variable variable)
        {
            foreach (var block in blocks)
                if (Array.IndexOf(block.Spec.MessageVariables, variable) >= 0)
                    return true;

            return false;
        }

        static bool ContainsAll(PlanningContext bag, Variable[] vars)
        {
            foreach (var v in vars)
                if (bag.Variables.ContainsKey(v) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Builds the result block, which reads the materializations back and feeds the action. egglog's
        /// <c>build_result_block</c> (core-relations/src/free_join/plan.rs:975-1016).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Blocks are visited root first. A block with value variables gets a stage binding them: a
        /// <see cref="MatScanMode.Full"/> scan for the last block (whose key is empty), else a
        /// <see cref="MatScanMode.Value"/> lookup keyed by its message variables, which the stages before it have bound
        /// (each message variable is a value variable of the last later bag holding it).
        /// </para>
        /// <para>
        /// A block constrains a later bag only if all its message variables lie in one later bag, and egglog's result
        /// block skips blocks without value variables (plan.rs:897-904 and 993-995). In egglog a block whose message
        /// variables are split across later bags and that has no value variables therefore keeps only "the
        /// materialization is non-empty", and the plan derives false matches (<c>lost3.egg</c>). Here every such block
        /// gets a <see cref="MatScanMode.Lookup"/> of its message variables, appended and then lifted by
        /// <see cref="LoopLifting"/>.
        /// </para>
        /// </remarks>
        /// <param name="blocks">The blocks, leaf first.</param>
        /// <param name="contributed">Per block, whether it constrains a later bag.</param>
        /// <returns>The result block's stages.</returns>
        static JoinStage[] BuildResultBlock(List<PlanBlock> blocks, bool[] contributed)
        {
            var result = new List<JoinStage>();
            var pinned = new HashSet<Variable>();
            for (var i = blocks.Count - 1; i >= 0; i--)
            {
                var spec = blocks[i].Spec;
                var bind = new List<StageBinding>();
                for (var k = 0; k < spec.ValueVariables.Length; k++)
                    if (pinned.Contains(spec.ValueVariables[k]) == false)
                        bind.Add(new StageBinding(new ColumnId(k), spec.ValueVariables[k]));

                if (bind.Count == 0)
                    continue;

                foreach (var b in bind)
                    pinned.Add(b.Variable);

                var last = i == blocks.Count - 1;
                result.Add(new FusedIntersectMatStage(new MatId(i), last ? MatScanMode.Full : MatScanMode.Value, last ? [] : (Variable[])spec.MessageVariables.Clone(), bind.ToArray(), []));
            }

            for (var i = blocks.Count - 1; i >= 0; i--)
            {
                var spec = blocks[i].Spec;
                if (contributed[i] == false && spec.ValueVariables.Length == 0 && spec.MessageVariables.Length > 0)
                    result.Add(new FusedIntersectMatStage(new MatId(i), MatScanMode.Lookup, (Variable[])spec.MessageVariables.Clone(), [], []));
            }

            return result.ToArray();
        }

        /// <summary>
        /// Moves each <see cref="MatScanMode.Lookup"/> stage before every earlier stage that binds none of its variables.
        /// egglog's <c>loop_lifting</c> (core-relations/src/free_join/plan.rs:1061-1100).
        /// </summary>
        /// <param name="stages">The stages; not modified.</param>
        /// <returns>The reordered stages.</returns>
        static JoinStage[] LoopLifting(JoinStage[] stages)
        {
            var instrs = (JoinStage[])stages.Clone();
            for (var i = 1; i < instrs.Length; i++)
            {
                if (instrs[i] is not FusedIntersectMatStage { Mode: MatScanMode.Lookup } lookup)
                    continue;

                if (lookup.Bind.Length != 0 || lookup.ToIntersect.Length != 0)
                    throw new InvalidOperationException("A lookup stage binds and probes nothing.");

                var vars = lookup.ModeVariables;
                var j = i;
                while (j > 0 && BindsNoneOf(instrs[j - 1], vars))
                {
                    (instrs[j - 1], instrs[j]) = (instrs[j], instrs[j - 1]);
                    j--;
                }
            }

            return instrs;
        }

        static bool BindsNoneOf(JoinStage stage, Variable[] vars)
        {
            switch (stage)
            {
                case IntersectStage s:
                    return Array.IndexOf(vars, s.Variable) < 0;
                case FusedIntersectStage f:
                    foreach (var b in f.Bind)
                        if (Array.IndexOf(vars, b.Variable) >= 0)
                            return false;
                    return true;
                case FusedIntersectMatStage m:
                    foreach (var b in m.Bind)
                        if (Array.IndexOf(vars, b.Variable) >= 0)
                            return false;
                    return true;
                default:
                    throw new InvalidOperationException("Unknown stage kind.");
            }
        }

    }

}
