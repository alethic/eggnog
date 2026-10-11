using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The ids a child context displaced, in order: the rows of the child's union-find table (colored-contexts spec
    /// §5.3), the child's counterpart of the root's <see cref="DisplacedUnionFind"/> rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Definition (I8): the journal holds exactly the ids x with <c>find_C(x) ≠ x</c> that were not displaced in the
    /// parent's view when the context was created, each once, in nondecreasing timestamp order. They come from three
    /// sources: the losers of the context's own unions; the canonical ids that lost while re-keying after a parent
    /// change; and imports, the parent's displacements of ids that were canonical in the context. Canonicity is lost once
    /// and never regained, so an id is appended at most once.
    /// </para>
    /// <para>
    /// The journal is what makes a child's rebuild incremental: a layer rebuilt at journal row r finds every id that
    /// stopped being canonical since as the suffix from r (the rebuilder's hint column), and a grandchild catches up with
    /// the child's union-find through one cursor into it (X7).
    /// </para>
    /// <para>
    /// Layout: ids and timestamps in parallel arrays (the timestamp binary search touches only timestamps), and an
    /// id-to-row hash map; about 16 bytes per row. Threading: reads are safe from many threads while nothing appends;
    /// <see cref="Append"/> needs exclusive access (the owning context's merge and catch-up).
    /// </para>
    /// </remarks>
    internal sealed class DisplacedJournal
    {

        uint[] ids = Array.Empty<uint>();
        uint[] timestamps = Array.Empty<uint>();
        int count;

        /// <summary>
        /// Every journaled id to its row (<see cref="IdEntry.Payload"/>); <c>null</c> until the first append.
        /// </summary>
        RawHashTable<IdEntry>? rowOf;

        /// <summary>
        /// Gets the number of rows.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets the journaled ids, in row order. Valid until the next append; aliases internal storage.
        /// </summary>
        public ReadOnlySpan<uint> Ids => new(ids, 0, count);

        /// <summary>
        /// Gets the timestamps, in row order (nondecreasing). Valid until the next append; aliases internal storage.
        /// </summary>
        public ReadOnlySpan<uint> Timestamps => new(timestamps, 0, count);

        /// <summary>
        /// Gets the timestamp of the last row, or 0 when the journal is empty.
        /// </summary>
        public uint LastTimestamp => count == 0 ? 0u : timestamps[count - 1];

        /// <summary>
        /// Appends the row <c>(id, timestamp)</c>. Amortized O(1).
        /// </summary>
        /// <param name="id">An id that just stopped being canonical in the owning context, not journaled yet.</param>
        /// <param name="timestamp">The epoch of the union or catch-up; no smaller than <see cref="LastTimestamp"/>.</param>
        /// <exception cref="InvalidOperationException"><paramref name="timestamp"/> is smaller than
        /// <see cref="LastTimestamp"/>. Nothing changed.</exception>
        public void Append(uint id, uint timestamp)
        {
            if (count > 0 && timestamp < timestamps[count - 1])
                ThrowDecreasingTimestamp(timestamp, timestamps[count - 1]);

            Debug.Assert(Contains(id) == false, "An id is journaled at most once.");
            if (count == ids.Length)
            {
                var capacity = (int)Math.Min(Array.MaxLength, Math.Max(16L, ids.Length * 2L));
                if (capacity == count)
                    throw new InvalidOperationException("The displaced journal is full.");

                Array.Resize(ref ids, capacity);
                Array.Resize(ref timestamps, capacity);
            }

            ids[count] = id;
            timestamps[count] = timestamp;
            var table = rowOf ??= new RawHashTable<IdEntry>();
            table.InsertUnique(FxHasher.Hash(id), new IdEntry(id, (uint)count), default(IdEntryHasher));
            count++;
        }

        /// <summary>
        /// Gets whether <paramref name="id"/> is journaled. One hash probe.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if it is.</returns>
        public bool Contains(uint id) => TryGetRow(id, out _);

        /// <summary>
        /// Gets the row of <paramref name="id"/>, if it is journaled. One hash probe.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="row">The row, or -1.</param>
        /// <returns><c>true</c> if it is journaled.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetRow(uint id, out int row)
        {
            var table = rowOf;
            if (table is not null)
            {
                var match = new IdEntryMatch(id);
                if (table.TryFind(FxHasher.Hash(id), ref match, out var entry))
                {
                    row = (int)entry.Payload;
                    return true;
                }
            }

            row = -1;
            return false;
        }

        /// <summary>
        /// Gets the id of <paramref name="row"/>.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <returns>The id.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is not below <see cref="Count"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetId(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return ids[row];
        }

        /// <summary>
        /// Gets the timestamp of <paramref name="row"/>.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <returns>The timestamp.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is not below <see cref="Count"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetTimestamp(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return timestamps[row];
        }

        /// <summary>
        /// Finds the rows whose timestamp equals <paramref name="timestamp"/>, as
        /// <see cref="DisplacedUnionFind.TimestampBounds"/> does for the root's rows. O(log <see cref="Count"/>).
        /// </summary>
        /// <param name="timestamp">The timestamp.</param>
        /// <param name="start">The first row with that timestamp; when there is none, the number of rows with a smaller
        /// one.</param>
        /// <param name="end">One past the last row with that timestamp; when there is none, equal to
        /// <paramref name="start"/>.</param>
        /// <returns><c>true</c> if some row has that timestamp.</returns>
        public bool TimestampBounds(uint timestamp, out int start, out int end)
        {
            var ts = timestamps;
            var n = count;
            start = DisplacedUnionFind.Search(ts, 0, n, timestamp, false);
            if (start == n || ts[start] != timestamp)
            {
                end = start;
                return false;
            }

            end = DisplacedUnionFind.Search(ts, start + 1, n, timestamp, true);
            return true;
        }

        /// <summary>
        /// Drops every row and its storage, when the owning context is disposed.
        /// </summary>
        internal void Release()
        {
            ids = Array.Empty<uint>();
            timestamps = Array.Empty<uint>();
            rowOf = null;
            count = 0;
        }

        /// <summary>
        /// Throws for an append whose timestamp precedes the last row's, with <see cref="DisplacedUnionFind"/>'s message.
        /// </summary>
        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowDecreasingTimestamp(uint timestamp, uint last)
        {
            throw new InvalidOperationException(
                $"Union at timestamp {timestamp} follows a union at timestamp {last}: timestamps must not decrease.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowRowOutOfRange(int row)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, "No such row.");
        }

    }

}
