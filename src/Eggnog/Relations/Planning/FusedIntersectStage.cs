using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// A Free Join step: enumerate the rows of the <see cref="Cover"/> sub-atom, bind the variables in
    /// <see cref="Bind"/>, and probe each of <see cref="ToIntersect"/> with a key built from the cover's tuple. egglog's
    /// <c>JoinStage::FusedIntersect</c> (core-relations/src/free_join/plan.rs:145-150).
    /// </summary>
    /// <remarks>
    /// With a whole-atom cover and one probe this is a hash join; with no probes it is a plain scan of one atom (the
    /// generic-join planner emits those for single-occurrence variables, fused per atom so the executor can take the
    /// factorized leaf-scan path).
    /// </remarks>
    internal sealed class FusedIntersectStage : JoinStage
    {

        /// <summary>
        /// Initializes a new instance. The arrays are owned by the new instance.
        /// </summary>
        /// <param name="cover">The sub-atom to enumerate.</param>
        /// <param name="bind">The variables bound from the cover's rows.</param>
        /// <param name="toIntersect">The indexes to probe.</param>
        public FusedIntersectStage(ScanSpec cover, StageBinding[] bind, Probe[] toIntersect) :
            base(JoinStageKind.FusedIntersect)
        {
            ArgumentNullException.ThrowIfNull(cover);
            ArgumentNullException.ThrowIfNull(bind);
            ArgumentNullException.ThrowIfNull(toIntersect);
            Cover = cover;
            Bind = bind;
            ToIntersect = toIntersect;
        }

        /// <summary>
        /// Gets the sub-atom to enumerate, with the cover atom's slow constraints if this is its first scan.
        /// </summary>
        public ScanSpec Cover { get; }

        /// <summary>
        /// Gets the variables bound from the cover's rows. Callers must not modify the array.
        /// </summary>
        public StageBinding[] Bind { get; }

        /// <summary>
        /// Gets the indexes to probe. Callers must not modify the array.
        /// </summary>
        public Probe[] ToIntersect { get; }

        /// <summary>
        /// Formats as <c>FusedIntersect a1[c0,c1] bind[c0-&gt;v1, c1-&gt;v2] probe[a0[c1] key[0]]</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append("FusedIntersect ").Append(Cover);
            PlanText.AppendBind(b, Bind);
            PlanText.AppendProbes(b, ToIntersect);
            return b.ToString();
        }

    }

}
