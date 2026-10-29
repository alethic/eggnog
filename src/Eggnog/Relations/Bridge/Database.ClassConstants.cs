using System;

namespace Eggnog.Relations
{

    internal sealed partial class Database
    {

        /// <summary>
        /// The ids of the class constants merges hold (<see cref="MergeFnKind.IdConst"/>), as written, by slot. Every
        /// access is exclusive: a slot is added while a function is defined, and the constants are refreshed by a rebuild
        /// pass and read by the merges of the root, none of which runs beside another; a child reads none of them.
        /// </summary>
        uint[] classConstantIds = Array.Empty<uint>();

        /// <summary>
        /// The class constants as the last rebuild pass left them canonical, by slot.
        /// </summary>
        uint[] classConstantValues = Array.Empty<uint>();

        /// <summary>
        /// Adds a class constant a merge holds and returns its slot: egglog's global, which it lowers to a function of no
        /// keys whose one row the rebuild keeps canonical (src/ast/remove_globals.rs), and which a merge reads as that row
        /// holds it, not as the union-find holds it then. Under the definition lock; not while the database rebuilds.
        /// </summary>
        /// <param name="id">The class's id, as written.</param>
        /// <param name="canonical">Its canonical id now.</param>
        /// <returns>The slot <see cref="ReadClassConstant"/> reads.</returns>
        internal int AddClassConstant(uint id, uint canonical)
        {
            var slot = classConstantIds.Length;
            Array.Resize(ref classConstantIds, slot + 1);
            Array.Resize(ref classConstantValues, slot + 1);
            classConstantIds[slot] = id;
            classConstantValues[slot] = canonical;
            return slot;
        }

        /// <summary>
        /// Gets the class constant in <paramref name="slot"/> as the last rebuild pass left it canonical. Every merge
        /// that reads a slot was resolved against this database, or a database it was cloned from, which added the slot
        /// first, so a missing slot is a defect and throws.
        /// </summary>
        /// <param name="slot">The slot.</param>
        /// <returns>The class's id.</returns>
        internal uint ReadClassConstant(int slot) => classConstantValues[slot];

        /// <summary>
        /// Makes every class constant canonical against <paramref name="unionFind"/>, as a rebuild pass makes the row of
        /// egglog's lowered global canonical with the other rows it rebuilds (egglog-bridge/src/lib.rs:668), so that the
        /// merges of the pass, and of the writes after it, read the class as the pass left it. Requires exclusive access.
        /// </summary>
        /// <param name="unionFind">The union-find table.</param>
        void RefreshClassConstants(TableId unionFind)
        {
            var ids = classConstantIds;
            if (ids.Length == 0 || GetTable(unionFind) is not DisplacedTable table)
                return;

            var values = classConstantValues;
            for (var i = 0; i < ids.Length; i++)
                values[i] = table.FindNaive(ids[i]);
        }

        /// <summary>
        /// Copies <paramref name="source"/>'s class constants into a clone, whose merges read them by the same slots.
        /// </summary>
        /// <param name="source">The database cloned.</param>
        void CloneClassConstants(Database source)
        {
            classConstantIds = source.classConstantIds;
            classConstantValues = (uint[])source.classConstantValues.Clone();
        }

    }

}
