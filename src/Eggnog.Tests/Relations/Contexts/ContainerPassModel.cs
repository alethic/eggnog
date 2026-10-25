using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The naive model of <see cref="ContainerPassDifferentialTests"/>, an oracle that shares nothing with the engine's
    /// rebuild loops: a partition of every id a <see cref="ContainerPassWorld"/> minted
    /// (<see cref="PartitionReference"/>, whose canonical id is the class minimum, as the engine's union-find keeps the
    /// smaller id), closed under congruence of the world's containers and of its constructor <c>c</c>, as
    /// <see cref="ContainerModelHarness"/> closes its own: vectors with equal canonical elements, and sets with equal
    /// sets of canonical elements, have equal ids, and rows of <c>c</c> with equal canonical keys have equal values.
    /// The max function <c>f</c> merges no ids, so the model leaves it out.
    /// </summary>
    sealed class ContainerPassModel
    {

        readonly PartitionReference partition;
        readonly List<(char Kind, uint[] Contents, uint Id)> containers = new();
        readonly List<(uint Key, uint Value)> constructorRows = new();

        /// <summary>
        /// Initializes the model of <paramref name="world"/> as built: its containers, its rows of <c>c</c>, and no
        /// union yet, with room for <paramref name="extraIds"/> ids minted later (<see cref="AddContainer"/>).
        /// </summary>
        /// <param name="world">The world.</param>
        /// <param name="extraIds">How many ids past the world's largest the model may meet.</param>
        public ContainerPassModel(ContainerPassWorld world, int extraIds = 0)
        {
            partition = new PartitionReference((int)world.MaxId + 1 + extraIds);
            for (var i = 0; i < world.VectorIds.Count; i++)
                containers.Add(('v', world.VectorContents[i], world.VectorIds[i]));

            for (var i = 0; i < world.SetIds.Count; i++)
                containers.Add(('s', world.SetContents[i], world.SetIds[i]));

            constructorRows.AddRange(world.ConstructorRows);
        }

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/>: the minimum of its class.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The canonical id.</returns>
        public uint Canonical(uint id) => partition.Canonical(id);

        /// <summary>
        /// Unions the classes of <paramref name="a"/> and <paramref name="b"/>.
        /// </summary>
        public void Union(uint a, uint b) => partition.Union(a, b, out _, out _);

        /// <summary>
        /// Records the row <c>c(<paramref name="key"/>) = <paramref name="value"/></c>.
        /// </summary>
        public void WriteConstructor(uint key, uint value) => constructorRows.Add((key, value));

        /// <summary>
        /// Records a container interned after the world was built: of kind <paramref name="kind"/> (<c>v</c> a vector,
        /// <c>s</c> a set), holding <paramref name="contents"/>, under <paramref name="id"/>, which must lie within the
        /// room the model was made with.
        /// </summary>
        public void AddContainer(char kind, uint[] contents, uint id) => containers.Add((kind, contents, id));

        /// <summary>
        /// Closes the partition under congruence of the containers and of <c>c</c>, to a fixpoint.
        /// </summary>
        public void Close()
        {
            bool changed;
            do
            {
                changed = false;
                var byContent = new Dictionary<string, uint>();
                foreach (var (kind, contents, id) in containers)
                {
                    var key = kind + ":" + string.Join(",", CanonicalContents(kind, contents));
                    if (byContent.TryGetValue(key, out var other))
                        changed |= partition.Union(other, id, out _, out _);
                    else
                        byContent.Add(key, id);
                }

                var byKey = new Dictionary<uint, uint>();
                foreach (var (key, value) in constructorRows)
                {
                    if (byKey.TryGetValue(Canonical(key), out var other))
                        changed |= partition.Union(other, value, out _, out _);
                    else
                        byKey.Add(Canonical(key), value);
                }
            }
            while (changed);
        }

        /// <summary>
        /// Gets the containers of kind <paramref name="kind"/> (<c>v</c> vectors, <c>s</c> sets) the model holds once
        /// closed: one per canonical content, under the canonical id of its class.
        /// </summary>
        /// <param name="kind">The kind.</param>
        /// <returns>The contents and ids, by content.</returns>
        public Dictionary<string, (uint[] Contents, uint Id)> Containers(char kind)
        {
            var result = new Dictionary<string, (uint[], uint)>();
            foreach (var (k, contents, id) in containers)
            {
                if (k != kind)
                    continue;

                var canonical = CanonicalContents(kind, contents);
                result.TryAdd(string.Join(",", canonical), (canonical, Canonical(id)));
            }

            return result;
        }

        /// <summary>
        /// Gets the canonical contents of a container: a vector's elements mapped to their canonical ids, a set's
        /// distinct and sorted as well.
        /// </summary>
        uint[] CanonicalContents(char kind, uint[] contents)
        {
            var mapped = contents.Select(Canonical);
            return kind == 's' ? mapped.Distinct().Order().ToArray() : mapped.ToArray();
        }

    }

}
