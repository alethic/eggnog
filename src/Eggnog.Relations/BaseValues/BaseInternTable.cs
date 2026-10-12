using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The intern table of one base value type, with egglog's rules for which values are stored inline and how interned
    /// ones are tagged: the port of <c>BaseInternTable&lt;P&gt;</c> (core-relations/src/base_values/mod.rs:133-188).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a codec whose <see cref="IBaseValueCodec{T}.MayUnbox"/> is <c>false</c> the <see cref="Value"/> of a base
    /// value is its index in <see cref="Table"/>. For one whose <see cref="IBaseValueCodec{T}.MayUnbox"/> is <c>true</c>,
    /// values the codec cannot box inline are interned and stored as <see cref="BaseValues.InternedTag"/> plus the
    /// index, so that the top bit tells inline values from interned ones.
    /// </para>
    /// <para>
    /// Thread-safe: interning and lookups go through <see cref="InternTable{TKey}"/>, which is fully concurrent.
    /// Costs: the inline path is the codec's compare and cast; the interned path is one
    /// <see cref="InternTable{TKey}.Intern"/> (a hash, a shard lock, an expected-O(1) probe), one
    /// <see cref="InternTable{TKey}.TryGetId"/> (the same, never appending) or one lock-free
    /// <see cref="InternTable{TKey}.Get"/>.
    /// </para>
    /// </remarks>
    /// <typeparam name="TCodec">The codec, which identifies the base value type.</typeparam>
    /// <typeparam name="T">The .NET type of the base values.</typeparam>
    internal sealed class BaseInternTable<TCodec, T> : BaseValueTable
        where TCodec : struct, IBaseValueCodec<T>
        where T : notnull, IEquatable<T>
    {

        readonly InternTable<T> table = new();

        /// <summary>
        /// Initializes a new, empty instance.
        /// </summary>
        /// <param name="id">The id of the type in the registry that creates the table.</param>
        public BaseInternTable(BaseValueTypeId id)
            : base(id, typeof(TCodec), typeof(T))
        {

        }

        /// <summary>
        /// Gets the underlying intern table, which holds every base value of this type that did not box inline.
        /// </summary>
        public InternTable<T> Table => table;

        /// <summary>
        /// Gets the <see cref="Value"/> of <paramref name="value"/>, interning it if it does not box inline: egglog's
        /// <c>BaseInternTable::intern</c>.
        /// </summary>
        /// <param name="value">The base value.</param>
        /// <returns>Its value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Value Intern(T value)
        {
            if (TCodec.MayUnbox && TCodec.TryBox(value, out var boxed))
                return boxed;

            return InternInTable(value);
        }

        /// <summary>
        /// Interns a base value that does not box inline and returns its <see cref="Value"/>: the interned half of
        /// egglog's <c>BaseInternTable::intern</c>.
        /// </summary>
        /// <param name="value">A base value that the codec cannot box inline.</param>
        /// <returns>Its value: the intern index, tagged with <see cref="BaseValues.InternedTag"/> when the codec boxes
        /// some values inline.</returns>
        public Value InternInTable(T value)
        {
            var index = table.Intern(value);
            if (TCodec.MayUnbox == false)
                return new Value(index);

            // egglog checks that index + VAL_OFFSET does not overflow (core-relations/src/base_values/mod.rs:164-171) but
            // lets index 2^31 - 1 through, which tags to u32::MAX, the stale marker. That index is rejected here too. It is
            // unreachable in practice: the intern table holds at most Array.MaxLength keys.
            if (index > BaseValues.MaxTaggedIndex)
                throw new InvalidOperationException($"Interned value overflowed: base value type {Id} holds too many interned values.");

            return new Value(index + BaseValues.InternedTag);
        }

        /// <summary>
        /// Gets the <see cref="Value"/> of a base value that does not box inline, if it has been interned: the lookup
        /// half of <see cref="InternInTable"/>, which never appends. One <see cref="InternTable{TKey}.TryGetId"/>.
        /// </summary>
        /// <param name="value">A base value that the codec cannot box inline.</param>
        /// <param name="boxed">Its value when this returns <c>true</c>, tagged as <see cref="InternInTable"/> tags it;
        /// otherwise <c>default</c>.</param>
        /// <returns>Whether the value has been interned.</returns>
        public bool TryGetExistingInTable(T value, out Value boxed)
        {
            if (table.TryGetId(value, out var index) == false)
            {
                boxed = default;
                return false;
            }

            if (TCodec.MayUnbox == false)
            {
                boxed = new Value(index);
                return true;
            }

            // InternInTable rejects an index above MaxTaggedIndex only after the intern table has stored it, so such a
            // key could in principle be found here. It cannot exist: the table holds at most Array.MaxLength keys, and
            // Array.MaxLength - 1 < MaxTaggedIndex.
            Debug.Assert(index <= BaseValues.MaxTaggedIndex);
            boxed = new Value(index + BaseValues.InternedTag);
            return true;
        }

        /// <inheritdoc />
        public override int InternedCount => table.Count;

        /// <summary>
        /// Gets the base value stored as <paramref name="value"/>: egglog's <c>BaseInternTable::get</c>.
        /// </summary>
        /// <param name="value">A value of this type.</param>
        /// <returns>The base value.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is neither inline nor interned.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get(Value value)
        {
            if (TCodec.MayUnbox && TCodec.TryUnbox(value, out var unboxed))
                return unboxed;

            return GetFromTable(value);
        }

        /// <summary>
        /// Gets the interned base value stored as <paramref name="value"/>: the interned half of egglog's
        /// <c>BaseInternTable::get</c>. Lock-free.
        /// </summary>
        /// <param name="value">A value of this type that the codec does not unbox inline.</param>
        /// <returns>The base value.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not an interned value.</exception>
        public T GetFromTable(Value value)
        {
            if (TCodec.MayUnbox == false)
                return table.Get(value.Rep);

            // A value without the tag (one a conforming codec would have unboxed) wraps to an index of at least 2^31,
            // which the table rejects; Rust's subtraction would panic in a debug build.
            return table.Get(unchecked(value.Rep - BaseValues.InternedTag));
        }

        /// <inheritdoc />
        public override string Format(Value value) => TCodec.Format(Get(value));

    }

}
