using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The ids of containers the root rebuilt in place while it had live descendants (colored-contexts spec §7.4, C13
    /// interim, X31): the store-wide log from which each child re-stamps the rows of its own layers that mention such a
    /// container, so its semi-naive evaluation sees them as new, as egglog's <c>refresh_rows_for_values</c> does at the
    /// root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root's rebuild pass appends (<see cref="Database.RebuildPass"/>); each child reads what was appended since its
    /// own cursor (<see cref="Since"/>) at the first pass of its rebuild. Root rows are re-stamped by the root itself and
    /// reach children as appends. <see cref="Context.Dispose"/> clears the log when the root's gate reaches zero: no
    /// cursor into it is left then.
    /// </para>
    /// <para>
    /// Thread-safe under a lock: the root appends only while it is not frozen, when every context shares its thread
    /// domain; children of a frozen root read concurrently while a disposal may clear it. A cursor beyond the end (taken
    /// before a clear that raced a creation under a frozen root, when nothing more is ever appended) reads nothing.
    /// </para>
    /// </remarks>
    internal sealed class ContainerDirtyLog
    {

        readonly object sync = new();
        uint[] ids = Array.Empty<uint>();
        int count;

        /// <summary>
        /// Gets the number of ids logged: the cursor a context created now starts at.
        /// </summary>
        public int End
        {
            get
            {
                lock (sync)
                    return count;
            }
        }

        /// <summary>
        /// Appends <paramref name="dirty"/>. O(ids) amortized.
        /// </summary>
        /// <param name="dirty">The ids of containers rebuilt in place.</param>
        public void Append(ReadOnlySpan<uint> dirty)
        {
            if (dirty.IsEmpty)
                return;

            lock (sync)
            {
                if (count + dirty.Length > ids.Length)
                    Array.Resize(ref ids, Math.Max(count + dirty.Length, Math.Max(16, ids.Length * 2)));

                dirty.CopyTo(ids.AsSpan(count));
                count += dirty.Length;
            }
        }

        /// <summary>
        /// Returns the ids appended since <paramref name="cursor"/> and moves the cursor to the end.
        /// </summary>
        /// <param name="cursor">The reader's cursor.</param>
        /// <returns>A copy of the new ids; empty when there are none.</returns>
        public uint[] Since(ref int cursor)
        {
            lock (sync)
            {
                if (cursor >= count)
                {
                    cursor = count;
                    return Array.Empty<uint>();
                }

                var result = ids.AsSpan(cursor, count - cursor).ToArray();
                cursor = count;
                return result;
            }
        }

        /// <summary>
        /// Empties the log: the root has no live descendant, so no cursor into it remains.
        /// </summary>
        public void Clear()
        {
            lock (sync)
            {
                count = 0;
                ids = Array.Empty<uint>();
            }
        }

    }

}
