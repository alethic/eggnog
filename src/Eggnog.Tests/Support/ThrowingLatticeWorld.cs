using System;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A graph with a frozen root and a writable child of it (<see cref="Parent"/>), a constructor, a relation, and a
    /// function whose lattice (max, named <c>throwing max</c>) throws <see cref="InvalidOperationException"/>
    /// ("merge refused") once <see cref="Arm"/> is called: for the catch-up failures of N113.
    /// </summary>
    public sealed class ThrowingLatticeWorld
    {

        bool fail;

        /// <summary>
        /// Defines the graph, freezes its root and creates <see cref="Parent"/>.
        /// </summary>
        /// <param name="refuses">Which merges (old value, new value) throw once armed; <c>null</c> for every one.</param>
        public ThrowingLatticeWorld(Func<long, long, bool>? refuses = null)
        {
            Graph = new EGraph();
            Graph.DefineSort<Node>("Node");
            A = Graph.DefineConstructor<Node>("A");
            Tag = Graph.DefineRelation<Node>("tag");
            var max = new ValueLattice<long>("throwing max", (x, y) => fail && (refuses?.Invoke(x, y) ?? true) ? throw new InvalidOperationException("merge refused") : Math.Max(x, y), Math.Min);
            F = Graph.DefineFunction<string, long>("f", max);
            Graph.Root.Freeze();
            Parent = Graph.Root.CreateChild();
        }

        /// <summary>
        /// Gets the graph.
        /// </summary>
        public EGraph Graph { get; }

        /// <summary>
        /// Gets the nullary constructor <c>A</c>.
        /// </summary>
        public Constructor<Node> A { get; }

        /// <summary>
        /// Gets the relation <c>tag</c>.
        /// </summary>
        public Relation<Node> Tag { get; }

        /// <summary>
        /// Gets the function <c>f</c>, whose lattice throws once armed.
        /// </summary>
        public Function<string, long> F { get; }

        /// <summary>
        /// Gets the writable child of the frozen root.
        /// </summary>
        public Scope Parent { get; }

        /// <summary>
        /// Makes the lattice throw from now on.
        /// </summary>
        public void Arm() => fail = true;

        /// <summary>
        /// Makes the lattice merge again.
        /// </summary>
        public void Disarm() => fail = false;

    }

}
