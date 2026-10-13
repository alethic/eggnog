using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A <see cref="UnionFindOverlay"/> over a parent <see cref="DisplacedUnionFind"/>, with a journal and a catch-up
    /// cursor, driven exactly as a child context drives its union-find: child unions map both ids through the parent's
    /// find and journal the loser; a catch-up re-keys every new parent displacement with its final parent canonical and
    /// journals each lost id once.
    /// </summary>
    sealed class OverlayRekeyHarness
    {

        /// <summary>
        /// Gets the parent's union-find.
        /// </summary>
        public DisplacedUnionFind Parent { get; } = new();

        /// <summary>
        /// Gets the child's overlay.
        /// </summary>
        public UnionFindOverlay Overlay { get; } = new();

        /// <summary>
        /// Gets the child's journal.
        /// </summary>
        public DisplacedJournal Journal { get; } = new();

        /// <summary>
        /// Gets how many parent rows the child has caught up with.
        /// </summary>
        public int Cursor { get; private set; }

        uint clock = 1;

        /// <summary>
        /// Gets the child's canonical id of <paramref name="x"/>: the overlay over the parent's find.
        /// </summary>
        public uint Find(uint x) => Overlay.Map(Parent.FindNaive(x));

        /// <summary>
        /// Unions two ids in the parent.
        /// </summary>
        public void ParentUnion(uint a, uint b) => Parent.Union(a, b, clock++, out _, out _);

        /// <summary>
        /// Catches up, then unions two ids in the child, journaling the loser.
        /// </summary>
        /// <returns>Whether the classes were distinct.</returns>
        public bool ChildUnion(uint a, uint b)
        {
            CatchUp();
            if (Overlay.Union(Parent.FindNaive(a), Parent.FindNaive(b), out _, out var loser) == false)
                return false;

            Journal.Append(loser, clock++);
            return true;
        }

        /// <summary>
        /// Re-keys the overlay over the parent's new rows.
        /// </summary>
        /// <returns>The ids journaled, in order.</returns>
        public List<uint> CatchUp()
        {
            var journaled = new List<uint>();
            Span<uint> lost = stackalloc uint[2];
            var children = Parent.Children;
            for (var i = Cursor; i < children.Length; i++)
            {
                var d = children[i];
                var n = Overlay.Rekey(d, Parent.FindNaive(d), lost);
                for (var j = 0; j < n; j++)
                {
                    if (Journal.Contains(lost[j]))
                        continue;

                    Journal.Append(lost[j], clock);
                    journaled.Add(lost[j]);
                }
            }

            clock++;
            Cursor = children.Length;
            return journaled;
        }

    }

}
