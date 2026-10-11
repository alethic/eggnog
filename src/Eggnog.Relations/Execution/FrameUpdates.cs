using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A flat buffer of the frames one join stage produced, each a candidate extension of the current bindings to
    /// recurse on: egglog's <c>FrameUpdates</c> (core-relations/src/free_join/frame_update.rs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stage builds a frame by pushing bindings and atom refinements and then <see cref="FinishFrame"/>, or abandons
    /// it with <see cref="Rollback"/>, which discards only the unfinished frame (repeated calls are harmless). Once
    /// <see cref="Frames"/> reaches the morsel size the stage drains the buffer: the frames are replayed in order and
    /// each <see cref="FrameUpdateKind.EndFrame"/> recurses into the next stage. The batching is egglog's "vectorized
    /// backtracking": better locality than recursing per candidate, and a whole buffer can be handed to another thread
    /// as one morsel.
    /// </para>
    /// <para>
    /// The executor keeps one buffer per join position and reuses it (a depth-first drain only ever has one buffer per
    /// position in use); a parallel hand-off moves the buffer to the task and gives the position a fresh one.
    /// </para>
    /// <para>
    /// Not thread-safe. Appending is amortized O(1) and allocation-free once the array has grown.
    /// </para>
    /// </remarks>
    internal sealed class FrameUpdates
    {

        FrameUpdate[] entries = new FrameUpdate[32];
        int count;
        int frames;
        int lastStart;

        /// <summary>
        /// Gets the number of finished frames. egglog's <c>frames</c>.
        /// </summary>
        public int Frames => frames;

        /// <summary>
        /// Gets the number of instructions, finished frames and the unfinished one.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets the instructions; the first <see cref="Count"/> are valid.
        /// </summary>
        public FrameUpdate[] Entries => entries;

        /// <summary>
        /// Binds <paramref name="variable"/> to <paramref name="value"/> in the current frame.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PushBinding(Variable variable, uint value)
        {
            ref var e = ref Append();
            e.Kind = FrameUpdateKind.PushBinding;
            e.Id = variable.Index;
            e.A = (int)value;
        }

        /// <summary>
        /// Refines <paramref name="atom"/> to <paramref name="node"/>, a node its parent's cache owns.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <param name="node">The node.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RefineAtom(AtomId atom, TrieNode node)
        {
            ref var e = ref Append();
            e.Kind = FrameUpdateKind.RefineAtomShared;
            e.Id = atom.Index;
            e.Node = node;
        }

        /// <summary>
        /// Refines <paramref name="atom"/> to the dense rows <paramref name="range"/>. egglog's
        /// <c>refine_atom_dense</c>, which avoids creating a node.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <param name="range">The rows.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RefineAtomDense(AtomId atom, OffsetRange range)
        {
            ref var e = ref Append();
            e.Kind = FrameUpdateKind.RefineAtomDense;
            e.Id = atom.Index;
            e.A = range.Start;
            e.B = range.End;
        }

        /// <summary>
        /// Refines <paramref name="atom"/> to <paramref name="rows"/>: a dense refinement when the rows are a range,
        /// otherwise a node from <paramref name="pool"/> that the frame owns. egglog's <c>refine_atom_subset</c>
        /// (core-relations/src/free_join/execute.rs:752-761).
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <param name="rows">The refined rows, nonempty; owned by the frame from now on.</param>
        /// <param name="pool">The pool to take the node from.</param>
        public void RefineAtomSubset(AtomId atom, Subset rows, TrieNodePool pool)
        {
            if (rows.TryGetDense(out var range))
            {
                RefineAtomDense(atom, range);
                return;
            }

            ref var e = ref Append();
            e.Kind = FrameUpdateKind.RefineAtomOwned;
            e.Id = atom.Index;
            e.Node = pool.Rent(rows);
        }

        /// <summary>
        /// Discards the unfinished frame, releasing the nodes it created. egglog's <c>rollback</c>: repeated calls still
        /// discard only that one frame.
        /// </summary>
        /// <param name="pool">Receives the released nodes.</param>
        public void Rollback(TrieNodePool pool)
        {
            for (var i = lastStart; i < count; i++)
            {
                ref var e = ref entries[i];
                if (e.Kind == FrameUpdateKind.RefineAtomOwned)
                    e.Node!.Release(pool);

                e.Node = null;
            }

            count = lastStart;
        }

        /// <summary>
        /// Finishes the current frame. egglog's <c>finish_frame</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FinishFrame()
        {
            ref var e = ref Append();
            e.Kind = FrameUpdateKind.EndFrame;
            lastStart = count;
            frames++;
        }

        /// <summary>
        /// Empties the buffer, dropping its node references without releasing them: after a replay (which has
        /// released or handed off every node), or to abandon frames after an early stop.
        /// </summary>
        public void Clear()
        {
            Array.Clear(entries, 0, count);
            count = 0;
            frames = 0;
            lastStart = 0;
        }

        /// <summary>
        /// Appends an instruction and returns it for the caller to fill.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref FrameUpdate Append()
        {
            if (count == entries.Length)
                Grow();

            return ref entries[count++];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow() => Array.Resize(ref entries, entries.Length * 2);

    }

}
