using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// A case split (§6.9.2, D43), defined by <see cref="EGraph.DefineSplit"/> or <see cref="EGraph.DefineDomainSplit"/>
    /// and taken by a split step (<see cref="Schedule.Split(SplitRule, Schedule)"/>): wherever its body matches in the
    /// running scope, one case per element of the matched set, each assuming its element. Immutable; usable from any thread.
    /// </summary>
    public sealed class SplitRule
    {

        readonly object sync = new();
        RuleSplitSource[] sources = Array.Empty<RuleSplitSource>();

        /// <summary>
        /// Initializes a split rule; the graph sets its program once compiled.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="builder">What its builder collected, for rendering.</param>
        /// <param name="caseTag">The tag of its cases; <c>null</c> for none.</param>
        internal SplitRule(EGraph graph, SplitBuilder builder, ScopeTag? caseTag)
        {
            Graph = graph;
            Name = builder.Draft.Name;
            CaseTag = caseTag;
            Builder = builder;
        }

        /// <summary>
        /// Gets the split rule's name, unique among the graph's split rules.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the tag of the cases it makes; <c>null</c>: its cases are untagged.
        /// </summary>
        public ScopeTag? CaseTag { get; }

        /// <summary>
        /// Gets the distinct tags its body's scoped atoms read, in first-use order.
        /// </summary>
        public IReadOnlyList<ScopeTag> ReadScopes { get; internal set; } = Array.Empty<ScopeTag>();

        /// <summary>
        /// Renders the split rule as written: <c>(split (body…) (cases v d) (key k) (assume…) :case-tag case)</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => SExpression.Render(this);

        /// <summary>
        /// Gets the graph.
        /// </summary>
        internal EGraph Graph { get; }

        /// <summary>
        /// Gets what its builder collected: the body, the set, the case variable, the key and the assumptions as written.
        /// </summary>
        internal SplitBuilder Builder { get; }

        /// <summary>
        /// Gets the compiled matcher and assumption template.
        /// </summary>
        internal SplitProgram Program { get; set; } = null!;

        /// <summary>
        /// Gets the split source of this rule at position <paramref name="index"/> of a split step's rules (§6.9.2, E8): its
        /// requests' keys carry the index, which the driver resolves in its own source list. One source per rule and
        /// position, kept for the graph's life, so that two split steps listing the rule at the same position share it,
        /// and a live split one opened covers the other's request for the same key (B6, which compares sources by
        /// reference). Thread-safe; lock-free once made.
        /// </summary>
        /// <param name="index">The rule's position among a split step's rules.</param>
        /// <returns>The source.</returns>
        internal RuleSplitSource SourceAt(int index)
        {
            var known = Volatile.Read(ref sources);
            if (index < known.Length)
                return known[index];

            lock (sync)
            {
                known = sources;
                if (index >= known.Length)
                {
                    var next = new RuleSplitSource[index + 1];
                    known.CopyTo(next, 0);
                    for (var i = known.Length; i < next.Length; i++)
                        next[i] = new RuleSplitSource(Graph, this, i);

                    Volatile.Write(ref sources, next);
                    known = next;
                }

                return known[index];
            }
        }

    }

}
