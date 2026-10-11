using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// A scan or lookup of the materialization written by an earlier block of a decomposed plan. egglog's
    /// <c>JoinStage::FusedIntersectMat</c> (core-relations/src/free_join/plan.rs:151-156).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Mode"/> decides what is read (see <see cref="MatScanMode"/>). <see cref="ModeVariables"/> holds the
    /// key variables of <see cref="MatScanMode.Value"/> and <see cref="MatScanMode.Lookup"/> (egglog's
    /// <c>MatScanMode::Value(vars)</c> / <c>Lookup(vars)</c>) and is empty otherwise.
    /// </para>
    /// <para>
    /// <see cref="ToIntersect"/> is used by <see cref="MatScanMode.KeyOnly"/> prologues: for each atom of the bag that
    /// holds message variables, an index over those columns probed with the materialization key.
    /// </para>
    /// </remarks>
    internal sealed class FusedIntersectMatStage : JoinStage
    {

        /// <summary>
        /// Initializes a new instance. The arrays are owned by the new instance.
        /// </summary>
        /// <param name="cover">The materialization.</param>
        /// <param name="mode">How it is read.</param>
        /// <param name="modeVariables">The key variables of a value scan or lookup; empty otherwise.</param>
        /// <param name="bind">The variables bound from keys or value rows.</param>
        /// <param name="toIntersect">The indexes to probe with the key.</param>
        public FusedIntersectMatStage(MatId cover, MatScanMode mode, Variable[] modeVariables, StageBinding[] bind, Probe[] toIntersect) :
            base(JoinStageKind.FusedIntersectMat)
        {
            ArgumentNullException.ThrowIfNull(modeVariables);
            ArgumentNullException.ThrowIfNull(bind);
            ArgumentNullException.ThrowIfNull(toIntersect);
            if ((mode == MatScanMode.Value || mode == MatScanMode.Lookup) == false && modeVariables.Length != 0)
                throw new ArgumentException("Only value scans and lookups carry key variables.", nameof(modeVariables));

            Cover = cover;
            Mode = mode;
            ModeVariables = modeVariables;
            Bind = bind;
            ToIntersect = toIntersect;
        }

        /// <summary>
        /// Gets the materialization (the index of the block that wrote it).
        /// </summary>
        public MatId Cover { get; }

        /// <summary>
        /// Gets how the materialization is read.
        /// </summary>
        public MatScanMode Mode { get; }

        /// <summary>
        /// Gets the key variables of a <see cref="MatScanMode.Value"/> or <see cref="MatScanMode.Lookup"/>; empty for
        /// the other modes. Callers must not modify the array.
        /// </summary>
        public Variable[] ModeVariables { get; }

        /// <summary>
        /// Gets the variables bound. Callers must not modify the array.
        /// </summary>
        public StageBinding[] Bind { get; }

        /// <summary>
        /// Gets the indexes probed with the key. Callers must not modify the array.
        /// </summary>
        public Probe[] ToIntersect { get; }

        /// <summary>
        /// Gets whether this stage is a reorder barrier: a <see cref="MatScanMode.Full"/>, <see cref="MatScanMode.Value"/>
        /// or <see cref="MatScanMode.Lookup"/> scan (core-relations/src/free_join/execute.rs:2296-2302).
        /// </summary>
        public bool IsBarrier => Mode != MatScanMode.KeyOnly;

        /// <summary>
        /// Formats as <c>Mat m0 KeyOnly bind[c0-&gt;v2] probe[...]</c>, <c>Mat m0 Value[v2] bind[c0-&gt;v4]</c> or
        /// <c>Mat m0 Lookup[v0,v2]</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append("Mat ").Append(Cover).Append(' ').Append(Mode);
            if (Mode == MatScanMode.Value || Mode == MatScanMode.Lookup)
            {
                b.Append('[');
                PlanText.AppendJoined(b, ModeVariables, ",");
                b.Append(']');
            }

            PlanText.AppendBind(b, Bind);
            PlanText.AppendProbes(b, ToIntersect);
            return b.ToString();
        }

    }

}
