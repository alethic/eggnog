using System;
using System.Collections.Generic;

namespace Eggnog.Storage
{

    /// <summary>
    /// The rows one context holds for one table: a flat buffer, a hash index on the key, an index from each
    /// identifier to the rows that mention it, and on demand an index per column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row is never moved. Replacing one (because its key changed) kills the old slot and appends a new one,
    /// so a slot number is a stable name for a row's state at one moment; the indexes may hold slots that have
    /// since died or changed, and every reader rechecks the slot rather than trusting the index.
    /// </para>
    /// <para>
    /// Reading is safe from many threads at once provided nothing writes, which is what a frozen context
    /// guarantees: the key probe lives in a thread-static buffer, and every index is built before freezing.
    /// </para>
    /// </remarks>
    internal sealed class RowSet
    {

        [ThreadStatic]
        static ulong[]? probe;

        static long compactions;

        /// <summary>
        /// Gets how many compactions have run in this process, so tests can tell they are exercising one.
        /// </summary>
        internal static long Compactions => System.Threading.Interlocked.Read(ref compactions);

        readonly int arity;
        readonly int keyCount;
        readonly bool[] isId;
        readonly HashSet<int> keys;
        readonly Dictionary<ulong, List<int>>? uses;
        Dictionary<ulong, List<int>>?[]? columns;

        ulong[] data;
        long[] stamps;
        bool[] alive;
        int count;

        public RowSet(Table table)
        {
            arity = table.Arity;
            keyCount = table.KeyCount;
            isId = table.IsId;
            keys = new HashSet<int>(new RowKeyComparer(this));
            uses = table.IdColumns.Length > 0 ? new Dictionary<ulong, List<int>>() : null;

            const int capacity = 8;
            data = new ulong[capacity * arity];
            stamps = new long[capacity];
            alive = new bool[capacity];
        }

        /// <summary>
        /// Gets the number of slots, live or dead.
        /// </summary>
        public int Count => count;

        public bool IsAlive(int slot) => alive[slot];

        public long StampOf(int slot) => stamps[slot];

        public ReadOnlySpan<ulong> Row(int slot) => new(data, slot * arity, arity);

        public ulong Output(int slot) => data[slot * arity + keyCount];

        /// <summary>
        /// Finds the live row with the given key; the span may be a whole row, of which only the key is read.
        /// </summary>
        /// <returns>The slot, or -1.</returns>
        public int Find(ReadOnlySpan<ulong> key)
        {
            var buffer = probe;
            if (buffer is null || buffer.Length < keyCount)
                probe = buffer = new ulong[Math.Max(keyCount, 8)];

            key.Slice(0, keyCount).CopyTo(buffer);
            return keys.TryGetValue(-1, out var slot) ? slot : -1;
        }

        /// <summary>
        /// Appends a row whose key no live row has.
        /// </summary>
        /// <returns>The slot.</returns>
        public int Add(ReadOnlySpan<ulong> row, long stamp)
        {
            if (count == alive.Length)
                Grow();

            var slot = count;
            row.CopyTo(new Span<ulong>(data, slot * arity, arity));
            stamps[slot] = stamp;
            alive[slot] = true;

            if (keys.Add(slot) == false)
            {
                alive[slot] = false;
                throw new InvalidOperationException("A live row already has this key.");
            }

            count++;

            for (var column = 0; column < arity; column++)
                Index(slot, column, row[column]);

            return slot;
        }

        /// <summary>
        /// Removes a row. Its slot stays, dead, so that slot numbers already handed out remain valid names.
        /// </summary>
        public void Kill(int slot)
        {
            keys.Remove(slot);
            alive[slot] = false;
        }

        /// <summary>
        /// Replaces a row's output. The key is untouched, so the key index needs nothing.
        /// </summary>
        public void SetOutput(int slot, ulong value, long stamp)
        {
            data[slot * arity + keyCount] = value;
            stamps[slot] = stamp;
            Index(slot, keyCount, value);
        }

        /// <summary>
        /// Gets the slots that mentioned <paramref name="id"/> in some identifier column when they were written.
        /// Some may since have died.
        /// </summary>
        public List<int>? Uses(ulong id) => uses is not null && uses.TryGetValue(id, out var list) ? list : null;

        /// <summary>
        /// Drops the use list of an identifier no live row can mention any more.
        /// </summary>
        public void ForgetUses(ulong id) => uses?.Remove(id);

        /// <summary>
        /// Gets the slots whose <paramref name="column"/> held <paramref name="value"/> when it was written. Some may
        /// since have died or changed.
        /// </summary>
        public List<int>? Column(int column, ulong value)
        {
            EnsureColumn(column);
            return columns![column]!.TryGetValue(value, out var list) ? list : null;
        }

        /// <summary>
        /// Builds every column index, so that nothing is built lazily once readers may be concurrent.
        /// </summary>
        public void BuildAllColumns()
        {
            for (var column = 0; column < arity; column++)
                EnsureColumn(column);
        }

        /// <summary>
        /// Gets whether at least half the slots are dead, so that compacting would at least halve the table.
        /// </summary>
        public bool ShouldCompact => count >= 16 && keys.Count * 2 <= count;

        /// <summary>
        /// Drops dead slots, renumbering the live ones in their existing order, and rebuilds every index. Slot
        /// numbers handed out before are meaningless afterwards.
        /// </summary>
        public void Compact()
        {
            System.Threading.Interlocked.Increment(ref compactions);
            var live = keys.Count;
            var capacity = Math.Max(8, live * 2);
            var nextData = new ulong[capacity * arity];
            var nextStamps = new long[capacity];
            var nextAlive = new bool[capacity];

            var next = 0;
            for (var slot = 0; slot < count; slot++)
            {
                if (alive[slot] == false)
                    continue;

                Array.Copy(data, slot * arity, nextData, next * arity, arity);
                nextStamps[next] = stamps[slot];
                nextAlive[next] = true;
                next++;
            }

            data = nextData;
            stamps = nextStamps;
            alive = nextAlive;
            count = next;

            keys.Clear();
            uses?.Clear();
            var built = columns;
            columns = null;

            for (var slot = 0; slot < count; slot++)
            {
                keys.Add(slot);
                for (var column = 0; column < arity; column++)
                    Index(slot, column, data[slot * arity + column]);
            }

            if (built is not null)
                for (var column = 0; column < arity; column++)
                    if (built[column] is not null)
                        EnsureColumn(column);
        }

        void EnsureColumn(int column)
        {
            columns ??= new Dictionary<ulong, List<int>>?[arity];
            if (columns[column] is not null)
                return;

            var index = new Dictionary<ulong, List<int>>();
            for (var slot = 0; slot < count; slot++)
                if (alive[slot])
                    Append(index, data[slot * arity + column], slot);

            columns[column] = index;
        }

        void Index(int slot, int column, ulong value)
        {
            if (uses is not null && isId[column])
                Append(uses, value, slot);

            if (columns?[column] is { } index)
                Append(index, value, slot);
        }

        static void Append(Dictionary<ulong, List<int>> index, ulong value, int slot)
        {
            if (index.TryGetValue(value, out var list) == false)
                index[value] = list = new List<int>(2);

            if (list.Count == 0 || list[list.Count - 1] != slot)
                list.Add(slot);
        }

        void Grow()
        {
            var capacity = alive.Length * 2;
            Array.Resize(ref data, capacity * arity);
            Array.Resize(ref stamps, capacity);
            Array.Resize(ref alive, capacity);
        }

        /// <summary>
        /// Gets a slot's key. Slot -1 stands for the thread's probe buffer, which is how a key that is not in the
        /// buffer is looked up without allocating.
        /// </summary>
        internal ReadOnlySpan<ulong> KeyOf(int slot) => slot < 0
            ? new ReadOnlySpan<ulong>(probe!, 0, keyCount)
            : new ReadOnlySpan<ulong>(data, slot * arity, keyCount);

        internal static int Hash(ReadOnlySpan<ulong> key)
        {
            var h = 0x9E3779B97F4A7C15UL;
            foreach (var value in key)
            {
                h = (h ^ value) * 0xBF58476D1CE4E5B9UL;
                h ^= h >> 31;
            }

            return (int)(h ^ (h >> 32));
        }

    }

}
