using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A rule's progress in a <see cref="ReferenceRuleContext"/>: the tick of its last run there and the canonical
    /// forms its id constants had then; the reference's counterpart of the engine's <see cref="RuleProgress"/>.
    /// </summary>
    /// <param name="Time">The tick of the last run.</param>
    /// <param name="Constants">The canonical forms of the rule's id constants at that run (K).</param>
    readonly record struct ReferenceRuleProgress(long Time, ulong[] Constants)
    {

        /// <summary>
        /// Gets whether the progress was made with the constants <paramref name="constants"/>.
        /// </summary>
        public bool SameConstants(IReadOnlyList<ulong> constants) => Constants.SequenceEqual(constants);

    }

}
