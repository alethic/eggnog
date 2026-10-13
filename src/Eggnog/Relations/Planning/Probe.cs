using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// One entry of a stage's <c>to_intersect</c> list: an index over some columns of an atom, and how to build the
    /// probe key from the tuple the stage's cover produces. egglog's <c>(ScanSpec, SmallVec&lt;[ColumnId; 2]&gt;)</c>
    /// pair (core-relations/src/free_join/plan.rs:145-156).
    /// </summary>
    /// <remarks>
    /// <see cref="Key"/> has one entry per column of <see cref="Scan"/>: entry <c>k</c> is the position, in the cover's
    /// projected tuple, of the value to look up in column <c>Scan.ToIndex.Columns[k]</c>. For a
    /// <see cref="FusedIntersectStage"/> the projected tuple is the cover's columns in order; for a
    /// <see cref="MatScanMode.KeyOnly"/> materialization scan it is the materialization key (the message variables).
    /// A repeated variable gives a repeated position, as in <c>key[0,0]</c>.
    /// </remarks>
    internal sealed class Probe
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scan">The atom, the columns to index, and the slow constraints.</param>
        /// <param name="key">The key positions, one per indexed column; owned by the new instance.</param>
        public Probe(ScanSpec scan, ColumnId[] key)
        {
            ArgumentNullException.ThrowIfNull(scan);
            ArgumentNullException.ThrowIfNull(key);
            if (key.Length != scan.ToIndex.Columns.Length)
                throw new ArgumentException("A probe needs one key position per indexed column.", nameof(key));

            Scan = scan;
            Key = key;
        }

        /// <summary>
        /// Gets the atom, the columns to index, and the slow constraints.
        /// </summary>
        public ScanSpec Scan { get; }

        /// <summary>
        /// Gets the key positions into the cover's projected tuple. Callers must not modify the array.
        /// </summary>
        public ColumnId[] Key { get; }

        /// <summary>
        /// Formats as <c>a0[c1] key[0]</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(Scan).Append(" key[");
            for (var i = 0; i < Key.Length; i++)
            {
                if (i > 0)
                    b.Append(',');
                b.Append(Key[i].Index);
            }

            return b.Append(']').ToString();
        }

    }

}
