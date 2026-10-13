namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a function of a bridge differential program is, which fixes its default, its merge and the sort of its
    /// output, as egglog's frontend declares them.
    /// </summary>
    enum BridgeDiffFunctionKind
    {

        /// <summary>
        /// A constructor: an eq-sort output, a fresh id for a missing key (<see cref="DefaultVal.FreshId"/>), and the
        /// union-id merge. Rules build terms by looking constructors up.
        /// </summary>
        Constructor,

        /// <summary>
        /// A relation, declared as egglog's frontend declares it: a constructor into a fresh sort of its own that
        /// nothing unions (egglog/src/ast/desugar.rs:396-427, egglog/src/lib.rs:789-850), so a fresh id for a missing
        /// key (<see cref="DefaultVal.FreshId"/>) and the union-id merge. Every row has an id of its own, which a
        /// rebuild unions with another row's when their keys meet. Programs write its output as the unit sort
        /// (<see cref="BridgeDiffFunction.IsNoOutput"/>).
        /// </summary>
        Relation,

        /// <summary>
        /// A function to integers merged by the minimum (<c>:merge (min old new)</c>), a lattice; looking up a missing
        /// key fails, so rules only read it through atoms.
        /// </summary>
        Min,

        /// <summary>
        /// A function to integers merged by the maximum (<c>:merge (max old new)</c>), a lattice.
        /// </summary>
        Max,

        /// <summary>
        /// A function to an eq sort merged by union (the union-id merge, a lattice) whose lookups fail on a missing
        /// key, as a custom function with an eq-sort output.
        /// </summary>
        IdFunction,

    }

}
