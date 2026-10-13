using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class JoinRun
    {

        /// <summary>
        /// Runs one stage list of the current rule, sending every match to <paramref name="sink"/> as a match of
        /// <paramref name="matchTarget"/>. egglog's <c>run_join_stages</c> (core-relations/src/free_join/execute.rs:895-935):
        /// return at once if some atom has no rows, sort the stages by current size, and run the plan from position 0.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="blockStages">The stages: a single plan's, or one block of a decomposed plan.</param>
        /// <param name="matchTarget">The action or materialization index matches are sent to.</param>
        /// <param name="sink">The sink.</param>
        public void RunJoinStages<TSink>(JoinStage[] blockStages, int matchTarget, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            ArgumentNullException.ThrowIfNull(blockStages);
            for (var a = 0; a < atomCount; a++)
                if (nodes[a] is { IsEmpty: true })
                    return;

            stages = blockStages;
            var block = PreparedBlock.Get(blockStages);
            prepared = block;
            target = matchTarget;
            AddBound(block.BoundVariables);
            order.Reset(blockStages.Length);
            if (leafScans.Length < blockStages.Length)
                leafScans = new bool[blockStages.Length];
            else
                Array.Clear(leafScans);

            EnsurePositions(blockStages.Length);
            EnsureKeyScratch(block);
            var sizes = new JoinSizes(this);
            StageOrdering.SortPlanBySize(order, leafScans, 0, blockStages, ref sizes, timesRefined, probedBy);
            RunPlan(0, ref sink);
        }

        /// <summary>
        /// Replays the frames of a morsel handed to this task, recursing into position <paramref name="cur"/> + 1 after
        /// each, then resets the task's scratch. The work of egglog's <c>drain_updates_parallel</c> task
        /// (core-relations/src/free_join/execute.rs:1047-1085).
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="cur">The position the frames were produced at.</param>
        /// <param name="updates">The frames.</param>
        /// <param name="sink">The task's sink.</param>
        public void ReplayMorsel<TSink>(int cur, FrameUpdates updates, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            ArgumentNullException.ThrowIfNull(updates);
            if (signals.ShouldStop == false)
                Replay(cur, updates, ref sink);

            framePool?.Return(updates);
            ResetScratch();
        }

        /// <summary>
        /// Runs the stage at position <paramref name="cur"/> and, through its drains, every position after it. egglog's
        /// <c>run_plan</c> (core-relations/src/free_join/execute.rs:937-1110).
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="cur">The position.</param>
        /// <param name="sink">The sink.</param>
        void RunPlan<TSink>(int cur, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            if (signals.ShouldStop)
                return;

            var count = order.Count;
            if (cur >= count)
            {
                ExpandBindingSets(0, ref sink);
                return;
            }

            var chunk = sink.MorselSize(cur, count);
            var index = order[cur];
            if (cur % 3 == 1 && cur < count - 1)
            {
                // Adjust the order every three positions, starting at the second, when there is enough to enumerate.
                var sizes = new JoinSizes(this);
                if (StageOrdering.EstimateSize(stages[index], ref sizes) > ResortThreshold)
                {
                    StageOrdering.SortPlanBySize(order, leafScans, cur, stages, ref sizes, timesRefined, probedBy);
                    index = order[cur];
                }
            }

            var stage = stages[index];
            var info = prepared!.Stages[index];
            switch (stage.Kind)
            {
                case JoinStageKind.Intersect:
                    RunIntersect(cur, Unsafe.As<IntersectStage>(stage), chunk, ref sink);
                    break;
                case JoinStageKind.FusedIntersect:
                    var fused = Unsafe.As<FusedIntersectStage>(stage);
                    if (fused.ToIntersect.Length == 0)
                        RunFusedScan(cur, fused, info, chunk, ref sink);
                    else
                        RunFusedIntersect(cur, fused, info, chunk, ref sink);
                    break;
                default:
                    var mat = Unsafe.As<FusedIntersectMatStage>(stage);
                    if (leafScans[cur] && mat.ToIntersect.Length == 0 && mat.Mode != MatScanMode.Lookup)
                        RunMatLeafScan(cur, mat, info, ref sink);
                    else
                        RunMat(cur, mat, info, chunk, ref sink);
                    break;
            }
        }

        /// <summary>
        /// Drains the frames of position <paramref name="cur"/>: hands them to another task at positions 0 and 1 when
        /// the sink runs in parallel, and replays them otherwise. egglog's <c>drain_updates!</c>
        /// (core-relations/src/free_join/execute.rs:981-1030).
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="cur">The position.</param>
        /// <param name="updates">The position's buffer; replaced by a fresh one when handed off.</param>
        /// <param name="sink">The sink.</param>
        /// <returns><c>false</c> if the run was stopped, in which case the stage returns at once.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Drain<TSink>(int cur, ref FrameUpdates updates, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            if (signals.ShouldStop)
                return false;

            // egglog's drain_updates_parallel: the frames move to a task with a copy of this state. A single frame (a
            // leaf scan, a lookup, the last candidate) is replayed here instead: a task would add a state copy and a
            // flush without adding parallelism, since the action batches it fills are spawned either way. So are the
            // frames of a backlogged scope (WorkScope.IsBacklogged): the same frames, run sooner.
            if (cur <= 1 && sink.SupportsParallelDrain && updates.Frames > 1 && sink.TryRecur(this, cur, updates))
            {
                updates = framePool?.Rent() ?? new FrameUpdates();
                frames[cur] = updates;
                return true;
            }

            return Replay(cur, updates, ref sink);
        }

        /// <summary>
        /// Replays every frame of <paramref name="updates"/>: applies its bindings and refinements and recurses into
        /// position <paramref name="cur"/> + 1 at its end (calling the sink directly when that is the leaf), then
        /// releases the nodes the frame created, unless the run may share them.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="cur">The position the frames were produced at.</param>
        /// <param name="updates">The frames; empty afterwards.</param>
        /// <param name="sink">The sink.</param>
        /// <returns><c>false</c> if the run was stopped.</returns>
        bool Replay<TSink>(int cur, FrameUpdates updates, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            // Nodes installed at position 0 of a parallel run reach the tasks forked at position 1, if there is one:
            // never reuse or release them there.
            var leaf = cur + 1 >= order.Count;
            var exclusive = parallel == false || cur >= 1 || leaf;
            var entries = updates.Entries;
            var count = updates.Count;
            var frameStart = 0;
            for (var i = 0; i < count; i++)
            {
                ref var e = ref entries[i];
                switch (e.Kind)
                {
                    case FrameUpdateKind.PushBinding:
                        values[e.Id] = (uint)e.A;
                        break;
                    case FrameUpdateKind.RefineAtomShared:
                    case FrameUpdateKind.RefineAtomOwned:
                        nodes[e.Id] = e.Node;
                        break;
                    case FrameUpdateKind.RefineAtomDense:
                        InsertDense(cur, e.Id, e.Layer, new OffsetRange(e.A, e.B), exclusive);
                        break;
                    default:
                        if (leaf)
                            ExpandBindingSets(0, ref sink);
                        else
                            RunPlan(cur + 1, ref sink);

                        if (exclusive)
                            ReleaseFrame(entries, frameStart, i);

                        frameStart = i + 1;
                        if (signals.ShouldStop)
                        {
                            updates.Clear();
                            return false;
                        }

                        break;
                }
            }

            updates.Clear();
            return true;
        }

        /// <summary>
        /// Releases the nodes the frame at <c>entries[start..end)</c> created, once its recursion is over.
        /// </summary>
        void ReleaseFrame(FrameUpdate[] entries, int start, int end)
        {
            for (var j = start; j < end; j++)
            {
                ref var e = ref entries[j];
                if (e.Kind == FrameUpdateKind.RefineAtomOwned)
                {
                    e.Node!.Release(pool);
                    e.Node = null;
                }
            }
        }

        /// <summary>
        /// Refines <paramref name="atom"/> to the dense rows <paramref name="range"/> of layer <paramref name="layer"/>:
        /// egglog's <c>insert_subset</c> (core-relations/src/free_join/execute.rs:775-785), which reuses the node in the
        /// slot when nothing else holds it. Here the position's scratch node for the atom is reused, which nothing outside
        /// the position's frames can hold, unless the run may share it, when a fresh node is taken.
        /// </summary>
        void InsertDense(int cur, int atom, int layer, OffsetRange range, bool exclusive)
        {
            if (exclusive == false)
            {
                nodes[atom] = pool.Rent(layer, Subset.Dense(range));
                return;
            }

            var row = scratch[cur];
            if (row is null || row.Length <= atom)
                scratch[cur] = row = new TrieNode?[Math.Max(nodes.Length, atom + 1)];

            var node = row[atom];
            if (node is null)
            {
                row[atom] = node = pool.Rent(layer, Subset.Dense(range));
            }
            else
            {
                node.ResetDense(layer, range, pool);
            }

            nodes[atom] = node;
        }

        /// <summary>
        /// Sends the current bindings to the sink, crossed with every row of the binding sets from
        /// <paramref name="index"/> on. egglog's <c>expand_binding_sets</c> (core-relations/src/free_join/execute.rs:2051-2093).
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="index">The first binding set to expand.</param>
        /// <param name="sink">The sink.</param>
        void ExpandBindingSets<TSink>(int index, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            if (signals.ShouldStop)
                return;

            if (index >= bindingSetCount)
            {
                sink.PushBindings(this, target);
                return;
            }

            var set = bindingSets[index];
            var variables = set.Variables;
            var rows = set.Rows;
            var data = rows.Data;
            var stride = rows.Stride;
            var last = index + 1 == bindingSetCount;
            for (var r = 0; r < rows.Count; r++)
            {
                if (last && signals.ShouldStop)
                    return;

                var offset = r * stride;
                for (var k = 0; k < variables.Length; k++)
                    values[variables[k].Index] = data[offset + k];

                if (last)
                    sink.PushBindings(this, target);
                else
                    ExpandBindingSets(index + 1, ref sink);
            }
        }

        /// <summary>
        /// Pushes a leaf scan's rows onto the binding-set stack.
        /// </summary>
        void PushBindingSet(Variable[] variables, TaggedRowBuffer rows)
        {
            if (bindingSetCount == bindingSets.Length)
                Array.Resize(ref bindingSets, bindingSets.Length * 2);

            bindingSets[bindingSetCount++] = new BindingSet(variables, rows);
        }

        /// <summary>
        /// Pops the top of the binding-set stack.
        /// </summary>
        void PopBindingSet()
        {
            Debug.Assert(bindingSetCount > 0);
            bindingSets[--bindingSetCount] = default;
        }

        /// <summary>
        /// Gets position <paramref name="cur"/>'s frame buffer, emptied.
        /// </summary>
        FrameUpdates TakeFrames(int cur)
        {
            var updates = frames[cur];
            if (updates is null)
                frames[cur] = updates = new FrameUpdates();
            else
                updates.Clear();

            return updates;
        }

        /// <summary>
        /// Gets position <paramref name="cur"/>'s probers, at least <paramref name="count"/> of them.
        /// </summary>
        Prober[] TakeProbers(int cur, int count)
        {
            var result = probers[cur];
            if (result is null || result.Length < count)
                probers[cur] = result = new Prober[Math.Max(count, prepared!.MaxProbers)];

            return result;
        }

        /// <summary>
        /// Gets a buffer for a chunked scan at position <paramref name="cur"/>, emptied. Reused: frames copy what they
        /// need out of it.
        /// </summary>
        TaggedRowBuffer TakeScanBuffer(int cur, int arity)
        {
            var buffer = scanBuffers[cur];
            if (buffer is null || buffer.Arity != arity)
                scanBuffers[cur] = buffer = new TaggedRowBuffer(arity);
            else
                buffer.Clear();

            return buffer;
        }

        /// <summary>
        /// Gets a buffer for a leaf scan at position <paramref name="cur"/>, emptied. Reused per position, except at
        /// position 0 of a parallel run with more than one stage, whose leaf rows the tasks forked at position 1 keep
        /// reading. (A leaf scan's own drain is a single frame, which is never handed off.)
        /// </summary>
        TaggedRowBuffer TakeLeafBuffer(int cur, int arity)
        {
            if (parallel && cur == 0 && order.Count > 1)
                return TaggedRowBuffer.CreateInline(arity);

            var buffer = leafBuffers[cur];
            if (buffer is null || buffer.Arity != arity)
                leafBuffers[cur] = buffer = TaggedRowBuffer.CreateInline(arity);
            else
                buffer.Clear();

            return buffer;
        }

        /// <summary>
        /// Adds <paramref name="variables"/> to the variables bound in the current rule, keeping the list ascending.
        /// </summary>
        void AddBound(Variable[] variables)
        {
            var added = 0;
            foreach (var v in variables)
                if (boundMask[v.Index] == false)
                    added++;

            if (added == 0)
                return;

            foreach (var v in variables)
                boundMask[v.Index] = true;

            var merged = new Variable[boundVariables.Length + added];
            var k = 0;
            for (var i = 0; i < boundMask.Length; i++)
                if (boundMask[i])
                    merged[k++] = new Variable(i);

            Debug.Assert(k == merged.Length);
            boundVariables = merged;
        }

        /// <summary>
        /// Makes the key and row scratch large enough for the block's stages.
        /// </summary>
        void EnsureKeyScratch(PreparedBlock block)
        {
            if (keyScratch.Length < block.MaxKey)
                keyScratch = new uint[block.MaxKey];

            if (rowScratch.Length < block.MaxBind)
                rowScratch = new uint[block.MaxBind];
        }

    }

}
