using System.Collections.Generic;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Shared text formatting for plan types. The format is compact and stable, because golden tests compare stage
    /// lists as text; it carries the same information as egglog's <c>Debug</c> output.
    /// </summary>
    internal static class PlanText
    {

        /// <summary>
        /// Appends <c>{c1 == c0, c1 &gt; 2}</c>, or nothing when there are no constraints.
        /// </summary>
        /// <param name="b">The builder.</param>
        /// <param name="constraints">The constraints.</param>
        public static void AppendConstraints(StringBuilder b, Constraint[] constraints)
        {
            if (constraints.Length == 0)
                return;

            b.Append('{');
            for (var i = 0; i < constraints.Length; i++)
            {
                if (i > 0)
                    b.Append(", ");
                b.Append(constraints[i]);
            }

            b.Append('}');
        }

        /// <summary>
        /// Appends the items separated by <paramref name="separator"/>.
        /// </summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="b">The builder.</param>
        /// <param name="items">The items.</param>
        /// <param name="separator">The separator.</param>
        public static void AppendJoined<T>(StringBuilder b, IReadOnlyList<T> items, string separator)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    b.Append(separator);
                b.Append(items[i]);
            }
        }

        /// <summary>
        /// Appends <c>bind[c0-&gt;v1, c1-&gt;v2]</c>, preceded by a space, or nothing when there are no bindings.
        /// </summary>
        /// <param name="b">The builder.</param>
        /// <param name="bind">The bindings.</param>
        public static void AppendBind(StringBuilder b, StageBinding[] bind)
        {
            if (bind.Length == 0)
                return;

            b.Append(" bind[");
            AppendJoined(b, bind, ", ");
            b.Append(']');
        }

        /// <summary>
        /// Appends <c>probe[a0[c1] key[0], ...]</c>, preceded by a space, or nothing when there are no probes.
        /// </summary>
        /// <param name="b">The builder.</param>
        /// <param name="probes">The probes.</param>
        public static void AppendProbes(StringBuilder b, Probe[] probes)
        {
            if (probes.Length == 0)
                return;

            b.Append(" probe[");
            AppendJoined(b, probes, ", ");
            b.Append(']');
        }

    }

}
