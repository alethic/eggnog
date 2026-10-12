using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What the children of the context differential programs exercised (colored-contexts spec §17 M9.4): the row
    /// catch-up, the copies by reason, step 4, retractions and freezes, accumulated from each child's
    /// <see cref="LayerStatistics"/> and its hides (whose reasons DEBUG builds record).
    /// </summary>
    sealed class Coverage
    {

        /// <summary>
        /// The ancestor rows catch-ups examined.
        /// </summary>
        public long CatchUpRowsExamined;

        /// <summary>
        /// The ancestor rows step 4 examined.
        /// </summary>
        public long AncestorRowsExamined;

        /// <summary>
        /// The rows hidden for naming a loser (<see cref="HideReason.Mentions"/>).
        /// </summary>
        public long Mentions;

        /// <summary>
        /// The rows hidden for their key (<see cref="HideReason.KeyHeld"/>).
        /// </summary>
        public long KeyHeld;

        /// <summary>
        /// The rows copied only to re-stamp them (§7.2 c′); expected to stay zero (I21).
        /// </summary>
        public long RestampedCopies;

        /// <summary>
        /// The retractions children made.
        /// </summary>
        public long Retractions;

        /// <summary>
        /// The children frozen.
        /// </summary>
        public long FrozenChildren;

        /// <summary>
        /// Adds what <paramref name="pair"/>'s context did, if it is a child.
        /// </summary>
        public void Add(SpikePair pair)
        {
            if (pair.Engine.Tables is not { } tables)
                return;

            var statistics = tables.Statistics;
            CatchUpRowsExamined += statistics.CatchUpRowsExamined;
            AncestorRowsExamined += statistics.AncestorRowsExamined;
            RestampedCopies += statistics.RestampedCopies;
            FrozenChildren += pair.IsFrozen ? 1 : 0;
            var rows = new List<int>();
            foreach (var table in pair.Program.Schema.Tables)
            {
                if (tables.TryGetLayer(table.Id) is not { } layer)
                    continue;

                for (var j = 0; j < layer.Depth; j++)
                {
                    if (layer.HiddenOver(j) is not { } set)
                        continue;

                    rows.Clear();
                    set.CopyTo(rows);
                    foreach (var row in rows)
                    {
                        switch (layer.ReasonFor(j, row))
                        {
                            case HideReason.Mentions:
                                Mentions++;
                                break;
                            case HideReason.KeyHeld:
                                KeyHeld++;
                                break;
                        }
                    }
                }
            }
        }

    }

}
