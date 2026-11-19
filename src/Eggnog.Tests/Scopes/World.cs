using System;
using System.Threading;

using Eggnog.Tests.Support;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A graph whose lattice function <c>f</c> refuses every merge once armed.
    /// </summary>
    public sealed class World
    {

        /// <summary>
        /// Whether the lattice refuses merges: 0 until <see cref="Arm"/> is called.
        /// </summary>
        int armed;

        /// <summary>
        /// Defines the graph: the sort <c>Node</c>, the constructor <c>A</c>, the relation <c>tag</c> and the function
        /// <c>f</c>.
        /// </summary>
        public World()
        {
            Graph = new EGraph();
            Graph.DefineSort<Node>("Node");
            A = Graph.DefineConstructor<Node>("A");
            Tag = Graph.DefineRelation<Node>("tag");
            F = Graph.DefineFunction<string, long>("f", new ValueLattice<long>("throwing max", (x, y) =>
                Volatile.Read(ref armed) != 0 ? throw new InvalidOperationException("merge refused") : Math.Max(x, y)));
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
        /// Gets the function <c>f</c>, whose lattice (max, named <c>throwing max</c>) throws
        /// <see cref="InvalidOperationException"/> ("merge refused") for every merge once armed.
        /// </summary>
        public Function<string, long> F { get; }

        /// <summary>
        /// Makes the lattice refuse merges from now on.
        /// </summary>
        public void Arm() => Volatile.Write(ref armed, 1);

    }

}
