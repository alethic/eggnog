using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What the children of the context differential programs exercised: the row catch-up, the copies by reason, the
    /// rebuild's hide-and-copy step, retractions and freezes, accumulated from each child's
    /// <see cref="LayerStatistics"/> and its hides (whose reasons DEBUG builds record).
    /// </summary>
    sealed class Coverage
    {

        /// <summary>
        /// The ancestor rows catch-ups examined.
        /// </summary>
        public long CatchUpRowsExamined;

        /// <summary>
        /// The ancestor rows the rebuild's hide-and-copy step examined.
        /// </summary>
        public long AncestorRowsExamined;

        /// <summary>
        /// The ancestor rows copied into children, for any reason (<see cref="LayerStatistics.RowsCopied"/>); counted
        /// in every build.
        /// </summary>
        public long RowsCopied;

        /// <summary>
        /// The rows hidden for naming a loser (<see cref="HideReason.Mentions"/>); DEBUG builds only, where the engine
        /// records why each row was hidden (<see cref="TableLayer.ReasonFor"/>), and zero otherwise.
        /// </summary>
        public long Mentions;

        /// <summary>
        /// The rows hidden for their key (<see cref="HideReason.KeyHeld"/>); DEBUG builds only, like
        /// <see cref="Mentions"/>.
        /// </summary>
        public long KeyHeld;

        /// <summary>
        /// The rows a catch-up copied only to re-stamp them, because their timestamp lies below the context's
        /// semi-naive floor; expected to stay zero, since every writer stamps rows with its state's timestamp.
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
        public void Add(ContextPair pair)
        {
            if (pair.Engine.Tables is not { } tables)
                return;

            var statistics = tables.Statistics;
            CatchUpRowsExamined += statistics.CatchUpRowsExamined;
            AncestorRowsExamined += statistics.AncestorRowsExamined;
            RowsCopied += statistics.RowsCopied;
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
