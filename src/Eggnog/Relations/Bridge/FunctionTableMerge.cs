using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The merge of a function's table: resolves a conflict between the row the table holds for a key and an incoming
    /// row by merging their outputs with the function's <see cref="ResolvedMergeFn"/> and their subsumption cells with
    /// the maximum. egglog-bridge's <c>MergeFn::to_callback</c> closure (egglog-bridge/src/lib.rs:1059-1101).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The output merge runs with the incoming row's timestamp (a union it stages carries it). The row changes only when
    /// the merged output or the merged subsumption cell differs from the current row's: then the new row is the incoming
    /// row with the merged output and subsumption cell, keeping the incoming key and timestamp, so a real change is a
    /// seminaive delta. Otherwise the current row stays with its old timestamp and no delta appears; a merge that only
    /// staged writes (a union that keeps the current, smaller id) still sets the state's
    /// <see cref="ExecutionState.Changed"/>, which keeps the merge loop going.
    /// </para>
    /// <para>
    /// One virtual call per conflict (the table's call into <see cref="MergeFunction.Merge"/>), then the switch of
    /// <see cref="ResolvedMergeFn.Run"/>; no allocation. A lattice merge exactly when the function's
    /// <see cref="MergeFn"/> is declared one: the subsumption merge, a maximum, is a lattice.
    /// </para>
    /// </remarks>
    internal sealed class FunctionTableMerge : MergeFunction
    {

        readonly ResolvedMergeFn output;
        readonly bool identityOnEqual;
        readonly int outputColumn;
        readonly int timestampColumn;
        readonly int subsumeColumn;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="math">The layout of the function's table.</param>
        /// <param name="output">The resolved merge of the output column.</param>
        /// <param name="isLattice">Whether the function's merge is declared a lattice merge.</param>
        public FunctionTableMerge(SchemaMath math, ResolvedMergeFn output, bool isLattice)
            : base(isLattice)
        {
            ArgumentNullException.ThrowIfNull(output);
            this.output = output;
            identityOnEqual = output.Kind is MergeFnKind.UnionId or MergeFnKind.AssertEq or MergeFnKind.Old or MergeFnKind.New or MergeFnKind.Function or MergeFnKind.IdConst;
            Math = math;
            outputColumn = math.OutputColumn;
            timestampColumn = math.TimestampColumn;
            subsumeColumn = math.SubsumeColumnOrNone;
        }

        /// <summary>
        /// Gets the layout of the function's table.
        /// </summary>
        public SchemaMath Math { get; }

        /// <summary>
        /// Gets the resolved merge of the output column.
        /// </summary>
        public ResolvedMergeFn Output => output;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            var timestamp = incoming[timestampColumn];
            var currentOutput = current[outputColumn];
            var incomingOutput = incoming[outputColumn];

            // Re-deriving a known fact is the common conflict. Merges that return the common value of equal inputs
            // without side effects (every case but a constant or a primitive) need not run then.
            var mergedOutput = identityOnEqual && incomingOutput == currentOutput
                ? currentOutput
                : this.output.Run(state, currentOutput, incomingOutput, timestamp);
            var changed = mergedOutput != currentOutput;

            uint mergedSubsume = 0;
            if (subsumeColumn >= 0)
            {
                // combine_subsumed: subsumption is sticky (lib.rs:1529-1531).
                var currentSubsume = current[subsumeColumn];
                mergedSubsume = System.Math.Max(currentSubsume, incoming[subsumeColumn]);
                changed |= mergedSubsume != currentSubsume;
            }

            if (changed == false)
                return false;

            // The new row is the incoming one with the merged cells; it keeps the incoming timestamp (lib.rs:1087-1097).
            incoming.CopyTo(output);
            output[outputColumn] = mergedOutput;
            if (subsumeColumn >= 0)
                output[subsumeColumn] = mergedSubsume;

            return true;
        }

    }

}
