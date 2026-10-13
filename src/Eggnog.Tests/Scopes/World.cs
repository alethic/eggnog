using System;
using System.Threading;

using Eggnog.Tests.Support;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A graph whose lattice function <c>f</c> refuses, once armed, the merges <c>refuses</c> selects, and counts the
    /// merges it refused and those of them that ran inside the engine's lifter.
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
        /// <param name="refuses">Which merges (old value, new value) throw once armed; <c>null</c> for every
        /// one.</param>
        public World(Func<long, long, bool>? refuses = null)
        {
            Graph = new EGraph();
            Graph.DefineSort<Node>("Node");
            A = Graph.DefineConstructor<Node>("A");
            Tag = Graph.DefineRelation<Node>("tag");
            F = Graph.DefineFunction<string, long>("f", new ValueLattice<long>("throwing max", (x, y) =>
            {
                if (Volatile.Read(ref armed) != 0 && (refuses?.Invoke(x, y) ?? true))
                {
                    Interlocked.Increment(ref Refused);
                    if (Environment.StackTrace.Contains("Lifter"))
                        Interlocked.Increment(ref RefusedInLifter);

                    throw new InvalidOperationException("merge refused");
                }

                return Math.Max(x, y);
            }, Math.Min));
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
        /// <see cref="InvalidOperationException"/> ("merge refused") for the merges it refuses once armed.
        /// </summary>
        public Function<string, long> F { get; }

        /// <summary>
        /// The number of merges the lattice refused.
        /// </summary>
        public int Refused;

        /// <summary>
        /// The number of refused merges that ran inside the engine's lifter.
        /// </summary>
        public int RefusedInLifter;

        /// <summary>
        /// Makes the lattice refuse merges from now on.
        /// </summary>
        public void Arm() => Volatile.Write(ref armed, 1);

    }

}
