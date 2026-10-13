using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The registry of base value types (integers, booleans, unit, strings, floats and any other equatable .NET type),
    /// which maps each base value to the 32-bit <see cref="Value"/> a table cell holds and back: the port of egglog's
    /// <c>BaseValues</c> (core-relations/src/base_values/mod.rs:66-124).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A base value type is a codec (<see cref="IBaseValueCodec{T}"/>) over a .NET type. Each registered type gets a
    /// <see cref="BaseValueTypeId"/>, in registration order, and its own intern table. A value is stored inline when its
    /// codec can box it (small integers, <see cref="bool"/>, unit, and 64-bit integers in <c>[0, 2^31)</c>); otherwise it
    /// is interned. Boxing is canonical: within one type, equal base values get equal <see cref="Value"/>s and distinct
    /// ones distinct <see cref="Value"/>s, so joins and lookups compare raw cells. <see cref="Value"/> order is not
    /// numeric order. A value carries no type tag: only the column it is stored in says which type it belongs to.
    /// </para>
    /// <para>
    /// The encodings are egglog's exactly (core-relations/src/base_values/unboxed.rs). One consequence is kept from
    /// egglog: <see cref="SByteCodec"/>, <see cref="Int16Codec"/> and <see cref="Int32Codec"/> box <c>-1</c>, and
    /// <see cref="UInt32Codec"/> boxes <see cref="uint.MaxValue"/>, to <see cref="Value.StaleRep"/>. Such a value must not
    /// be stored in the first column of a table, where that representation marks a deleted row.
    /// </para>
    /// <para>
    /// Thread-safe. <see cref="Get{TCodec, T}"/> and <see cref="Unwrap{TCodec, T}"/> never lock on the inline path or on
    /// lookups, and take one intern-table shard lock to intern. Registration takes a lock and publishes copy-on-write
    /// arrays, so it may run concurrently with boxing (egglog requires <c>&amp;mut self</c> for it instead). Interned
    /// values are never freed: the store is append-only, as in egglog.
    /// </para>
    /// <para>
    /// Two members egglog lacks serve readers that must not grow the store (request E1 of the rule-layer spec):
    /// <see cref="TryGetExisting{TCodec, T}"/> finds a value's <see cref="Value"/> without interning it, and
    /// <see cref="InternedCount"/> reports how many values have been interned.
    /// </para>
    /// <para>
    /// egglog finds a type's table by hashing its <c>TypeId</c> on every interned <c>get</c> and <c>unwrap</c>. Here each
    /// base value type has a process-wide slot number (<see cref="BaseValueSlot{TCodec, T}"/>), a JIT-time constant, and
    /// the registry indexes an array by it.
    /// </para>
    /// </remarks>
    internal sealed class BaseValues
    {

        /// <summary>
        /// The tag added to the intern index of a value whose codec boxes some values inline but could not box this one:
        /// egglog's <c>VAL_OFFSET</c> (core-relations/src/base_values/mod.rs:156). Inline values of such codecs have the
        /// top bit clear.
        /// </summary>
        public const uint InternedTag = 0x8000_0000u;

        /// <summary>
        /// The largest intern index that can be tagged with <see cref="InternedTag"/> without producing
        /// <see cref="Value.StaleRep"/>.
        /// </summary>
        internal const uint MaxTaggedIndex = Value.StaleRep - InternedTag - 1;

        /// <summary>
        /// The number of slots allocated so far, across all registries.
        /// </summary>
        static int slotCount;

        readonly object sync = new();
        readonly Dictionary<Type, BaseValueTypeId> idsByCodec;

        /// <summary>
        /// The tables by <see cref="BaseValueTypeId"/>. Never written after publication: registration replaces the
        /// array, so clones can share it.
        /// </summary>
        BaseValueTable[] tables;

        /// <summary>
        /// The tables by <see cref="BaseValueSlot{TCodec, T}"/> slot, <c>null</c> where this registry has no such type.
        /// Never written after publication.
        /// </summary>
        BaseValueTable?[] tablesBySlot;

        /// <summary>
        /// Initializes a new, empty registry.
        /// </summary>
        public BaseValues()
        {
            idsByCodec = new Dictionary<Type, BaseValueTypeId>();
            tables = Array.Empty<BaseValueTable>();
            tablesBySlot = Array.Empty<BaseValueTable?>();
        }

        /// <summary>
        /// Initializes a clone of <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The registry to clone.</param>
        BaseValues(BaseValues source)
        {
            lock (source.sync)
            {
                idsByCodec = new Dictionary<Type, BaseValueTypeId>(source.idsByCodec);
                tables = source.tables;
                tablesBySlot = source.tablesBySlot;
            }
        }

        /// <summary>
        /// Registers the base value type described by <typeparamref name="TCodec"/>, or gets its id if it is already
        /// registered: egglog's <c>register_type</c>. Ids are assigned in registration order, from 0. Thread-safe.
        /// </summary>
        /// <typeparam name="TCodec">The codec, which identifies the type.</typeparam>
        /// <typeparam name="T">The .NET type of the base values.</typeparam>
        /// <returns>The id of the type.</returns>
        /// <exception cref="InvalidOperationException"><typeparamref name="TCodec"/> is already registered for a
        /// different value type.</exception>
        public BaseValueTypeId RegisterType<TCodec, T>()
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            var slot = BaseValueSlot<TCodec, T>.Index;
            lock (sync)
            {
                var bySlot = tablesBySlot;
                if ((uint)slot < (uint)bySlot.Length && bySlot[slot] is { } registered)
                    return registered.Id;

                // A codec type identifies one base value type (GetTypeId(Type) looks it up by codec), so a codec that
                // implements IBaseValueCodec<T> for several T can be registered for only one of them.
                if (idsByCodec.TryGetValue(typeof(TCodec), out var other))
                    throw new InvalidOperationException(
                        $"Codec {typeof(TCodec)} is already registered as base value type {other} for " +
                        $"{tables[other.Index].ValueType}, not {typeof(T)}.");

                var current = tables;
                var id = new BaseValueTypeId(current.Length);
                var table = new BaseInternTable<TCodec, T>(id);

                var grownTables = new BaseValueTable[current.Length + 1];
                Array.Copy(current, grownTables, current.Length);
                grownTables[id.Index] = table;

                var grownBySlot = new BaseValueTable?[Math.Max(bySlot.Length, slot + 1)];
                Array.Copy(bySlot, grownBySlot, bySlot.Length);
                grownBySlot[slot] = table;

                idsByCodec.Add(typeof(TCodec), id);

                // Release: a reader that sees either array sees the fully built table in it. The id array goes first,
                // so a thread that can box a value of the new type can also format it.
                Volatile.Write(ref tables, grownTables);
                Volatile.Write(ref tablesBySlot, grownBySlot);
                return id;
            }
        }

        /// <summary>
        /// Gets the id of the registered base value type <typeparamref name="TCodec"/>: egglog's <c>get_ty</c>.
        /// Lock-free.
        /// </summary>
        /// <typeparam name="TCodec">The codec, which identifies the type.</typeparam>
        /// <typeparam name="T">The .NET type of the base values.</typeparam>
        /// <returns>The id of the type.</returns>
        /// <exception cref="InvalidOperationException">The type is not registered.</exception>
        public BaseValueTypeId GetTypeId<TCodec, T>()
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            return GetTable<TCodec, T>().Id;
        }

        /// <summary>
        /// Gets the id of the registered base value type whose codec is <paramref name="codecType"/>: egglog's
        /// <c>get_ty_by_id</c>, for callers that know the type only at run time. Takes the registration lock.
        /// </summary>
        /// <param name="codecType">The codec type.</param>
        /// <returns>The id of the type.</returns>
        /// <exception cref="InvalidOperationException">No base value type with that codec is registered.</exception>
        public BaseValueTypeId GetTypeId(Type codecType)
        {
            if (codecType is null)
                throw new ArgumentNullException(nameof(codecType));

            lock (sync)
                if (idsByCodec.TryGetValue(codecType, out var id))
                    return id;

            throw new InvalidOperationException($"No base value type with codec {codecType} is registered.");
        }

        /// <summary>
        /// Gets the <see cref="Value"/> that stores <paramref name="value"/>, interning it if its codec cannot box it
        /// inline: egglog's <c>BaseValues::get</c>. Thread-safe.
        /// </summary>
        /// <remarks>
        /// The inline path is a JIT-time constant check plus the codec's compare and cast; it needs no registration (as in
        /// egglog, a value that boxes inline never consults the registry). The interned path takes one intern-table shard
        /// lock.
        /// </remarks>
        /// <typeparam name="TCodec">The codec, which identifies the type.</typeparam>
        /// <typeparam name="T">The .NET type of the base values.</typeparam>
        /// <param name="value">The base value.</param>
        /// <returns>Its value.</returns>
        /// <exception cref="InvalidOperationException">The value must be interned and its type is not registered.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Value Get<TCodec, T>(T value)
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            if (TCodec.MayUnbox && TCodec.TryBox(value, out var boxed))
                return boxed;

            return GetInterned<TCodec, T>(value);
        }

        /// <summary>
        /// Gets the base value stored as <paramref name="value"/>: egglog's <c>BaseValues::unwrap</c>. Thread-safe and
        /// lock-free.
        /// </summary>
        /// <remarks>
        /// The inline path needs no registration, as in egglog. The interned path is an array lookup in the type's intern
        /// table.
        /// </remarks>
        /// <typeparam name="TCodec">The codec, which identifies the type.</typeparam>
        /// <typeparam name="T">The .NET type of the base values.</typeparam>
        /// <param name="value">A value of this type, from <see cref="Get{TCodec, T}"/>.</param>
        /// <returns>The base value.</returns>
        /// <exception cref="InvalidOperationException">The value is interned and its type is not registered.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The value is neither inline nor interned.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Unwrap<TCodec, T>(Value value)
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            if (TCodec.MayUnbox && TCodec.TryUnbox(value, out var unboxed))
                return unboxed;

            return UnwrapInterned<TCodec, T>(value);
        }

        /// <summary>
        /// Gets the <see cref="Value"/> that stores <paramref name="value"/> if it boxes inline or has already been
        /// interned, without interning it. egglog has no such member: it serves readers that must not grow the store
        /// with values they only look up (rule-layer D15, request E1). Thread-safe.
        /// </summary>
        /// <remarks>
        /// The inline path is <see cref="Get{TCodec, T}"/>'s and needs no registration: a value that boxes inline always
        /// succeeds and never consults the registry. The interned path is one lookup in the type's intern table under one
        /// shard lock (<see cref="InternTable{TKey}.TryGetId"/>), and it never adds to the table: a value never interned
        /// fails, and <see cref="InternedCount"/> stays unchanged. A value that succeeds gets exactly the
        /// <see cref="Value"/> <see cref="Get{TCodec, T}"/> returns.
        /// </remarks>
        /// <typeparam name="TCodec">The codec, which identifies the type.</typeparam>
        /// <typeparam name="T">The .NET type of the base values.</typeparam>
        /// <param name="value">The base value.</param>
        /// <param name="boxed">The value that stores it when this returns <c>true</c>; otherwise <c>default</c>.</param>
        /// <returns>Whether <paramref name="value"/> boxes inline or has been interned.</returns>
        /// <exception cref="InvalidOperationException">The value would have to be interned and its type is not
        /// registered, as for <see cref="Get{TCodec, T}"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetExisting<TCodec, T>(T value, out Value boxed)
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            if (TCodec.MayUnbox && TCodec.TryBox(value, out boxed))
                return true;

            return TryGetExistingInterned<TCodec, T>(value, out boxed);
        }

        /// <summary>
        /// Gets the number of base values interned in this registry's intern tables, summed over every registered type:
        /// how much the append-only store has grown (rule-layer D15, request E1). Values stored inline are not counted.
        /// </summary>
        /// <remarks>
        /// Lock-free; costs one read per registered type. Never decreases. A clone (<see cref="Clone"/>) shares the
        /// tables of the types registered before it was made, so a value of such a type interned through either
        /// registry counts in both. Under concurrent interning the sum reads each table at its own moment, not all of
        /// them at once.
        /// </remarks>
        public long InternedCount
        {
            get
            {
                var current = Volatile.Read(ref tables);
                var total = 0L;
                foreach (var table in current)
                    total += table.InternedCount;

                return total;
            }
        }

        /// <summary>
        /// Formats the base value stored as <paramref name="value"/> in a column of type <paramref name="type"/>, as
        /// egglog's <c>BaseValuePrinter</c> does (core-relations/src/base_values/mod.rs:56-64). Cold path.
        /// </summary>
        /// <param name="type">The registered type of the value.</param>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not registered.</exception>
        public string Format(BaseValueTypeId type, Value value)
        {
            var current = Volatile.Read(ref tables);
            if ((uint)type.Index >= (uint)current.Length)
                throw new ArgumentOutOfRangeException(nameof(type), $"Base value type {type} is not registered.");

            return current[type.Index].Format(value);
        }

        /// <summary>
        /// Creates a registry with the same types and ids that shares this one's intern tables: egglog's derived
        /// <c>Clone</c>, whose <c>InternTable</c>s share their storage through <c>Arc</c>s. A value interned through
        /// either registry is visible through both. Types registered afterwards belong to one registry only.
        /// </summary>
        /// <returns>The clone.</returns>
        public BaseValues Clone() => new(this);

        /// <summary>
        /// Allocates the next process-wide base value type slot. Called once per type by
        /// <see cref="BaseValueSlot{TCodec, T}"/>.
        /// </summary>
        /// <returns>The slot.</returns>
        internal static int AllocateSlot() => Interlocked.Increment(ref slotCount) - 1;

        /// <summary>
        /// Gets the typed intern table of a registered type. Lock-free.
        /// </summary>
        /// <typeparam name="TCodec">The codec, which identifies the type.</typeparam>
        /// <typeparam name="T">The .NET type of the base values.</typeparam>
        /// <returns>The table.</returns>
        /// <exception cref="InvalidOperationException">The type is not registered.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BaseInternTable<TCodec, T> GetTable<TCodec, T>()
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            var slot = BaseValueSlot<TCodec, T>.Index;
            var bySlot = Volatile.Read(ref tablesBySlot);
            if ((uint)slot < (uint)bySlot.Length)
            {
                var table = bySlot[slot];

                // RegisterType fills this slot only with a BaseInternTable<TCodec, T>, so the unchecked cast is exact.
                if (table is not null)
                    return Unsafe.As<BaseInternTable<TCodec, T>>(table);
            }

            return ThrowNotRegistered<TCodec, T>();
        }

        /// <summary>
        /// The interned half of <see cref="Get{TCodec, T}"/>, kept out of line so the inline path stays small.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        Value GetInterned<TCodec, T>(T value)
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            return GetTable<TCodec, T>().InternInTable(value);
        }

        /// <summary>
        /// The interned half of <see cref="Unwrap{TCodec, T}"/>, kept out of line so the inline path stays small.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        T UnwrapInterned<TCodec, T>(Value value)
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            return GetTable<TCodec, T>().GetFromTable(value);
        }

        /// <summary>
        /// The interned half of <see cref="TryGetExisting{TCodec, T}"/>, kept out of line so the inline path stays small.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        bool TryGetExistingInterned<TCodec, T>(T value, out Value boxed)
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            return GetTable<TCodec, T>().TryGetExistingInTable(value, out boxed);
        }

        /// <summary>
        /// Throws the exception for a base value type that is not registered.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static BaseInternTable<TCodec, T> ThrowNotRegistered<TCodec, T>()
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            throw new InvalidOperationException($"The base value type with codec {typeof(TCodec)} for {typeof(T)} is not registered.");
        }

    }

}
