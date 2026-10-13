namespace Eggnog.Relations
{

    /// <summary>
    /// How the lift treats a table (colored-contexts spec §11.2): which facts of the cases it may write into their parent.
    /// </summary>
    internal enum LiftTableKind
    {

        /// <summary>
        /// A relation: a key present in every live case and absent in the parent is written with the output every live
        /// case holds for it (for a relation, its unit value). The output must be a base value; a key whose outputs differ
        /// between live cases is not written, so a base output other than unit lifts only where the cases agree.
        /// </summary>
        Relation,

        /// <summary>
        /// A constructor (an id output merged by union): a term present in every live case and absent in the parent is
        /// added to the parent, with the parent id every case's output stands for when there is one (a row), otherwise
        /// with a fresh id (a term), whose equalities a later round lifts.
        /// </summary>
        Constructor,

        /// <summary>
        /// A function: a key every live case holds a value for is written with an id output that one parent id stands for
        /// in every case, or with a base output that every case agrees on or that the table's case join combines.
        /// </summary>
        Function,

    }

}
