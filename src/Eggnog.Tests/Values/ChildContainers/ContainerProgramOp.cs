namespace Eggnog.Tests.Values
{

    /// <summary>
    /// What one operation of a random container program does in its scope (<see cref="ContainerProgramOps.Apply"/>),
    /// over the step's small numbers <c>A</c>, <c>B</c>, <c>C</c> (the <c>Num</c> classes it names) and keys <c>K</c>
    /// (a key no scope wrote before), <c>K1</c> and <c>K2</c> (keys of the scope's chain, which may hold nothing).
    /// </summary>
    enum ContainerProgramOp
    {

        /// <summary>Unions <c>(Num A)</c> and <c>(Num B)</c>.</summary>
        Union,

        /// <summary>Unions <c>(Add (Num A) (Num B))</c> and <c>(Num C)</c>.</summary>
        UnionAdd,

        /// <summary>Writes <c>fv(K)</c>, a vector of <c>(Num A)</c> and <c>(Add (Num A) (Num B))</c>.</summary>
        VectorOfAdd,

        /// <summary>Writes <c>fv(K)</c>, a vector of <c>(Num A)</c> and <c>(Num B)</c>.</summary>
        Vector,

        /// <summary>Writes <c>fvv(K)</c>, a vector of the vectors of <c>(Num A) (Num B)</c> and <c>(Num C)</c>.</summary>
        NestedVector,

        /// <summary>Writes <c>fs(K)</c>, a set of <c>(Num A)</c> and <c>(Num B)</c>.</summary>
        Set,

        /// <summary>Writes <c>fm(K)</c>, the map of <c>(Num A)</c> to <c>(Num B)</c>.</summary>
        Map,

        /// <summary>Writes <c>fms(K)</c>, the multiset of <c>(Num A)</c> once and <c>(Num B)</c> twice.</summary>
        MultiSet,

        /// <summary>Writes <c>fp(K)</c>, the pair of <c>(Num A)</c> and <c>(Num B)</c>.</summary>
        Pair,

        /// <summary>Writes <c>ff(K)</c>, <c>Add</c> partially applied to <c>(Num A)</c>.</summary>
        Function,

        /// <summary>Makes <c>(L (vec-of (Num A) (Num B)))</c>.</summary>
        MakeL,

        /// <summary>Writes <c>h</c> of the vector of <c>(Num A)</c> and <c>(Num B)</c> as <c>C</c>, merged with max.</summary>
        Max,

        /// <summary>Writes <c>go(K1)</c>.</summary>
        Go,

        /// <summary>Writes <c>vu(K1, K2)</c>.</summary>
        VectorUnion,

        /// <summary>Writes <c>pu(K1)</c>.</summary>
        PairUnion,

        /// <summary>Runs the ruleset once.</summary>
        Run,

        /// <summary>Runs the ruleset to saturation.</summary>
        Saturate,

        /// <summary>A host batch: makes an <c>L</c>, writes <c>fv(K)</c> and <c>h</c>, and unions two classes when <c>C</c>
        /// is even.</summary>
        Batch,

        /// <summary>Extracts <c>(Num A)</c> and <c>(L (vec-of (Num A) (Num B)))</c>, reporting their costs.</summary>
        Extract,

        /// <summary>Computes <c>vec-union</c> of <c>fv(K1)</c> and <c>fv(K2)</c> in a host write.</summary>
        HostVectorUnion,

        /// <summary>Checks <c>fv(K1) = fv(K2)</c>.</summary>
        CheckVectors,

        /// <summary>Checks <c>fv(K1)</c> against a constant vector, which the scope may not hold.</summary>
        CheckConstantVector,

        /// <summary>Looks up <c>(L (vec-of (Num A) (Num B)))</c>.</summary>
        GetL,

        /// <summary>Lists the matches of the <c>heads</c> query, each class by its name in the view.</summary>
        Query,

    }

}
