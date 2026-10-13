using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The naive model of one context's union-find: the ids minted in it and the unions asserted in it, in order.
    /// Context = copy: the context's partition is the closure of every union asserted in it or in any ancestor,
    /// whenever the ancestor asserted it, and its canonical ids are the class minima.
    /// </summary>
    sealed class ReferenceUnionFindContext
    {

        readonly List<(uint A, uint B)> unions = new();
        readonly HashSet<uint> minted = new();

        /// <summary>
        /// Initializes a root.
        /// </summary>
        public ReferenceUnionFindContext()
        {
            DisplacedInParentAtCreation = new HashSet<uint>();
        }

        /// <summary>
        /// Initializes a child of <paramref name="parent"/>, recording the ids displaced in the parent's view now.
        /// </summary>
        /// <param name="parent">The parent.</param>
        ReferenceUnionFindContext(ReferenceUnionFindContext parent)
        {
            Parent = parent;
            Depth = parent.Depth + 1;
            var canonicals = parent.Canonicals();
            DisplacedInParentAtCreation = canonicals.Where(pair => pair.Key != pair.Value).Select(pair => pair.Key).ToHashSet();
        }

        /// <summary>
        /// Gets the parent, or <c>null</c> for the root.
        /// </summary>
        public ReferenceUnionFindContext? Parent { get; }

        /// <summary>
        /// Gets the depth.
        /// </summary>
        public int Depth { get; }

        /// <summary>
        /// Gets the ids minted here.
        /// </summary>
        public IReadOnlySet<uint> Minted => minted;

        /// <summary>
        /// Gets the ids that were not canonical in the parent's view when this context was created: the ids a context's
        /// journal must not hold.
        /// </summary>
        public IReadOnlySet<uint> DisplacedInParentAtCreation { get; }

        /// <summary>
        /// Gets or sets whether the context is frozen.
        /// </summary>
        public bool IsFrozen { get; set; }

        /// <summary>
        /// Creates a child.
        /// </summary>
        /// <returns>The child.</returns>
        public ReferenceUnionFindContext CreateChild() => new(this);

        /// <summary>
        /// Records an id minted here.
        /// </summary>
        /// <param name="id">The id.</param>
        public void Mint(uint id)
        {
            if (minted.Add(id) == false)
                throw new InvalidOperationException($"Id {id} was minted twice.");
        }

        /// <summary>
        /// Records a union asserted here.
        /// </summary>
        /// <param name="a">An id.</param>
        /// <param name="b">Another id.</param>
        /// <returns>Whether the two were in different classes in this context's view.</returns>
        public bool Union(uint a, uint b)
        {
            var canonicals = Canonicals();
            var distinct = canonicals[a] != canonicals[b];
            unions.Add((a, b));
            return distinct;
        }

        /// <summary>
        /// Gets the ids visible here: minted here or in any ancestor.
        /// </summary>
        /// <returns>The ids, ascending.</returns>
        public uint[] VisibleIds()
        {
            var ids = new SortedSet<uint>();
            for (var c = this; c is not null; c = c.Parent)
                ids.UnionWith(c.minted);

            return ids.ToArray();
        }

        /// <summary>
        /// Gets every visible id's canonical id in this context: the minimum of its class under every union of the
        /// chain.
        /// </summary>
        /// <returns>The canonical id of each visible id.</returns>
        public Dictionary<uint, uint> Canonicals()
        {
            var parent = new Dictionary<uint, uint>();
            foreach (var id in VisibleIds())
                parent[id] = id;

            uint Root(uint x)
            {
                while (parent[x] != x)
                    x = parent[x];

                return x;
            }

            for (var c = this; c is not null; c = c.Parent)
            {
                foreach (var (a, b) in c.unions)
                {
                    var ra = Root(a);
                    var rb = Root(b);
                    if (ra != rb)
                        parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                }
            }

            var result = new Dictionary<uint, uint>();
            foreach (var id in parent.Keys)
                result[id] = Root(id);

            return result;
        }

    }

}
