namespace Eggnog.Relations
{

    /// <summary>
    /// A variable a stage binds and the column it reads it from: one entry of egglog's
    /// <c>bind: SmallVec&lt;[(ColumnId, Variable); 2]&gt;</c> (core-relations/src/free_join/plan.rs:147, 154).
    /// </summary>
    /// <remarks>
    /// For a <see cref="FusedIntersectStage"/> the column is a column of the cover atom (the last one for a repeated
    /// variable). For a <see cref="FusedIntersectMatStage"/> in mode <see cref="MatScanMode.Full"/> or
    /// <see cref="MatScanMode.KeyOnly"/>, a column below the key length reads the key and the rest read the value row
    /// (offset by the key length); in mode <see cref="MatScanMode.Value"/> it indexes the value row directly.
    /// </remarks>
    /// <param name="Column">The column read.</param>
    /// <param name="Variable">The variable bound.</param>
    internal readonly record struct StageBinding(ColumnId Column, Variable Variable)
    {

        /// <inheritdoc />
        public override string ToString() => Column + "->" + Variable;

    }

}
