using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The oracle of the context differentials: a context that records what was asserted in it, in order
    /// (<see cref="ReferenceContextOp"/>, deletes and clears included), and computes its view from scratch on demand by
    /// replaying its ancestors' assertions and then its own into one <see cref="ReferenceContextView"/>.
    /// </summary>
    /// <remarks>
    /// Replaying ancestors first and then the context's own assertions is the order they took effect in: an ancestor
    /// changes after a child exists only by monotone assertions (an ancestor with live descendants retracts nothing),
    /// and a child retracts only under a frozen parent, after which no ancestor changes. Nothing here is incremental,
    /// shared or clever, which is the point.
    /// </remarks>
    sealed class ReferenceContext
    {

        readonly List<ReferenceContextOp> ops = new();

        /// <summary>
        /// Initializes a context over <paramref name="tables"/>.
        /// </summary>
        /// <param name="tables">The tables.</param>
        /// <param name="parent">The parent, or <c>null</c> for the root.</param>
        /// <param name="displacedInParentAtCreation">The ids displaced in the parent's view when the context was
        /// created.</param>
        public ReferenceContext(IReadOnlyList<LogicalTable> tables, ReferenceContext? parent, HashSet<ulong> displacedInParentAtCreation)
        {
            Tables = tables;
            Parent = parent;
            DisplacedInParentAtCreation = displacedInParentAtCreation;
        }

        /// <summary>
        /// Gets the tables.
        /// </summary>
        public IReadOnlyList<LogicalTable> Tables { get; }

        /// <summary>
        /// Gets the parent, or <c>null</c>.
        /// </summary>
        public ReferenceContext? Parent { get; }

        /// <summary>
        /// Gets the ids that were not canonical in the parent's view when the context was created: what its journal
        /// leaves out.
        /// </summary>
        public HashSet<ulong> DisplacedInParentAtCreation { get; }

        /// <summary>
        /// Gets the assertions made in this context, in order.
        /// </summary>
        public IReadOnlyList<ReferenceContextOp> Ops => ops;

        /// <summary>
        /// Creates a child, recording which of <paramref name="ids"/> are displaced in this context's view now.
        /// </summary>
        /// <param name="ids">The ids to consider: every id visible here.</param>
        public ReferenceContext CreateChild(IEnumerable<ulong> ids)
        {
            var view = View();
            var displaced = new HashSet<ulong>();
            foreach (var id in ids)
                if (view.Find(id) != id)
                    displaced.Add(id);

            return new ReferenceContext(Tables, this, displaced);
        }

        /// <summary>
        /// Records a union.
        /// </summary>
        public void Union(ulong a, ulong b) => ops.Add(new ReferenceContextOp(ReferenceContextOpKind.Union, null, null, a, b));

        /// <summary>
        /// Records a logical row.
        /// </summary>
        public void Insert(LogicalTable table, ulong[] row) => ops.Add(new ReferenceContextOp(ReferenceContextOpKind.Insert, table, (ulong[])row.Clone(), 0, 0));

        /// <summary>
        /// Records the retraction of the row with key <paramref name="key"/>.
        /// </summary>
        public void Delete(LogicalTable table, ulong[] key) => ops.Add(new ReferenceContextOp(ReferenceContextOpKind.Delete, table, (ulong[])key.Clone(), 0, 0));

        /// <summary>
        /// Records the clearing of <paramref name="table"/>.
        /// </summary>
        public void Clear(LogicalTable table) => ops.Add(new ReferenceContextOp(ReferenceContextOpKind.Clear, table, null, 0, 0));

        /// <summary>
        /// Records a row a rule run derived (<see cref="ReferenceRuleContext"/>).
        /// </summary>
        public void Derive(LogicalTable table, ulong[] row) => ops.Add(new ReferenceContextOp(ReferenceContextOpKind.Derived, table, (ulong[])row.Clone(), 0, 0));

        /// <summary>
        /// Records a union a rule run derived (<see cref="ReferenceRuleContext"/>).
        /// </summary>
        public void DeriveUnion(ulong a, ulong b) => ops.Add(new ReferenceContextOp(ReferenceContextOpKind.Derived, null, null, a, b));

        /// <summary>
        /// Gets the assertions of the chain from the root to this context, in replay order.
        /// </summary>
        public List<ReferenceContextOp> ChainOps()
        {
            var chain = new List<ReferenceContext>();
            for (var context = this; context is not null; context = context.Parent)
                chain.Insert(0, context);

            var all = new List<ReferenceContextOp>();
            foreach (var context in chain)
                all.AddRange(context.ops);

            return all;
        }

        /// <summary>
        /// Computes the context's closed view.
        /// </summary>
        public ReferenceContextView View()
        {
            var view = new ReferenceContextView(Tables);
            foreach (var op in ChainOps())
            {
                switch (op.Kind)
                {
                    case ReferenceContextOpKind.Union:
                    case ReferenceContextOpKind.Derived when op.Table is null:
                        view.Union(op.A, op.B);
                        break;
                    case ReferenceContextOpKind.Insert:
                    case ReferenceContextOpKind.Derived:
                        view.Add(op.Table!, op.Row!);
                        break;
                    case ReferenceContextOpKind.Delete:
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
