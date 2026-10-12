using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The oracle of the context differentials (colored-contexts spec §17 M9.4, X32), ported from the spike's
    /// <c>Reference/ReferenceContext</c>: a context that records what was asserted in it, in order
    /// (<see cref="SpikeReferenceOp"/>, deletes and clears included), and computes its view from scratch on demand by
    /// replaying its ancestors' assertions and then its own into one <see cref="SpikeReferenceView"/>.
    /// </summary>
    /// <remarks>
    /// Replaying ancestors first and then the context's own assertions is the order they took effect in: an ancestor
    /// changes after a child exists only by monotone assertions (an ancestor with live descendants retracts nothing,
    /// decision 1), and a child retracts only under a frozen parent (R1), after which no ancestor changes. Nothing here is
    /// incremental, shared or clever, which is the point.
    /// </remarks>
    sealed class SpikeReferenceContext
    {

        readonly List<SpikeReferenceOp> ops = new();

        /// <summary>
        /// Initializes a context over <paramref name="tables"/>.
        /// </summary>
        /// <param name="tables">The tables.</param>
        /// <param name="parent">The parent, or <c>null</c> for the root.</param>
        /// <param name="displacedInParentAtCreation">The ids displaced in the parent's view when the context was created.</param>
        public SpikeReferenceContext(IReadOnlyList<SpikeTable> tables, SpikeReferenceContext? parent, HashSet<ulong> displacedInParentAtCreation)
        {
            Tables = tables;
            Parent = parent;
            DisplacedInParentAtCreation = displacedInParentAtCreation;
        }

        /// <summary>
        /// Gets the tables.
        /// </summary>
        public IReadOnlyList<SpikeTable> Tables { get; }

        /// <summary>
        /// Gets the parent, or <c>null</c>.
        /// </summary>
        public SpikeReferenceContext? Parent { get; }

        /// <summary>
        /// Gets the ids that were not canonical in the parent's view when the context was created: what its journal leaves
        /// out (I8, review finding R1-8).
        /// </summary>
        public HashSet<ulong> DisplacedInParentAtCreation { get; }

        /// <summary>
        /// Gets the assertions made in this context, in order.
        /// </summary>
        public IReadOnlyList<SpikeReferenceOp> Ops => ops;

        /// <summary>
        /// Creates a child, recording which of <paramref name="ids"/> are displaced in this context's view now.
        /// </summary>
        /// <param name="ids">The ids to consider: every id visible here.</param>
        public SpikeReferenceContext CreateChild(IEnumerable<ulong> ids)
        {
            var view = View();
            var displaced = new HashSet<ulong>();
            foreach (var id in ids)
                if (view.Find(id) != id)
                    displaced.Add(id);

            return new SpikeReferenceContext(Tables, this, displaced);
        }

        /// <summary>
        /// Records a union.
        /// </summary>
        public void Union(ulong a, ulong b) => ops.Add(new SpikeReferenceOp(SpikeReferenceOpKind.Union, null, null, a, b));

        /// <summary>
        /// Records a logical row.
        /// </summary>
        public void Insert(SpikeTable table, ulong[] row) => ops.Add(new SpikeReferenceOp(SpikeReferenceOpKind.Insert, table, (ulong[])row.Clone(), 0, 0));

        /// <summary>
        /// Records the retraction of the row with key <paramref name="key"/>.
        /// </summary>
        public void Delete(SpikeTable table, ulong[] key) => ops.Add(new SpikeReferenceOp(SpikeReferenceOpKind.Delete, table, (ulong[])key.Clone(), 0, 0));

        /// <summary>
        /// Records the clearing of <paramref name="table"/>.
        /// </summary>
        public void Clear(SpikeTable table) => ops.Add(new SpikeReferenceOp(SpikeReferenceOpKind.Clear, table, null, 0, 0));

        /// <summary>
        /// Gets the assertions of the chain from the root to this context, in replay order.
        /// </summary>
        public List<SpikeReferenceOp> ChainOps()
        {
            var chain = new List<SpikeReferenceContext>();
            for (var context = this; context is not null; context = context.Parent)
                chain.Insert(0, context);

            var all = new List<SpikeReferenceOp>();
            foreach (var context in chain)
                all.AddRange(context.ops);

            return all;
        }

        /// <summary>
        /// Computes the context's closed view.
        /// </summary>
        public SpikeReferenceView View()
        {
            var view = new SpikeReferenceView(Tables);
            foreach (var op in ChainOps())
            {
                switch (op.Kind)
                {
                    case SpikeReferenceOpKind.Union:
                        view.Union(op.A, op.B);
                        break;
                    case SpikeReferenceOpKind.Insert:
                        view.Add(op.Table!, op.Row!);
                        break;
                    case SpikeReferenceOpKind.Delete:
                        view.Delete(op.Table!, op.Row!);
                        break;
                    default:
                        view.Clear(op.Table!);
                        break;
                }
            }

            view.Close();
            return view;
        }

    }

}
