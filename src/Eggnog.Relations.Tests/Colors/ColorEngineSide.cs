using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One engine a <see cref="ColorRunner"/> drives: its e-graph, its rules and queries, and its contexts and ids by slot.
    /// The runner drives two in lockstep for the seeded-versus-unseeded property, the second creating every child with
    /// <see cref="ContextOptions.InheritWatermarks"/> off.
    /// </summary>
    sealed class ColorEngineSide
    {

        /// <summary>
        /// Initializes a side over a fresh schema; children inherit watermarks unless <paramref name="inherit"/> is false.
        /// </summary>
        public ColorEngineSide(bool inherit)
        {
            Schema = new ColorSchema();
            Inherit = inherit;
            Contexts.Add(Schema.Root);
        }

        /// <summary>
        /// Gets the schema and e-graph.
        /// </summary>
        public ColorSchema Schema { get; }

        /// <summary>
        /// Gets whether children inherit watermarks.
        /// </summary>
        public bool Inherit { get; }

        /// <summary>
        /// Gets or sets the program's rules, by index.
        /// </summary>
        public EGraphRuleId[] Rules { get; set; } = [];

        /// <summary>
        /// Gets or sets the program's queries, by index.
        /// </summary>
        public EGraphRuleId[] Queries { get; set; } = [];

        /// <summary>
        /// Gets the contexts by slot; <c>null</c> for a slot never filled or disposed.
        /// </summary>
        public List<EGraphContext?> Contexts { get; } = new();

        /// <summary>
        /// Gets the ids by slot; -1 for a slot never filled.
        /// </summary>
        public List<long> Ids { get; } = new();

        /// <summary>
        /// Gets the options a child is created with.
        /// </summary>
        public ContextOptions ChildOptions => Inherit ? ContextOptions.Default : new ContextOptions { InheritWatermarks = false };

        /// <summary>
        /// Gets the rule ids of mask <paramref name="mask"/>, in rule order.
        /// </summary>
        public EGraphRuleId[] Select(int mask)
        {
            var list = new List<EGraphRuleId>();
            for (var i = 0; i < Rules.Length; i++)
                if ((mask & (1 << i)) != 0)
                    list.Add(Rules[i]);

            return list.ToArray();
        }

    }

}
