using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A strategy-neutral description of one stage: the cover sub-atom, the variables it binds, and the sub-atoms to
    /// filter with keys built from the cover. egglog's <c>StageInfo</c> (core-relations/src/free_join/plan.rs:1194-1208);
    /// <see cref="QueryPlanner"/> lowers it to a <see cref="JoinStage"/>.
    /// </summary>
    internal sealed class StageInfo
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cover">The cover sub-atom.</param>
        /// <param name="variables">The variables bound, parallel to the cover's columns.</param>
        /// <param name="filters">The sub-atoms to filter and their key positions into the cover.</param>
        public StageInfo(SubAtom cover, List<Variable> variables, List<(SubAtom SubAtom, ColumnId[] Key)> filters)
        {
            ArgumentNullException.ThrowIfNull(cover);
            ArgumentNullException.ThrowIfNull(variables);
            ArgumentNullException.ThrowIfNull(filters);
            Cover = cover;
            Variables = variables;
            Filters = filters;
        }

        /// <summary>
        /// Gets the cover sub-atom.
        /// </summary>
        public SubAtom Cover { get; }

        /// <summary>
        /// Gets the variables bound.
        /// </summary>
        public List<Variable> Variables { get; }

        /// <summary>
        /// Gets the sub-atoms to filter, each with one key position per column.
        /// </summary>
        public List<(SubAtom SubAtom, ColumnId[] Key)> Filters { get; }

    }

}
