namespace Eggnog.Tests.Values
{

    /// <summary>
    /// One step of a random container program: a scope's creation, disposal, freeze or snapshot, or an operation in it.
    /// Scopes are numbered, the root 0.
    /// </summary>
    /// <param name="Kind">What the step does.</param>
    /// <param name="Scope">The scope it acts on.</param>
    /// <param name="Parent">For <see cref="ContainerProgramStepKind.Create"/>, the new scope's parent.</param>
    /// <param name="Op">For <see cref="ContainerProgramStepKind.Operation"/>, the operation.</param>
    /// <param name="A">The first class the operation names, <c>(Num A)</c>.</param>
    /// <param name="B">The second class.</param>
    /// <param name="C">The third class, or a number.</param>
    /// <param name="K">A key no scope wrote before.</param>
    /// <param name="K1">A key of the scope's chain.</param>
    /// <param name="K2">Another key of the scope's chain.</param>
    readonly record struct ContainerProgramStep(ContainerProgramStepKind Kind, int Scope, int Parent, ContainerProgramOp Op,
        long A, long B, long C, long K, long K1, long K2)
    {

        /// <summary>
        /// Gets whether the operation only reads, so a frozen scope may apply it.
        /// </summary>
        public bool ReadsOnly => Op is ContainerProgramOp.Extract or ContainerProgramOp.CheckVectors
            or ContainerProgramOp.CheckConstantVector or ContainerProgramOp.GetL or ContainerProgramOp.Query;

        /// <summary>
        /// Gets whether the operation reads the scope's own facts to decide what it writes, so it runs only in a scope
        /// without live children: the replay merges a scope's chain into one root, where the operation would also see
        /// the facts of the scope's descendants.
        /// </summary>
        public bool LeafOnly => Op is ContainerProgramOp.Run or ContainerProgramOp.Saturate or ContainerProgramOp.HostVectorUnion;

        /// <inheritdoc />
        public override string ToString() => Kind switch
        {
            ContainerProgramStepKind.Create => $"create s{Scope} < s{Parent}",
            ContainerProgramStepKind.Dispose => $"dispose s{Scope}",
            ContainerProgramStepKind.Freeze => $"freeze s{Scope}",
            ContainerProgramStepKind.Snapshot => $"snapshot s{Scope}",
            _ => $"s{Scope}: {Op} a{A} b{B} c{C} k{K} k1={K1} k2={K2}",
        };

    }

}
