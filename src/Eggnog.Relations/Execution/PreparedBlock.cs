using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// What the executor derives once from a stage list (a <see cref="SinglePlan"/>'s stages, a block of a
    /// <see cref="DecomposedPlan"/>, or its result block): each stage's <see cref="PreparedStage"/>, the variables the
    /// list binds, and the sizes of the per-position scratch the join needs.
    /// </summary>
    /// <remarks>
    /// Plan templates are immutable and shared by every run, so the prepared form is cached per stage array, weakly, and
    /// computed on first use (<see cref="Get"/>). Immutable; safe to share across threads.
    /// </remarks>
    internal sealed class PreparedBlock
    {

        static readonly ConditionalWeakTable<JoinStage[], PreparedBlock> cache = new();

        PreparedBlock(JoinStage[] stages)
        {
            Stages = new PreparedStage[stages.Length];
            var bound = new SortedSet<int>();
            var maxProbers = 1;
            var maxKey = 1;
            var maxBind = 1;
            for (var i = 0; i < stages.Length; i++)
            {
                var stage = stages[i];
                var prepared = PreparedStage.Create(stage);
                Stages[i] = prepared;
                foreach (var v in prepared.BindVariables)
                    bound.Add(v.Index);

                maxBind = Math.Max(maxBind, prepared.BindVariables.Length);
                switch (stage)
                {
                    case IntersectStage intersect:
                        maxProbers = Math.Max(maxProbers, intersect.Scans.Length);
                        break;
                    case FusedIntersectStage fused:
                        maxProbers = Math.Max(maxProbers, fused.ToIntersect.Length);
                        foreach (var p in fused.ToIntersect)
                            maxKey = Math.Max(maxKey, p.Key.Length);
                        break;
                    case FusedIntersectMatStage mat:
                        maxProbers = Math.Max(maxProbers, mat.ToIntersect.Length);
                        maxKey = Math.Max(maxKey, mat.ModeVariables.Length);
                        foreach (var p in mat.ToIntersect)
                            maxKey = Math.Max(maxKey, p.Key.Length);
                        break;
                }
            }

            BoundVariables = new Variable[bound.Count];
            var k = 0;
            foreach (var v in bound)
                BoundVariables[k++] = new Variable(v);

            MaxProbers = maxProbers;
            MaxKey = maxKey;
            MaxBind = maxBind;
        }

        /// <summary>
        /// Gets the prepared form of each stage, by stage index.
        /// </summary>
        public PreparedStage[] Stages { get; }

        /// <summary>
        /// Gets the variables the stages bind, ascending, each once.
        /// </summary>
        public Variable[] BoundVariables { get; }

        /// <summary>
        /// Gets the most probers one stage uses (at least one).
        /// </summary>
        public int MaxProbers { get; }

        /// <summary>
        /// Gets the most cells of a key one stage builds to probe an index or look up a materialization (at least one).
        /// </summary>
        public int MaxKey { get; }

        /// <summary>
        /// Gets the most variables one stage binds (at least one).
        /// </summary>
        public int MaxBind { get; }

        /// <summary>
        /// Gets the prepared form of <paramref name="stages"/>, computing it on first use. Thread-safe.
        /// </summary>
        /// <param name="stages">The stage list of a plan template; must not change.</param>
        /// <returns>The prepared form.</returns>
        public static PreparedBlock Get(JoinStage[] stages)
        {
            ArgumentNullException.ThrowIfNull(stages);
            return cache.GetValue(stages, static s => new PreparedBlock(s));
        }

    }

}
