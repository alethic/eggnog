using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// One order in which an <see cref="Extractor"/> meets rows as egglog's extractor does, and what was decided in it for
    /// the classes asked about so far: the time of each class's last update, of its first update where its cost
    /// saturates, and the node it extracts as (Extractor.Choices.cs). The root-sort extractor has one, over its functions
    /// in its own order; an on-demand extractor has one per sort queried, over the functions reachable from that sort.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A time is a (pass, node) pair: the relaxation of the node's row in that pass of egglog's <c>bellman_ford</c>
    /// (src/extract.rs:407-460). Pass <c>-1</c> stands for no event, before every time.
    /// </para>
    /// <para>
    /// The order holds only the classes it has met: each gets a local index on first use (<see cref="GetOrAdd"/>), and
    /// the per-class arrays are indexed by it, so an order that answers a few classes costs a few entries however large
    /// the extractor's graph is. A class's entries mean something only once its state is <see cref="Solved"/>.
    /// </para>
    /// </remarks>
    internal sealed class ExtractionOrder
    {

        /// <summary>
        /// The state of a class whose times are not computed in this order.
        /// </summary>
        public const int Unsolved = 0;

        /// <summary>
        /// The state of a class whose times are being computed.
        /// </summary>
        public const int Collecting = 1;

        /// <summary>
        /// The state of a class whose times are computed.
        /// </summary>
        public const int Solved = 2;

        /// <summary>
        /// Each extractor slot's place in egglog's list of functions (<c>funcs</c>, src/extract.rs:246-286); <c>-1</c>
        /// for a slot that is not in it.
        /// </summary>
        public readonly int[] SlotRank;

        readonly Dictionary<int, int> locals = new();
        int count;

        /// <summary>
        /// The extractor's class at each local index.
        /// </summary>
        public int[] Owner;

        /// <summary>
        /// Each class's state for its last update and its choice.
        /// </summary>
        public int[] State;

        /// <summary>
        /// The pass of each class's last update, the update that set the cost it ends with: egglog's topological rank.
        /// </summary>
        public int[] FinalPass;

        /// <summary>
        /// The node whose relaxation made each class's last update.
        /// </summary>
        public int[] FinalNode;

        /// <summary>
        /// The node each class extracts as: egglog's parent edge (src/extract.rs:461-499), or for a saturated class
        /// egglog's rule finds none for, the fallback of <see cref="Extractor"/>'s choices.
        /// </summary>
        public int[] Choice;

        /// <summary>
        /// Each class's state for its first update, which a saturated cost needs.
        /// </summary>
        public int[] FirstState;

        /// <summary>
        /// The pass of each class's first update.
        /// </summary>
        public int[] FirstPass;

        /// <summary>
        /// The node whose relaxation made each class's first update.
        /// </summary>
        public int[] FirstNode;

        /// <summary>
        /// Initializes a new instance with no class met.
        /// </summary>
        /// <param name="slotRank">Each slot's place in the order; <c>-1</c> for one outside it.</param>
        public ExtractionOrder(int[] slotRank)
        {
            const int Initial = 16;
            SlotRank = slotRank;
            Owner = new int[Initial];
            State = new int[Initial];
            FinalPass = new int[Initial];
            FinalNode = new int[Initial];
            Choice = new int[Initial];
            FirstState = new int[Initial];
            FirstPass = new int[Initial];
            FirstNode = new int[Initial];
        }

        /// <summary>
        /// Gets the local index of <paramref name="owner"/>.
        /// </summary>
        /// <param name="owner">One of the extractor's classes.</param>
        /// <returns>The local index; <c>-1</c> for a class the order has not met.</returns>
        public int LocalOf(int owner) => locals.TryGetValue(owner, out var local) ? local : -1;

        /// <summary>
        /// Gets the state of <paramref name="owner"/> for its last update and its choice.
        /// </summary>
        /// <param name="owner">One of the extractor's classes.</param>
        /// <returns>The state; <see cref="Unsolved"/> for a class the order has not met.</returns>
        public int StateOf(int owner) => locals.TryGetValue(owner, out var local) ? State[local] : Unsolved;

        /// <summary>
        /// Gets the local index of <paramref name="owner"/>, giving it the next one, unsolved, on first use. The arrays
        /// may be replaced, so a caller holding one reads it again after this.
        /// </summary>
        /// <param name="owner">One of the extractor's classes.</param>
        /// <returns>The local index.</returns>
        public int GetOrAdd(int owner)
        {
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(locals, owner, out var exists);
            if (exists)
                return slot;

            if (count == Owner.Length)
            {
                var size = count * 2;
                Array.Resize(ref Owner, size);
                Array.Resize(ref State, size);
                Array.Resize(ref FinalPass, size);
                Array.Resize(ref FinalNode, size);
                Array.Resize(ref Choice, size);
                Array.Resize(ref FirstState, size);
                Array.Resize(ref FirstPass, size);
                Array.Resize(ref FirstNode, size);
            }

            slot = count;
            Owner[count] = owner;
            State[count] = Unsolved;
            FirstState[count] = Unsolved;
            return count++;
        }

    }

}
