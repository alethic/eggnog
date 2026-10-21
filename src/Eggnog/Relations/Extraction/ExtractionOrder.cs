using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One order in which an <see cref="Extractor"/> meets rows as egglog's extractor does, and what was decided in it for
    /// the classes asked about so far: the time of each class's last update, of its first update where its cost
    /// saturates, and the node it extracts as (Extractor.Choices.cs). The root-sort extractor has one, over its functions
    /// in its own order; an on-demand extractor has one per sort queried, over the functions reachable from that sort.
    /// </summary>
    /// <remarks>
    /// A time is a (pass, node) pair: the relaxation of the node's row in that pass of egglog's <c>bellman_ford</c>
    /// (src/extract.rs:407-460). Pass <c>-1</c> stands for no event, before every time. The per-class arrays grow with the
    /// extractor's classes; a class's entries mean something only once its state is <see cref="Solved"/>.
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
        /// The state of a class whose times and choice are computed.
        /// </summary>
        public const int Solved = 2;

        /// <summary>
        /// Each extractor slot's place in egglog's list of functions (<c>funcs</c>, src/extract.rs:246-286); <c>-1</c>
        /// for a slot that is not in it.
        /// </summary>
        public readonly int[] SlotRank;

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
        /// The node each class extracts as, egglog's parent edge (src/extract.rs:461-499); <c>-1</c> when no row
        /// qualifies, which egglog's reconstruction meets with a panic.
        /// </summary>
        public int[] Choice;

        /// <summary>
        /// Each class's state for its first update; <c>null</c> until a saturated cost needs it.
        /// </summary>
        public int[]? FirstState;

        /// <summary>
        /// The pass of each class's first update.
        /// </summary>
        public int[]? FirstPass;

        /// <summary>
        /// The node whose relaxation made each class's first update.
        /// </summary>
        public int[]? FirstNode;

        /// <summary>
        /// Initializes a new instance with no class solved.
        /// </summary>
        /// <param name="slotRank">Each slot's place in the order; <c>-1</c> for one outside it.</param>
        public ExtractionOrder(int[] slotRank)
        {
            const int Initial = 16;
            SlotRank = slotRank;
            State = new int[Initial];
            FinalPass = new int[Initial];
            FinalNode = new int[Initial];
            Choice = new int[Initial];
        }

        /// <summary>
        /// Grows the arrays of last updates and choices to at least <paramref name="classes"/> classes; new classes are
        /// unsolved.
        /// </summary>
        /// <param name="classes">The extractor's number of classes.</param>
        public void Grow(int classes)
        {
            if (classes <= State.Length)
                return;

            var size = Math.Max(classes, State.Length * 2);
            Array.Resize(ref State, size);
            Array.Resize(ref FinalPass, size);
            Array.Resize(ref FinalNode, size);
            Array.Resize(ref Choice, size);
        }

        /// <summary>
        /// Allocates or grows the arrays of first updates to at least <paramref name="classes"/> classes; new classes are
        /// unsolved.
        /// </summary>
        /// <param name="classes">The extractor's number of classes.</param>
        public void GrowFirst(int classes)
        {
            if (FirstState is not null && classes <= FirstState.Length)
                return;

            var size = Math.Max(classes, FirstState is null ? 16 : FirstState.Length * 2);
            Array.Resize(ref FirstState, size);
            Array.Resize(ref FirstPass, size);
            Array.Resize(ref FirstNode, size);
        }

    }

}
