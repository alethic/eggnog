using System;
using System.Collections.Generic;
using System.Linq;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A store built from a <see cref="ContainerPassProgram"/> for <see cref="ContainerPassDifferentialTests"/>: the
    /// classes first (ids 0 up), then vectors of classes and of earlier vectors, sets of classes and of vectors, rows
    /// of the max function <c>f</c> over containers and classes and of the constructor <c>c</c> over classes, and a
    /// root rebuild. Two worlds built from one program hold the same ids. The world records what it interned and the
    /// rows of <c>c</c> it wrote, which the naive model starts from (<see cref="ContainerPassModel"/>).
    /// </summary>
    sealed class ContainerPassWorld
    {

        /// <summary>
        /// Builds the world of <paramref name="program"/>.
        /// </summary>
        /// <param name="program">The program.</param>
        public ContainerPassWorld(ContainerPassProgram program)
        {
            Store = new Store(ParallelHeuristics.Serial);
            F = Store.AddTable(MaxFunction(1), name: "f");
            C = Store.AddTable(Constructor(Store), name: "c");
            var values = Store.Root.Database.Containers;
            values.RegisterType<VecContainer>(Store.IdCounter, new UnionContainerMerge(Store.UnionFindTable, Store.ClockCounter));
            values.RegisterType<SetContainer>(Store.IdCounter, new UnionContainerMerge(Store.UnionFindTable, Store.ClockCounter));
            Vectors = values.GetEnvironment<VecContainer>();
            Sets = values.GetEnvironment<SetContainer>();

            var root = Store.Root;
            var random = new Random(program.Seed ^ 0x5eed);
            Classes = program.Classes;
            var classes = new List<uint>();
            for (var i = 0; i < program.Classes; i++)
            {
                var id = root.NewId();
                if (id != (uint)i)
                    throw new InvalidOperationException("the classes are the root's first ids");

                classes.Add(id);
            }

            // A large program's vectors nearly all hold the last class, the hub its child displaces first; a small
            // program's containers hold one or two elements.
            var hub = classes[^1];
            var longest = program.Small ? 2 : 3;
            for (var i = 0; i < program.VectorCount; i++)
            {
                var length = random.Next(1, longest + 1);
                var elements = new uint[length];
                for (var j = 0; j < length; j++)
                    elements[j] = VectorIds.Count > 0 && random.Next(program.Large ? 20 : 4) == 0 ? VectorIds[random.Next(VectorIds.Count)] : classes[random.Next(classes.Count)];

                if (program.Large && random.Next(10) != 0)
                    elements[0] = hub;

                VectorIds.Add(Intern(new VecContainer(elements)));
                VectorContents.Add(elements);
            }

            for (var i = 0; i < program.SetCount; i++)
            {
                var length = random.Next(1, longest + 1);
                var elements = new uint[length];
                for (var j = 0; j < length; j++)
                    elements[j] = VectorIds.Count > 0 && random.Next(3) == 0 ? VectorIds[random.Next(VectorIds.Count)] : classes[random.Next(classes.Count)];

                SetIds.Add(Intern(new SetContainer(elements)));
                SetContents.Add(elements);
            }

            foreach (var id in VectorIds.Concat(SetIds).Distinct())
                if (random.Next(2) == 0)
                    Insert(root, F, id, (uint)random.Next(10), 0);

            foreach (var id in classes)
            {
                if (random.Next(2) == 0)
                    Insert(root, F, id, (uint)random.Next(10), 0);

                if (random.Next(3) == 0)
                {
                    var value = classes[random.Next(classes.Count)];
                    Insert(root, C, id, value, 0);
                    ConstructorRows.Add((id, value));
                }
            }

            root.Rebuild();
            MaxId = (uint)(Store.Counters.Read(Store.IdCounter) - 1);
        }

        /// <summary>
        /// Gets the store.
        /// </summary>
        public Store Store { get; }

        /// <summary>
        /// Gets <c>f(id) → number</c>, merged by the larger value.
        /// </summary>
        public TableId F { get; }

        /// <summary>
        /// Gets the constructor <c>c(id) → id</c>, whose outputs are unioned when its keys become one.
        /// </summary>
        public TableId C { get; }

        /// <summary>
        /// Gets the root's environment of vectors.
        /// </summary>
        public ContainerEnv<VecContainer> Vectors { get; }

        /// <summary>
        /// Gets the root's environment of sets.
        /// </summary>
        public ContainerEnv<SetContainer> Sets { get; }

        /// <summary>
        /// Gets the ids the vectors were interned under, in order, with repeats.
        /// </summary>
        public List<uint> VectorIds { get; } = new();

        /// <summary>
        /// Gets the ids the sets were interned under, in order, with repeats.
        /// </summary>
        public List<uint> SetIds { get; } = new();

        /// <summary>
        /// Gets the elements of each vector interned, in the order of <see cref="VectorIds"/>.
        /// </summary>
        public List<uint[]> VectorContents { get; } = new();

        /// <summary>
        /// Gets the elements of each set interned, in the order of <see cref="SetIds"/>.
        /// </summary>
        public List<uint[]> SetContents { get; } = new();

        /// <summary>
        /// Gets the rows of the constructor <c>c</c> the world wrote, as (key, value).
        /// </summary>
        public List<(uint Key, uint Value)> ConstructorRows { get; } = new();

        /// <summary>
        /// Gets the number of classes, the world's first ids.
        /// </summary>
        public int Classes { get; }

        /// <summary>
        /// Gets the largest id the world minted.
        /// </summary>
        public uint MaxId { get; }

        /// <summary>
        /// Gets the id a program's write names by <paramref name="index"/>: a class below the number of classes, then a
        /// vector by its place in <see cref="VectorIds"/>, then a set by its place in <see cref="SetIds"/>.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <returns>The id, which need not be canonical.</returns>
        public uint Resolve(uint index)
        {
            if (index < Classes)
                return index;

            var i = (int)index - Classes;
            return i < VectorIds.Count ? VectorIds[i] : SetIds[i - VectorIds.Count];
        }

        /// <summary>
        /// Runs <paramref name="batches"/> at the root, each followed by a rebuild.
        /// </summary>
        /// <param name="batches">The batches of steps.</param>
        public void Replay(List<List<(char Kind, uint A, uint B)>> batches)
        {
            foreach (var batch in batches)
                ContainerPassDifferentialTests.Apply(Store.Root, this, batch);
        }

        /// <summary>
        /// Interns <paramref name="container"/> at the root: the world's own containers, and those a program of
        /// <see cref="AncestorContainerDifferentialTests"/> interns later.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <returns>Its id.</returns>
        internal uint Intern<TContainer>(TContainer container)
            where TContainer : struct, IContainerValue<TContainer>
        {
            return Store.Root.Database.WithExecutionState(state => state.Containers.GetOrInsert(container, state));
        }

    }

}
