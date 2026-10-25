using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A root for the per-child container benchmarks: a <see cref="Store"/> whose root holds <see cref="Containers"/>
    /// classes <c>x0 &lt; x1 &lt; …</c> and as many vectors of classes, <c>v(i) = [x(i), x(i+1)]</c> (the last wrapping
    /// to <c>x0</c>), in the vector type registered as holding e-class ids under the bridge's merge, and frozen.
    /// </summary>
    /// <remarks>
    /// Every class is held by exactly two vectors, so a child whose union displaces a fixed set of classes claims a
    /// fixed number of vectors whatever <see cref="Containers"/> is: what "flat" means for the per-child cost of
    /// containers. The vectors are interned in one pass through the root's environment, and the root is frozen, so
    /// every child reads it without locks and leaves it as it was.
    /// </remarks>
    internal sealed class ContainerRootWorkload
    {

        /// <summary>
        /// Builds and freezes the root.
        /// </summary>
        /// <param name="containers">The number of classes, and of vectors.</param>
        public ContainerRootWorkload(int containers)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(containers, 1000);
            Containers = containers;
            Store = new Store(ParallelHeuristics.Serial);
            var values = Store.Root.Database.Containers;
            values.RegisterType<VecContainer>(Store.IdCounter, new UnionContainerMerge(Store.UnionFindTable, Store.ClockCounter), holdsIds: true);
            Env = values.GetEnvironment<VecContainer>();
            var root = Store.Root;
            Classes = new uint[containers];
            for (var i = 0; i < containers; i++)
                Classes[i] = root.NewId();

            Vectors = new uint[containers];
            Span<uint> elements = stackalloc uint[2];
            for (var i = 0; i < containers; i++)
            {
                elements[0] = Classes[i];
                elements[1] = Classes[(i + 1) % containers];
                Vectors[i] = Env.GetOrInsertAtRoot(new VecContainer(true, elements), Store.Counters);
            }

            root.Freeze();
        }

        /// <summary>
        /// Gets the number of classes, and of vectors.
        /// </summary>
        public int Containers { get; }

        /// <summary>
        /// Gets the store.
        /// </summary>
        public Store Store { get; }

        /// <summary>
        /// Gets the root's environment of vectors.
        /// </summary>
        public ContainerEnv<VecContainer> Env { get; }

        /// <summary>
        /// Gets the classes, ascending.
        /// </summary>
        public uint[] Classes { get; }

        /// <summary>
        /// Gets the vectors' ids: <c>Vectors[i]</c> is <c>[x(i), x(i+1)]</c>.
        /// </summary>
        public uint[] Vectors { get; }

    }

}
