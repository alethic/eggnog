using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Computes <b>K</b>, the executed constants of a rule in a context (colored-contexts spec §9.8, X19, I30): every Id
    /// constant of the rule's query (<see cref="IdConstant"/>) and actions (<see cref="ActionIdConstant"/>), in that
    /// order, in its canonical form in the context that runs it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A store-backed context, the root included, canonicalizes through its view
    /// (<see cref="ContextView.TryCanonicalize"/>); a bare database has no designated union-find and runs the constants
    /// as written. The written vector (<see cref="Written"/>) is returned itself, not a copy, whenever nothing changes, so
    /// a rule whose constants are all canonical costs O(constants) finds and allocates nothing per run (§14.1).
    /// </para>
    /// <para>
    /// Defect fixed (proposed map §8.1 row 25; egglog-bridge/src/lib.rs:1397-1420): egglog plans a rule once and bakes
    /// its Id constants into the cached plan, so after a constant loses a union the rule silently stops matching. Here
    /// every run substitutes K (<see cref="CachedPlan.GetVariantFor"/>), at a store-backed root as in a child.
    /// </para>
    /// <para>
    /// Cold path: once per rule per rule-set build.
    /// </para>
    /// </remarks>
    internal static class CanonicalConstants
    {

        /// <summary>
        /// Gets the written values of a rule's constant ids, query constants first, or <c>null</c> when it has none.
        /// </summary>
        /// <param name="query">The query's constant ids.</param>
        /// <param name="action">The actions' constant ids.</param>
        /// <returns>The values, or <c>null</c>.</returns>
        public static uint[]? Written(IdConstant[] query, ActionIdConstant[] action)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(action);
            var count = query.Length + action.Length;
            if (count == 0)
                return null;

            var values = new uint[count];
            for (var i = 0; i < query.Length; i++)
                values[i] = query[i].Value.Rep;

            for (var i = 0; i < action.Length; i++)
                values[query.Length + i] = action[i].Value.Rep;

            return values;
        }

        /// <summary>
        /// Computes K for a rule in <paramref name="view"/>'s context: <c>null</c> iff the rule has no constant ids;
        /// otherwise, for each constant in order, its canonical form when the view canonicalizes, else its written value.
        /// </summary>
        /// <param name="view">The context's view.</param>
        /// <param name="query">The query's constant ids.</param>
        /// <param name="action">The actions' constant ids.</param>
        /// <param name="written">The written values (<see cref="Written"/>), returned as they are when every constant is
        /// canonical; computed here when <c>null</c>.</param>
        /// <returns>K: <paramref name="written"/> itself when nothing changes, else a new array.</returns>
        public static uint[]? Compute(ContextView view, IdConstant[] query, ActionIdConstant[] action, uint[]? written = null)
        {
            ArgumentNullException.ThrowIfNull(view);
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(action);
            var count = query.Length + action.Length;
            if (count == 0)
                return null;

            written ??= Written(query, action)!;
            if (written.Length != count)
                throw new ArgumentException("The written values do not match the rule's constants.", nameof(written));

            if (view.CanCanonicalize == false)
                return written;

            uint[]? result = null;
            for (var i = 0; i < count; i++)
            {
                var value = written[i];
                view.TryCanonicalize(value, out var canonical);
                if (canonical == value)
                    continue;

                result ??= (uint[])written.Clone();
                result[i] = canonical;
            }

            return result ?? written;
        }

    }

}
