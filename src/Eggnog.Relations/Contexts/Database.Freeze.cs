using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class Database
    {

        /// <summary>
        /// Freezes the database with its context (colored-contexts spec §8.1 step 4): every table freezes
        /// (<see cref="Table.Freeze"/>: the union-find compresses, a sorted-writes table compacts where its gate allows and
        /// builds its rebuild index, each seals its pending state), the indexes of <paramref name="warmup"/> are built, every
        /// index catalog switches to serial refresh (X14), and the frozen flag is published. Afterwards merges, rebuilds,
        /// clears, rule runs and new buffers throw (<see cref="ThrowIfFrozen"/>). A second call does nothing.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="Context.Freeze"/> on the context's domain thread, after its rebuild: nothing is staged.
        /// Every table is checked before any is frozen, so a refusal leaves the database as it was. Cost: O(ids + rows) for
        /// the union-find and the compactions, plus the rebuild indexes and the warm-up.
        /// </remarks>
        /// <param name="warmup">The cached indexes to build, or <c>null</c>.</param>
        /// <exception cref="InvalidOperationException">Writes are staged and not merged (notified, or staged into a table
        /// without notifying it).</exception>
        internal void Freeze(FreezeWarmup? warmup = null)
        {
            if (frozen)
                return;

            // A layer created by a search that no merge round followed still freezes with the others (X37).
            layers?.AdoptDeferred();
            if (catalog.Notifications.IsEmpty == false)
                throw new InvalidOperationException("Writes are staged and not merged; merge them before freezing.");

            foreach (var (_, info) in tables)
                if (info.Table.HasPendingWrites)
                    throw new InvalidOperationException($"Table {info.Name ?? "<unnamed>"} has staged writes that are not merged; merge them before freezing.");

            foreach (var (_, info) in tables)
                info.Table.Freeze();

            if (warmup is not null)
            {
                foreach (var key in warmup.Keys)
                {
                    if (tables.TryGetValue(key.Table, out var info) == false)
                        continue;

                    var columns = key.CheckedColumns;
                    if (columns.Length == 1)
                        info.GetColumnIndex(columns[0]);
                    else
                        info.GetIndex(columns);
                }
            }

            foreach (var (_, info) in tables)
                info.FreezeIndexes();

            frozen = true;
        }

        /// <summary>
        /// Throws when the database is frozen: the guard of every entry point that would change it (§8.1). One volatile
        /// read.
        /// </summary>
        /// <exception cref="InvalidOperationException">The database is frozen.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ThrowIfFrozen()
        {
            if (frozen)
                ThrowFrozen();
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowFrozen()
        {
            throw new InvalidOperationException("The context is frozen.");
        }

    }

}
