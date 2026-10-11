using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Some columns of one atom: egglog's <c>SubAtom</c> (core-relations/src/free_join/mod.rs:106-119). In a
    /// <see cref="VarInfo"/> it lists every column of the atom holding the variable (several for a repeated variable);
    /// in a plan it names the columns a stage scans, indexes or probes.
    /// </summary>
    /// <remarks>
    /// Immutable: planning builds a new instance instead of appending in place, so instances are shared freely between
    /// queries, bags and plan templates.
    /// </remarks>
    internal sealed class SubAtom : IEquatable<SubAtom>
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <param name="columns">The columns, in order; the array is owned by the new instance.</param>
        public SubAtom(AtomId atom, ColumnId[] columns)
        {
            ArgumentNullException.ThrowIfNull(columns);
            Atom = atom;
            Columns = columns;
        }

        /// <summary>
        /// Gets the atom.
        /// </summary>
        public AtomId Atom { get; }

        /// <summary>
        /// Gets the columns, in order. Callers must not modify the array.
        /// </summary>
        public ColumnId[] Columns { get; }

        /// <summary>
        /// Creates a sub-atom from the given columns, copying them.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <param name="columns">The columns.</param>
        /// <returns>The sub-atom.</returns>
        public static SubAtom Create(AtomId atom, ReadOnlySpan<ColumnId> columns) => new(atom, columns.ToArray());

        /// <summary>
        /// Creates a sub-atom holding this one's columns followed by <paramref name="more"/>.
        /// </summary>
        /// <param name="more">The columns to append.</param>
        /// <returns>The extended sub-atom.</returns>
        public SubAtom Append(ReadOnlySpan<ColumnId> more)
        {
            var columns = new ColumnId[Columns.Length + more.Length];
            Columns.CopyTo(columns, 0);
            more.CopyTo(columns.AsSpan(Columns.Length));
            return new SubAtom(Atom, columns);
        }

        /// <inheritdoc />
        public bool Equals(SubAtom? other) => other is not null && Atom == other.Atom && Columns.AsSpan().SequenceEqual(other.Columns);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as SubAtom);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Atom);
            foreach (var column in Columns)
                hash.Add(column);

            return hash.ToHashCode();
        }

        /// <summary>
        /// Formats as <c>a1[c0,c1]</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(Atom).Append('[');
            for (var i = 0; i < Columns.Length; i++)
            {
                if (i > 0)
                    b.Append(',');
                b.Append(Columns[i]);
            }

            return b.Append(']').ToString();
        }

    }

}
