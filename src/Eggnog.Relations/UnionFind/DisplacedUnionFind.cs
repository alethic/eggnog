using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A union-find that logs every identifier it displaces: the storage half of egglog's <c>DisplacedTable</c>
    /// (<c>core-relations/src/uf/mod.rs</c>), independent of the table machinery that will wrap it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each successful union appends one row <c>(child, timestamp)</c>, where <c>child</c> is the root that stopped
    /// being canonical. The table built on this exposes row <c>r</c> as
    /// <c>[child, FindNaive(child), timestamp]</c>: the canonical column is never stored, so it is always current
    /// (egglog's "displaced" encoding, a version of the "leader" encoding that only records ids when they cease to
    /// be canonical).
    /// </para>
    /// <para>
    /// Invariants: an identifier is displaced at most once, so there is at most one row per child and the lookup
    /// from child to row is a function; rows are in non-decreasing timestamp order, which
    /// <see cref="TimestampBounds"/> relies on; the row count only grows until <see cref="Clear"/>.
    /// </para>
    /// <para>
    /// Threading: the read-only members (<see cref="FindNaive"/>, <see cref="TryGetRow"/>,
    /// <see cref="TimestampBounds"/>, the row accessors and the <see cref="UnionFind"/> view) never write and may run
    /// on many threads at once while no thread mutates. <see cref="Union"/>, <see cref="Find"/> and <see cref="Clear"/> need exclusive access, as
    /// egglog's <c>&amp;mut self</c> merge does.
    /// </para>
    /// <para>
    /// Layout: rows are kept as two parallel arrays (structure of arrays) so the timestamp binary search touches
    /// only timestamps. The child-to-row lookup is a dense array indexed by identifier, where egglog uses a hash
    /// map: every child is a reserved union-find identifier, so the array (grown geometrically to cover the largest
    /// child) stays within a small factor of the union-find's own parent array, and a lookup is one load with no
    /// hashing.
    /// </para>
    /// </remarks>
    internal sealed class DisplacedUnionFind
    {

        /// <summary>
        /// The underlying union-find.
        /// </summary>
        readonly UnionFind uf;

        /// <summary>
        /// The displaced identifier of each row, in row order.
        /// </summary>
        uint[] children;

        /// <summary>
        /// The timestamp of each row, non-decreasing in row order.
        /// </summary>
        uint[] timestamps;

        /// <summary>
        /// The number of rows.
        /// </summary>
        int count;

        /// <summary>
        /// One more than the row that displaced each identifier, indexed by identifier; zero when the identifier was
        /// never displaced (so growing with zeroed memory needs no fill).
        /// </summary>
        int[] rowOf;

        /// <summary>
        /// Initializes an empty instance: every identifier is its own class and no row exists. Constant time; allocates
        /// nothing beyond the instance and its empty union-find.
        /// </summary>
        public DisplacedUnionFind()
        {
            uf = new UnionFind();
            children = Array.Empty<uint>();
            timestamps = Array.Empty<uint>();
            rowOf = Array.Empty<int>();
        }

        /// <summary>
        /// Initializes an instance over copied state.
        /// </summary>
        /// <param name="uf">The union-find, owned by the new instance.</param>
        /// <param name="children">The row children, owned by the new instance.</param>
        /// <param name="timestamps">The row timestamps, owned by the new instance.</param>
        /// <param name="count">The number of rows.</param>
        /// <param name="rowOf">The child-to-row lookup, owned by the new instance.</param>
        DisplacedUnionFind(UnionFind uf, uint[] children, uint[] timestamps, int count, int[] rowOf)
        {
            this.uf = uf;
            this.children = children;
            this.timestamps = timestamps;
            this.count = count;
            this.rowOf = rowOf;
        }

        /// <summary>
        /// Gets a read-only view of the underlying union-find (egglog's <c>underlying_uf</c>,
        /// <c>uf/mod.rs:463-465</c>, which returns <c>&amp;UnionFind</c>). Constant time; the view allocates nothing.
        /// </summary>
        /// <remarks>
        /// The view cannot union, reserve or reset, so every displaced identifier keeps exactly one row: a caller
        /// holding the mutable union-find could link two identifiers without appending one, and incremental rebuild
        /// finds changed identifiers through the rows.
        /// </remarks>
        public ReadOnlyUnionFind UnionFind => new(uf);

        /// <summary>
        /// Gets the number of rows: the number of successful unions since construction or the last
        /// <see cref="Clear"/>. Rows are only appended, so a consumer that remembers a count can find every row added
        /// since as the range from it to the current count. Constant time.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets the displaced identifier of every row, in row order. Valid until the next mutating call. Constant
        /// time; the span aliases internal storage.
        /// </summary>
        public ReadOnlySpan<uint> Children => new(children, 0, count);

        /// <summary>
        /// Gets the timestamp of every row, in row order (non-decreasing). Valid until the next mutating call.
        /// Constant time; the span aliases internal storage.
        /// </summary>
        public ReadOnlySpan<uint> Timestamps => new(timestamps, 0, count);

        /// <summary>
        /// Finds the canonical identifier of <paramref name="id"/>, reserving it and halving the path it walks
        /// (<see cref="Relations.UnionFind.Find"/>). Needs exclusive access. O(path length).
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <returns>The canonical identifier.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Find(uint id) => uf.Find(id);

        /// <summary>
        /// Finds the canonical identifier of <paramref name="id"/> without writing. Safe from many threads while no
        /// thread mutates. An identifier never seen is its own canonical identifier. O(path length).
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <returns>The canonical identifier.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint FindNaive(uint id) => uf.FindNaive(id);

        /// <summary>
        /// Unions the classes of <paramref name="left"/> and <paramref name="right"/> at <paramref name="timestamp"/>,
        /// appending a row for the displaced root if they were distinct (egglog's <c>insert_impl</c>,
        /// <c>uf/mod.rs:489-510</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Which root is displaced is decided here, not by the argument order: the larger root loses. The two finds
        /// that test equivalence and the two inside <see cref="Relations.UnionFind.Union"/> reserve both identifiers
        /// and halve paths exactly as egglog's do. egglog follows with <c>find(parent)</c> and <c>find(child)</c> "to
        /// compress paths somewhat" (<c>uf/mod.rs:496-498</c>), but right after a union the parent is a root and the
        /// child points straight at it, so those calls write nothing and are left out.
        /// </para>
        /// <para>
        /// A redundant union neither appends nor checks the timestamp, as in egglog, and still runs egglog's two
        /// finds. egglog asserts timestamp order only after its finds and the link (<c>uf/mod.rs:491-505</c>), so a
        /// rejected union has already reserved identifiers, halved paths and linked the roots when it panics. Here a
        /// timestamp below the last row's is caught first and decided with <see cref="Relations.UnionFind.FindNaive"/>,
        /// which writes nothing: a rejected union leaves the union-find (parents and <c>IdCount</c>) and the rows
        /// exactly as they were.
        /// </para>
        /// <para>
        /// Cost: four finds, O(path length) each, plus amortized constant time to append the row. The order check
        /// costs one comparison unless the timestamp goes backwards, which then adds two naive finds.
        /// </para>
        /// </remarks>
        /// <param name="left">An identifier.</param>
        /// <param name="right">Another identifier.</param>
        /// <param name="timestamp">The timestamp of the union; no smaller than the last row's.</param>
        /// <param name="parent">The canonical identifier of the merged class.</param>
        /// <param name="child">The displaced root; equal to <paramref name="parent"/> when nothing changed.</param>
        /// <returns><c>true</c> if the classes were distinct and a row was appended.</returns>
        /// <exception cref="InvalidOperationException">
        /// The classes were distinct and <paramref name="timestamp"/> is smaller than the last row's. Nothing changed.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// An identifier exceeds <see cref="Relations.UnionFind.MaxId"/>.
        /// </exception>
        public bool Union(uint left, uint right, uint timestamp, out uint parent, out uint child)
        {
            // A timestamp going backwards is legal only for a redundant union. Decide that without writing, so the
            // rejection below happens before egglog's mutating finds rather than after the link (uf/mod.rs:491-505).
            if (count > 0 && timestamps[count - 1] > timestamp && uf.FindNaive(left) != uf.FindNaive(right))
                ThrowDecreasingTimestamp(timestamp, timestamps[count - 1]);

            var l = uf.Find(left);
            var r = uf.Find(right);
            if (l == r)
            {
                parent = l;
                child = l;
                return false;
            }

            (parent, child) = uf.Union(left, right);
            Debug.Assert(parent == Math.Min(l, r) && child == Math.Max(l, r));
            Append(child, timestamp);
            return true;
        }

        /// <summary>
        /// Gets the row that displaced <paramref name="id"/>, if any. Roots and identifiers never seen have none.
        /// Constant time.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <param name="row">The row, or -1.</param>
        /// <returns><c>true</c> if <paramref name="id"/> was displaced.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetRow(uint id, out int row)
        {
            var map = rowOf;
            if (id < (uint)map.Length)
            {
                row = map[id] - 1;
                return row >= 0;
            }

            row = -1;
            return false;
        }

        /// <summary>
        /// Gets the displaced identifier of <paramref name="row"/>. Constant time.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <returns>The identifier that the row's union displaced.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is not below <see cref="Count"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetChild(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return children[row];
        }

        /// <summary>
        /// Gets the current canonical identifier of <paramref name="row"/>'s displaced identifier. O(path length).
        /// </summary>
        /// <param name="row">The row.</param>
        /// <returns>The canonical identifier.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is not below <see cref="Count"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetCanonical(int row) => uf.FindNaive(GetChild(row));

        /// <summary>
        /// Gets the timestamp of <paramref name="row"/>. Constant time.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <returns>The timestamp of the union that appended it.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is not below <see cref="Count"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetTimestamp(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return timestamps[row];
        }

        /// <summary>
        /// Gets the three logical columns of <paramref name="row"/>: displaced identifier, its current canonical
        /// identifier, and timestamp (egglog's <c>expand</c>, <c>uf/mod.rs:466-469</c>). O(path length).
        /// </summary>
        /// <param name="row">The row.</param>
        /// <param name="child">The displaced identifier.</param>
        /// <param name="canonical">Its canonical identifier.</param>
        /// <param name="timestamp">The timestamp.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is not below <see cref="Count"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Expand(int row, out uint child, out uint canonical, out uint timestamp)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            child = children[row];
            canonical = uf.FindNaive(child);
            timestamp = timestamps[row];
        }

        /// <summary>
        /// Finds the rows whose timestamp equals <paramref name="timestamp"/> (egglog's <c>timestamp_bounds</c>,
        /// <c>uf/mod.rs:470-484</c>). O(log <see cref="Count"/>).
        /// </summary>
        /// <remarks>
        /// egglog binary-searches for any matching row and then walks outward over the run of equal timestamps, which
        /// is linear in the run; every union of one rule run shares a timestamp, so runs are long. Two binary searches
        /// return the same bounds in logarithmic time.
        /// </remarks>
        /// <param name="timestamp">The timestamp.</param>
        /// <param name="start">
        /// The first row with that timestamp; when there is none, the row where one would be inserted, which is
        /// also the number of rows with a smaller timestamp.
        /// </param>
        /// <param name="end">
        /// One past the last row with that timestamp; when there is none, equal to <paramref name="start"/>.
        /// </param>
        /// <returns><c>true</c> if some row has that timestamp.</returns>
        public bool TimestampBounds(uint timestamp, out int start, out int end)
        {
            var ts = timestamps;
            var n = count;
            start = Search(ts, 0, n, timestamp, false);
            if (start == n || ts[start] != timestamp)
            {
                end = start;
                return false;
            }

            end = Search(ts, start + 1, n, timestamp, true);
            return true;
        }

        /// <summary>
        /// Removes every row and makes every identifier its own class again, keeping allocated storage. Linear in
        /// the union-find's reserved identifiers.
        /// </summary>
        /// <remarks>
        /// egglog's <c>clear</c> (<c>uf/mod.rs:286-289</c>) resets the union-find and the rows but leaves the
        /// child-to-row lookup in place, so a later lookup can return a row that no longer exists. This clears the
        /// lookup too. Bumping the table's major version on clear is left to the table that wraps this.
        /// </remarks>
        public void Clear()
        {
            uf.Reset();
            var map = rowOf;
            for (var i = 0; i < count; i++)
                map[children[i]] = 0;

            count = 0;
        }

        /// <summary>
        /// Creates an independent deep copy (egglog's <c>Clone for DisplacedTable</c> minus the pending-write queue,
        /// which belongs to the table). Linear in the union-find's size plus the row count.
        /// </summary>
        /// <returns>The copy.</returns>
        public DisplacedUnionFind Clone()
        {
            return new DisplacedUnionFind(
                uf.Clone(),
                children.AsSpan(0, count).ToArray(),
                timestamps.AsSpan(0, count).ToArray(),
                count,
                (int[])rowOf.Clone());
        }

        /// <summary>
        /// Appends the row <c>(child, timestamp)</c> and records it in the lookup.
        /// </summary>
        /// <param name="child">The displaced identifier.</param>
        /// <param name="timestamp">The timestamp.</param>
        void Append(uint child, uint timestamp)
        {
            if (count == children.Length)
                GrowRows();

            if (child >= (uint)rowOf.Length)
                GrowLookup(child);

            Debug.Assert(rowOf[child] == 0, "An identifier is displaced at most once.");
            children[count] = child;
            timestamps[count] = timestamp;
            rowOf[child] = count + 1;
            count++;
        }

        /// <summary>
        /// Doubles the row arrays.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void GrowRows()
        {
            var capacity = (int)Math.Min(Array.MaxLength, Math.Max(16L, children.Length * 2L));
            if (capacity == count)
                throw new InvalidOperationException("The displaced log is full.");

            Array.Resize(ref children, capacity);
            Array.Resize(ref timestamps, capacity);
        }

        /// <summary>
        /// Grows the lookup to cover <paramref name="id"/>; new slots read as "not displaced".
        /// </summary>
        /// <param name="id">The identifier to cover.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void GrowLookup(uint id)
        {
            var capacity = (int)Math.Min(Array.MaxLength, Math.Max(id + 1L, Math.Max(16L, rowOf.Length * 2L)));
            Array.Resize(ref rowOf, capacity);
        }

        /// <summary>
        /// Gets the first index in [<paramref name="from"/>, <paramref name="to"/>) whose timestamp is at least
        /// (<paramref name="strict"/> false) or greater than (<paramref name="strict"/> true)
        /// <paramref name="timestamp"/>, or <paramref name="to"/> if none is.
        /// </summary>
        /// <param name="ts">The sorted timestamps.</param>
        /// <param name="from">The first index to consider.</param>
        /// <param name="to">One past the last index to consider.</param>
        /// <param name="timestamp">The timestamp sought.</param>
        /// <param name="strict">Whether to skip timestamps equal to <paramref name="timestamp"/>.</param>
        /// <returns>The index.</returns>
        static int Search(uint[] ts, int from, int to, uint timestamp, bool strict)
        {
            var lo = from;
            var n = to - from;
            while (n > 0)
            {
                var half = n >> 1;
                var t = ts[lo + half];
                if (t < timestamp || (strict && t == timestamp))
                {
                    lo += half + 1;
                    n -= half + 1;
                }
                else
                {
                    n = half;
                }
            }

            return lo;
        }

        /// <summary>
        /// Throws for a union whose timestamp precedes the last row's.
        /// </summary>
        /// <param name="timestamp">The union's timestamp.</param>
        /// <param name="last">The last row's timestamp.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowDecreasingTimestamp(uint timestamp, uint last)
        {
            throw new InvalidOperationException(
                $"Union at timestamp {timestamp} follows a union at timestamp {last}: timestamps must not decrease.");
        }

        /// <summary>
        /// Throws for a row beyond <see cref="Count"/>.
        /// </summary>
        /// <param name="row">The row.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowRowOutOfRange(int row)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, "No such row.");
        }

    }

}
