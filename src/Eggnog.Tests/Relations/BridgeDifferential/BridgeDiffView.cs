using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What one evaluation of a bridge differential program holds at a point in time: the canonical id of every id it
    /// minted, every function's rows, and the classes its term seeds produced. <see cref="BridgeDiffComparer"/>
    /// compares two views modulo a renaming of ids.
    /// </summary>
    /// <remarks>
    /// Ids are minted densely from zero by every evaluation, so the view lists the canonical id of each id below
    /// <see cref="IdCount"/>. The ids minted before the first rule runs are the same in every evaluation (each term
    /// seed and relation insertion mints exactly one, <see cref="BridgeDiffSeed.MintedIds"/>), which
    /// <see cref="InitialIds"/> records; later ids depend on how each evaluation batched its matches. Immutable once
    /// built: a row's cells are never changed, so keys can wrap them (<see cref="BridgeDiffKey"/>).
    /// </remarks>
    sealed class BridgeDiffView
    {

        readonly uint[] find;
        readonly List<BridgeDiffRow>[] rows;
        readonly uint[] seedIds;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="find">The canonical id of each id; owned by the view.</param>
        /// <param name="rows">The rows of each function; owned by the view.</param>
        /// <param name="seedIds">The id each seed produced (<see cref="uint.MaxValue"/> for seeds that are neither
        /// terms nor relation insertions, or did not run yet); owned by the view.</param>
        /// <param name="initialIds">The number of ids minted before the rules first ran.</param>
        public BridgeDiffView(uint[] find, List<BridgeDiffRow>[] rows, uint[] seedIds, int initialIds)
        {
            this.find = find;
            this.rows = rows;
            this.seedIds = seedIds;
            InitialIds = initialIds;
        }

        /// <summary>
        /// Gets the number of ids minted.
        /// </summary>
        public int IdCount => find.Length;

        /// <summary>
        /// Gets the number of ids minted before the rules first ran.
        /// </summary>
        public int InitialIds { get; }

        /// <summary>
        /// Gets the number of functions.
        /// </summary>
        public int FunctionCount => rows.Length;

        /// <summary>
        /// Gets the total number of rows.
        /// </summary>
        public int TotalRows
        {
            get
            {
                var total = 0;
                foreach (var list in rows)
                    total += list.Count;

                return total;
            }
        }

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/>; an id the view does not know is returned as it is.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The canonical id.</returns>
        public uint Find(uint id) => id < (uint)find.Length ? find[id] : id;

        /// <summary>
        /// Gets the rows of <paramref name="function"/>.
        /// </summary>
        /// <param name="function">The function's index.</param>
        /// <returns>The rows, in no particular order.</returns>
        public IReadOnlyList<BridgeDiffRow> Rows(int function) => rows[function];

        /// <summary>
        /// Gets the id seed <paramref name="seed"/> produced, or <see cref="uint.MaxValue"/>.
        /// </summary>
        /// <param name="seed">The seed's index.</param>
        /// <returns>The id.</returns>
        public uint SeedId(int seed) => seedIds[seed];

    }

}
