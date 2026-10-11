using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The differential model of the union-find table (<see cref="DisplacedTable"/>): a naive union-find whose classes
    /// are named by their smallest id, and the row each effective union adds, <c>[displaced root, canonical, ts]</c>.
    /// </summary>
    /// <remarks>
    /// A union of two classes displaces the larger root. Over any sequence of unions the displaced ids are exactly the
    /// ids that were roots and are not the smallest of their final class, whatever the order, so the rows (with the
    /// canonical column read at the time of the query) do not depend on the order in which a merge applied the unions,
    /// as long as the unions of one merge carry one timestamp, which the programs ensure.
    /// </remarks>
    sealed class DiffUnionFindModel
    {

        readonly Dictionary<uint, uint> parent = new();
        readonly List<(uint Child, uint Timestamp)> rows = new();

        /// <summary>
        /// Gets every id ever unioned.
        /// </summary>
        public IEnumerable<uint> Ids => parent.Keys;

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/>: the smallest id of its class.
        /// </summary>
        public uint Find(uint id)
        {
            while (parent.TryGetValue(id, out var p) && p != id)
                id = p;

            return id;
        }

        /// <summary>
        /// Unions the classes of <paramref name="a"/> and <paramref name="b"/>, stamping the row with
        /// <paramref name="timestamp"/>.
        /// </summary>
        /// <returns><c>true</c> if the classes were different.</returns>
        public bool Union(uint a, uint b, uint timestamp)
        {
            var ra = Find(a);
            var rb = Find(b);
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            if (ra == rb)
                return false;

            var (root, child) = ra < rb ? (ra, rb) : (rb, ra);
            parent[child] = root;
            parent[root] = root;
            rows.Add((child, timestamp));
            return true;
        }

        /// <summary>
        /// Lists the table's rows, <c>[child, canonical, ts]</c>, the canonical column read now.
        /// </summary>
        public List<uint[]> Rows()
        {
            var result = new List<uint[]>(rows.Count);
            foreach (var (child, ts) in rows)
                result.Add([child, Find(child), ts]);

            return result;
        }

    }

}
