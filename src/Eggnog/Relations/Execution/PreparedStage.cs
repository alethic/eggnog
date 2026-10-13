using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// What the executor derives once from a <see cref="JoinStage"/>: the columns it projects and the variables it
    /// binds, as flat arrays. egglog rebuilds these per stage invocation (<c>SmallVec</c>s on the stack).
    /// </summary>
    /// <remarks>
    /// Immutable and shared by every run of the plan, on any thread.
    /// </remarks>
    internal sealed class PreparedStage
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="projection">The columns read for <paramref name="bindVariables"/>, in the same order.</param>
        /// <param name="bindVariables">The variables the stage binds.</param>
        public PreparedStage(ColumnId[] projection, Variable[] bindVariables)
        {
            ArgumentNullException.ThrowIfNull(projection);
            ArgumentNullException.ThrowIfNull(bindVariables);
            Projection = projection;
            BindVariables = bindVariables;
        }

        /// <summary>
        /// Gets the columns read for the bound variables: the cover's columns of a <see cref="FusedIntersectStage"/>,
        /// the key-then-value positions of a <see cref="FusedIntersectMatStage"/>; empty for an
        /// <see cref="IntersectStage"/>. Callers must not modify the array.
        /// </summary>
        public ColumnId[] Projection { get; }

        /// <summary>
        /// Gets the variables the stage binds, parallel to <see cref="Projection"/> (the one variable of an
        /// <see cref="IntersectStage"/>). Callers must not modify the array.
        /// </summary>
        public Variable[] BindVariables { get; }

        /// <summary>
        /// Derives the prepared form of <paramref name="stage"/>.
        /// </summary>
        /// <param name="stage">The stage.</param>
        /// <returns>The prepared stage.</returns>
        public static PreparedStage Create(JoinStage stage)
        {
            ArgumentNullException.ThrowIfNull(stage);
            switch (stage)
            {
                case IntersectStage intersect:
                    return new PreparedStage(Array.Empty<ColumnId>(), [intersect.Variable]);
                case FusedIntersectStage fused:
                    return FromBindings(fused.Bind);
                case FusedIntersectMatStage mat:
                    return FromBindings(mat.Bind);
                default:
                    throw new ArgumentException($"Unknown stage kind {stage.Kind}.", nameof(stage));
            }
        }

        static PreparedStage FromBindings(StageBinding[] bind)
        {
            var projection = new ColumnId[bind.Length];
            var variables = new Variable[bind.Length];
            for (var i = 0; i < bind.Length; i++)
            {
                projection[i] = bind[i].Column;
                variables[i] = bind[i].Variable;
            }

            return new PreparedStage(projection, variables);
        }

    }

}
