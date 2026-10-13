using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The tables a lift may write into the cases' parent, and how (colored-contexts spec §11.2; rule-layer §6.9.1, E8).
    /// Immutable; built once and shared by every lift over the same tables.
    /// </summary>
    /// <remarks>
    /// The e-graph's contradiction function is skipped if listed: an all-vacuous lift writes it on its own terms (L4).
    /// A function is listed at most once. The lift checks each table against the e-graph's definitions when it runs.
    /// </remarks>
    internal sealed class LiftSpec
    {

        readonly LiftTable[] tables;

        /// <summary>
        /// Initializes a spec over <paramref name="tables"/>, in lift order.
        /// </summary>
        /// <param name="tables">The tables.</param>
        /// <exception cref="ArgumentException">A function is listed twice, or a kind is not defined.</exception>
        public LiftSpec(IEnumerable<LiftTable> tables)
        {
            ArgumentNullException.ThrowIfNull(tables);
            var list = new List<LiftTable>();
            var seen = new HashSet<FunctionId>();
            foreach (var table in tables)
            {
                if (Enum.IsDefined(table.Kind) == false)
                    throw new ArgumentException($"{table.Kind} is not a lift kind.", nameof(tables));

                if (seen.Add(table.Function) == false)
                    throw new ArgumentException($"Function {table.Function} is listed twice.", nameof(tables));

                list.Add(table);
            }

            this.tables = list.ToArray();
        }

        /// <summary>
        /// Initializes a spec over <paramref name="tables"/>, in lift order.
        /// </summary>
        /// <param name="tables">The tables.</param>
        /// <exception cref="ArgumentException">A function is listed twice, or a kind is not defined.</exception>
        public LiftSpec(params LiftTable[] tables)
            : this((IEnumerable<LiftTable>)tables)
        {

        }

        /// <summary>
        /// Gets an empty spec: a lift then writes equalities only.
        /// </summary>
        public static LiftSpec Empty { get; } = new(Array.Empty<LiftTable>());

        /// <summary>
        /// Gets the tables, in lift order.
        /// </summary>
        public IReadOnlyList<LiftTable> Tables => tables;

        /// <summary>
        /// Gets whether <paramref name="other"/> lists the same tables in the same order, each with the same kind and case
        /// join (delegates compare by target and method). A lift memo keyed on one spec holds for the other
        /// (<see cref="LiftSignature"/>). O(tables).
        /// </summary>
        /// <param name="other">The other spec.</param>
        /// <returns><c>true</c> if the two specs lift alike.</returns>
        public bool HasSameTables(LiftSpec other)
        {
            ArgumentNullException.ThrowIfNull(other);
            return tables.AsSpan().SequenceEqual(other.tables);
        }

    }

}
