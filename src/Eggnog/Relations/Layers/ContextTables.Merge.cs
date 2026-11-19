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

    }

}
