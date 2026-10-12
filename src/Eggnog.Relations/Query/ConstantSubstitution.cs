using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The Id constants of a rule whose executed form K differs from the written one (colored-contexts spec §9.8): query
    /// entries <c>(atom, column, written, canonical)</c> and action entries <c>(instruction, operand, written,
    /// canonical)</c>. <see cref="TemplateSubstituter"/> and <see cref="ActionSubstituter"/> rewrite exactly these
    /// positions (I30).
    /// </summary>
    /// <remarks>
    /// Immutable. Cold path: built at most once per (rule, distinct K) and cached in the rule's
    /// <see cref="ConstantVariant"/>s.
    /// </remarks>
    internal sealed class ConstantSubstitution
    {

        readonly (AtomId Atom, int Column, uint Written, uint Canonical)[] query;
        readonly (int Instr, int Operand, uint Written, uint Canonical)[] action;

        ConstantSubstitution((AtomId, int, uint, uint)[] query, (int, int, uint, uint)[] action)
        {
            this.query = query;
            this.action = action;
        }

        /// <summary>
        /// Gets the query entries that change: an <see cref="ConstraintKind.EqConst"/> of the atom on the column with the
        /// written constant becomes one with the canonical constant.
        /// </summary>
        public ReadOnlySpan<(AtomId Atom, int Column, uint Written, uint Canonical)> Query => query;

        /// <summary>
        /// Gets the action entries that change: the operand of the instruction becomes the canonical constant.
        /// </summary>
        public ReadOnlySpan<(int Instr, int Operand, uint Written, uint Canonical)> Action => action;

        /// <summary>
        /// Gets whether any action entry changes.
        /// </summary>
        public bool ChangesActions => action.Length > 0;

        /// <summary>
        /// Lists the constants whose canonical form differs from the written one.
        /// </summary>
        /// <param name="queryConstants">The query's constant ids.</param>
        /// <param name="actionConstants">The actions' constant ids.</param>
        /// <param name="canonical">K (<see cref="CanonicalConstants.Compute"/>), or <c>null</c>.</param>
        /// <returns>The substitution, or <c>null</c> when nothing differs.</returns>
        /// <exception cref="ArgumentException">K does not have one value per constant.</exception>
        public static ConstantSubstitution? Create(IdConstant[] queryConstants, ActionIdConstant[] actionConstants, uint[]? canonical)
        {
            ArgumentNullException.ThrowIfNull(queryConstants);
            ArgumentNullException.ThrowIfNull(actionConstants);
            if (canonical is null)
                return null;

            if (canonical.Length != queryConstants.Length + actionConstants.Length)
                throw new ArgumentException($"K has {canonical.Length} values for {queryConstants.Length + actionConstants.Length} constants.", nameof(canonical));

            List<(AtomId, int, uint, uint)>? query = null;
            for (var i = 0; i < queryConstants.Length; i++)
            {
                var c = queryConstants[i];
                if (canonical[i] != c.Value.Rep)
                    (query ??= new()).Add((c.Atom, c.Column.Rep, c.Value.Rep, canonical[i]));
            }

            List<(int, int, uint, uint)>? action = null;
            for (var i = 0; i < actionConstants.Length; i++)
            {
                var c = actionConstants[i];
                var k = canonical[queryConstants.Length + i];
                if (k != c.Value.Rep)
                    (action ??= new()).Add((c.Instr, c.Operand, c.Value.Rep, k));
            }

            if (query is null && action is null)
                return null;

            return new ConstantSubstitution(query?.ToArray() ?? [], action?.ToArray() ?? []);
        }

        /// <summary>
        /// Gets the canonical form of the constant <paramref name="written"/> the atom compares column
        /// <paramref name="column"/> with, if it changes.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <param name="column">The column.</param>
        /// <param name="written">The constant as written.</param>
        /// <param name="canonical">The canonical form.</param>
        /// <returns><c>true</c> if the constant changes.</returns>
        public bool TryGetQuery(AtomId atom, int column, uint written, out uint canonical)
        {
            foreach (var (a, c, w, k) in query)
            {
                if (a == atom && c == column && w == written)
                {
                    canonical = k;
                    return true;
                }
            }

            canonical = written;
            return false;
        }

        /// <summary>
        /// Gets the canonical form of operand <paramref name="operand"/> of instruction <paramref name="instr"/>, if it
        /// changes.
        /// </summary>
        /// <param name="instr">The instruction's index.</param>
        /// <param name="operand">The operand's index.</param>
        /// <param name="canonical">The canonical form.</param>
        /// <returns><c>true</c> if the operand changes.</returns>
        public bool TryGetAction(int instr, int operand, out uint canonical)
        {
            foreach (var (i, o, _, k) in action)
            {
                if (i == instr && o == operand)
                {
                    canonical = k;
                    return true;
                }
            }

            canonical = 0;
            return false;
        }

        /// <summary>
        /// Gets whether any action entry names instruction <paramref name="instr"/>.
        /// </summary>
        /// <param name="instr">The instruction's index.</param>
        /// <returns><c>true</c> if the instruction changes.</returns>
        public bool ChangesInstr(int instr)
        {
            foreach (var (i, _, _, _) in action)
                if (i == instr)
                    return true;

            return false;
        }

    }

}
