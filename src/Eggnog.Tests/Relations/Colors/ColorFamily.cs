namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The program family of a rule differential (colored-contexts spec §17 M10.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Minting"/> chooses family B (constructor minting: host terms and rule mints, views compared by
    /// <see cref="TermNamer"/> names) over family A (explicit ids, no minting, views compared cell for cell; the engine
    /// and the reference agree on every canonical id, since both make the least id of a class canonical, which is stronger
    /// than the spec's modulo-renaming comparison).
    /// </para>
    /// <para>
    /// <see cref="History"/> chooses the history family over the monotone one: rules may carry <see cref="ColorFilter"/>s
    /// (assert-ne on ids, and in family A an id-order primitive) and <see cref="ColorActionKind.Read"/> actions, and
    /// programs delete and clear rows wherever §6.7 is asked, the engine refusing where it does not allow it.
    /// <see cref="DeletesOnlyUnwritten"/> limits deletes and clears to tables no rule writes, for the seeded-versus-unseeded
    /// property, which a child's delete of a derived fact would break by design (an unseeded child re-derives it).
    /// </para>
    /// </remarks>
    /// <param name="Minting">Whether the program is family B.</param>
    /// <param name="History">Whether the program is of the history family.</param>
    /// <param name="DeletesOnlyUnwritten">Whether deletes and clears touch only tables that no rule writes.</param>
    readonly record struct ColorFamily(bool Minting, bool History, bool DeletesOnlyUnwritten = false)
    {

        /// <inheritdoc />
        public override string ToString() => $"family {(Minting ? "B" : "A")}, {(History ? "history" : "monotone")}{(DeletesOnlyUnwritten ? ", deletes of unwritten tables only" : "")}";

    }

}
