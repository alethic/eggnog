namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The join graph a differential query is generated from (<see cref="DiffGenerator"/>): the variables each atom
    /// shares with the others before the remaining columns are filled with fresh or shared variables, repeated
    /// variables, constants and wildcards.
    /// </summary>
    enum DiffShape
    {

        /// <summary>
        /// Atom <c>i</c> joins variables <c>i</c> and <c>i + 1</c>.
        /// </summary>
        Path,

        /// <summary>
        /// Every atom joins the centre with a leaf of its own.
        /// </summary>
        Star,

        /// <summary>
        /// A path whose last atom joins back to the first variable.
        /// </summary>
        Cycle,

        /// <summary>
        /// Atom <c>i</c> joins variable <c>i + 1</c> with a random earlier variable.
        /// </summary>
        Tree,

        /// <summary>
        /// Every atom draws its variables from a small shared pool, which gives cliques, multi-edges and disconnected
        /// parts.
        /// </summary>
        Random,

        /// <summary>
        /// The lost-constraint shape (PlannerDecompositionTests): eight to ten atoms over three tables of four, one and
        /// three columns, nearly every column one of nine variables (repeating within and across atoms), a few
        /// constants, and only a few variables read by the action. Its tree decompositions often have a block whose
        /// message variables no single later bag holds and that has no value variables, which only a materialization
        /// lookup in the result block keeps (map §8.1 row 5).
        /// </summary>
        Dense,

    }

}
