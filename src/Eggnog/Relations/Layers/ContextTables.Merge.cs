namespace Eggnog.Relations
{

    internal sealed partial class ContextTables
    {

        /// <summary>
        /// The epilogue of every merge of the context's database (<see cref="Database.MergeAll"/>,
        /// <see cref="Database.MergeTable"/>), run in its <c>finally</c> block: seals every hidden set that gained a
        /// hide during the merge, then drops the views whose structure changed. Afterwards no set is unsealed: outside
        /// a merge of the context, every hidden set is sealed.
        /// </summary>
        internal void AfterMerge()
        {
            SealHidden();
            InvalidateViews(false);
        }

        /// <summary>
        /// Seals every hidden set that gained a hide since the last seal. Domain thread only. O(Σ sets touched).
        /// </summary>
        internal void SealHidden()
        {
            if (unsealed.Count == 0)
                return;

            foreach (var (set, layer) in unsealed)
                set.Seal(layer.Rows.Count);

            unsealed.Clear();
        }

        /// <summary>
        /// Stops the context because a contradiction row became visible in it: sets the sticky
        /// <see cref="Context.IsContradicted"/> and moves its change stamp, when its options ask for it
        /// (<see cref="ContextOptions.StopOnContradiction"/>); otherwise a contradiction is an ordinary fact.
        /// </summary>
        /// <remarks>
        /// Once set, the context's merge stops after the current round with the remaining writes pending, its rebuild
        /// returns at once, layers are no longer created, writes throw, and reads throw except
        /// <see cref="Context.IsContradictory"/>.
        /// </remarks>
        internal void SetContradicted()
        {
            if (owner.Options.StopOnContradiction == false || owner.IsContradicted)
                return;

            owner.MarkContradicted();
            owner.Database.NoteLayerChange();
        }

    }

}
