using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The result of one block of a decomposed plan: an insertion-ordered map from the values of the block's message
    /// variables (the key) to the rows of its value variables. egglog's
    /// <c>IndexMap&lt;Vec&lt;Value&gt;, RowBuffer&gt;</c> (core-relations/src/free_join/execute.rs:770, 2111-2153).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: keys are stored once each, back to back in one array, in the order they were first added; group
    /// <c>g</c> is the key at <c>[g * KeyArity, (g + 1) * KeyArity)</c> and its rows are <see cref="GetValues"/>(g), one
    /// <see cref="RowBuffer"/> of <see cref="ValueArity"/> cells per group. A SwissTable of (hash, group) pairs finds a
    /// key's group. A block with no value variables stores one stale cell per row, as egglog does, so a key's row count
    /// still counts its matches. Rows are a multiset: the join may produce a match more than once.
    /// </para>
    /// <para>
    /// Iteration is in insertion order, so a serial run is deterministic. Adding costs one hash, one probe and one row
    /// copy; a new key also appends its cells and allocates its row buffer.
    /// </para>
    /// <para>
    /// Not thread-safe; the parallel executor gives each thread its own (<see cref="ConcurrentMaterialization"/>) and
    /// merges them with <see cref="AddAll"/>. Immutable once a block finishes, so any number of threads may read it.
    /// </para>
    /// </remarks>
    internal sealed class Materialization : IDisposable
    {

        readonly int keyArity;
        readonly int valueArity;
        readonly RawHashTable<MaterializationEntry> table = new();
        uint[] keys = Array.Empty<uint>();
        RowBuffer[] groups = Array.Empty<RowBuffer>();
        int count;

        /// <summary>
        /// Initializes an empty materialization.
        /// </summary>
        /// <param name="keyArity">The number of message variables; may be zero.</param>
        /// <param name="valueArity">The number of cells per row: the number of value variables, or one when there are
        /// none.</param>
        public Materialization(int keyArity, int valueArity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(keyArity);
            ArgumentOutOfRangeException.ThrowIfLessThan(valueArity, 1);
            this.keyArity = keyArity;
            this.valueArity = valueArity;
        }

        /// <summary>
        /// Gets the number of cells of a key.
        /// </summary>
        public int KeyArity => keyArity;

        /// <summary>
        /// Gets the number of cells of a row.
        /// </summary>
        public int ValueArity => valueArity;

        /// <summary>
        /// Gets the number of distinct keys. egglog's <c>len</c>, which the executor uses as the stage size estimate.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets whether nothing was added. A block whose materialization is empty ends the rule.
        /// </summary>
        public bool IsEmpty => count == 0;

        /// <summary>
        /// Gets the key store: group <c>g</c>'s key starts at <c>g * KeyArity</c>.
        /// </summary>
        public uint[] KeyData => keys;

        /// <summary>
        /// Gets the key of group <paramref name="group"/>.
        /// </summary>
        /// <param name="group">The group, in <c>[0, Count)</c>.</param>
        /// <returns>The key's cells.</returns>
        public ReadOnlySpan<uint> GetKey(int group)
        {
            if ((uint)group >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(group));

            return new ReadOnlySpan<uint>(keys, group * keyArity, keyArity);
        }

        /// <summary>
        /// Gets the rows of group <paramref name="group"/>, in the order they were added.
        /// </summary>
        /// <param name="group">The group, in <c>[0, Count)</c>.</param>
        /// <returns>The rows; must not be modified.</returns>
        public RowBuffer GetValues(int group)
        {
            if ((uint)group >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(group));

            return groups[group];
        }

        /// <summary>
        /// Finds the group of the key held at <paramref name="probe"/>[<paramref name="offset"/>]. egglog's
        /// <c>IndexMap::get</c>. O(1) expected, no allocation.
        /// </summary>
        /// <param name="probe">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <param name="group">The group, or -1.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        public bool TryFind(uint[] probe, int offset, out int group)
        {
            CheckKey(probe, offset);
            var hash = FxHasher.Hash(new ReadOnlySpan<uint>(probe, offset, keyArity));
            var match = new MaterializationKeyMatch(keys, keyArity, probe, offset, hash);
            if (table.TryFind(hash, ref match, out var entry))
            {
                group = entry.Group;
                return true;
            }

            group = -1;
            return false;
        }

        /// <summary>
        /// Appends the row held at <paramref name="value"/>[0..ValueArity) to the group of the key held at
        /// <paramref name="key"/>[0..KeyArity), creating the group if the key is new. egglog's materializer
        /// <c>push_bindings</c> (core-relations/src/free_join/execute.rs:2124-2153).
        /// </summary>
        /// <param name="key">The array holding the key.</param>
        /// <param name="value">The array holding the row.</param>
        public void Add(uint[] key, uint[] value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length < valueArity)
                throw new ArgumentException($"A row has {valueArity} cells.", nameof(value));

            var group = FindOrAddGroup(key, 0);
            groups[group].AddRow(new ReadOnlySpan<uint>(value, 0, valueArity));
        }

        /// <summary>
        /// Adds every row of <paramref name="other"/>, keeping this materialization's group order and appending the
        /// other's new keys in its order. Used to merge the per-thread parts of a parallel block. O(rows of other).
        /// </summary>
        /// <param name="other">A materialization of the same shape.</param>
        public void AddAll(Materialization other)
        {
            ArgumentNullException.ThrowIfNull(other);
            if (other.keyArity != keyArity || other.valueArity != valueArity)
                throw new ArgumentException("The materializations have different shapes.", nameof(other));

            for (var g = 0; g < other.count; g++)
            {
                var group = FindOrAddGroup(other.keys, g * keyArity);
                var rows = other.groups[g];
                var target = groups[group];
                for (var r = 0; r < rows.Count; r++)
                    target.AddRow(rows.GetRow(r));
            }
        }

        /// <summary>
        /// Returns every group's row storage to the pool and empties the materialization. Rows read from it must not
        /// be used afterwards. Optional: a dropped materialization is reclaimed by the garbage collector.
        /// </summary>
        public void Dispose()
        {
            for (var g = 0; g < count; g++)
                groups[g].Dispose();

            Array.Clear(groups, 0, count);
            table.Clear();
            count = 0;
        }

        /// <summary>
        /// Finds the group of the key at <paramref name="source"/>[<paramref name="offset"/>], adding it if new.
        /// </summary>
        int FindOrAddGroup(uint[] source, int offset)
        {
            CheckKey(source, offset);
            var hash = FxHasher.Hash(new ReadOnlySpan<uint>(source, offset, keyArity));
            var match = new MaterializationKeyMatch(keys, keyArity, source, offset, hash);
            var slot = table.FindOrFindInsertSlot(hash, ref match, default(MaterializationEntryHasher));
            if (slot >= 0)
                return table.GetEntry(slot).Group;

            var group = count;
            if (group == groups.Length)
                Grow();

            Array.Copy(source, offset, keys, group * keyArity, keyArity);
            groups[group] = new RowBuffer(valueArity);
            count = group + 1;
            table.InsertInSlot(hash, ~slot, new MaterializationEntry(hash, group));
            return group;
        }

        /// <summary>
        /// Doubles the room for groups.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow()
        {
            var capacity = Math.Max(8, groups.Length * 2);
            Array.Resize(ref groups, capacity);
            Array.Resize(ref keys, capacity * keyArity);
        }

        /// <summary>
        /// Throws unless a whole key fits at <paramref name="offset"/>.
        /// </summary>
        void CheckKey(uint[] source, int offset)
        {
            ArgumentNullException.ThrowIfNull(source);
            if ((uint)offset > (uint)source.Length || source.Length - offset < keyArity)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, $"The array does not hold a key of {keyArity} cells at the offset.");
        }

    }

}
