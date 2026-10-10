using System.Collections.Generic;

namespace Eggnog.Storage.Tests.Reference
{

    /// <summary>
    /// The oracle: a context that records what was asserted in it and computes its view from scratch on demand,
    /// by replaying its ancestors' assertions and its own into one naive e-graph and closing it.
    /// </summary>
    /// <remarks>
    /// Nothing here is incremental, shared or clever, which is the point. It defines what a context should see —
    /// every assertion of every ancestor and of itself, closed under congruence and merges — and the store is
    /// tested by agreeing with it.
    /// </remarks>
    public sealed class ReferenceContext
    {

        readonly List<(ulong A, ulong B)> unions = new();
        readonly List<(Table Table, ulong[] Row)> inserts = new();

        public ReferenceContext(IReadOnlyList<Table> tables, ReferenceContext? parent)
        {
            Tables = tables;
            Parent = parent;
        }

        public IReadOnlyList<Table> Tables { get; }

        public ReferenceContext? Parent { get; }

        public ReferenceContext CreateChild() => new(Tables, this);

        public void Union(ulong a, ulong b) => unions.Add((a, b));

        public void Insert(Table table, ulong[] row) => inserts.Add((table, (ulong[])row.Clone()));

        public ReferenceView View()
        {
            var chain = new List<ReferenceContext>();
            for (var context = this; context is not null; context = context.Parent)
                chain.Insert(0, context);

            var view = new ReferenceView(Tables);
            foreach (var context in chain)
            {
                foreach (var (a, b) in context.unions)
                    view.Union(a, b);

                foreach (var (table, row) in context.inserts)
                    view.Add(table, row);
            }

            view.Close();
            return view;
        }

    }

}
