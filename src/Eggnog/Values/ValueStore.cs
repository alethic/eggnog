using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog
{

    /// <summary>
    /// The column types of one graph, by CLR type. Built in: <c>long</c> "i64", <c>int</c> "i32", <c>bool</c> "bool",
    /// <c>double</c> "f64", <c>string</c> "String", <see cref="BigInteger"/> "BigInt", <c>ValueSet&lt;string&gt;</c>
    /// "Set&lt;String&gt;", <c>ValueSet&lt;long&gt;</c> "Set&lt;i64&gt;" and <c>ValueSet&lt;int&gt;</c> "Set&lt;i32&gt;". Sorts join through
    /// <see cref="EGraph.DefineSort{TSort}(string)"/>, user types through the <c>Register*</c> methods. Thread-safe.
    /// </summary>
    /// <remarks>
    /// Names are unique across sorts and value types. Interning pools are permanent and shared by every scope of the
    /// graph: a value encodes to the same cell whichever scope interns it, and a value a child scope interned stays
    /// after the child is disposed. <see cref="InternedCount"/> reports their growth, and <see cref="EGraph.Usage"/>
    /// reports it beside the graph's ids and epochs, so a long-lived host can recycle a graph.
    /// </remarks>
    public sealed class ValueStore
    {

        readonly Relations.BaseValues values;
        readonly object sync = new();
        readonly ConcurrentDictionary<Type, ColumnType> byType = new();
        readonly Dictionary<string, ColumnType> byName = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes the store of a graph with the built-in types, registering their codecs with
        /// <paramref name="values"/>.
        /// </summary>
        /// <param name="values">The graph's base value registry.</param>
        internal ValueStore(Relations.BaseValues values)
        {
            this.values = values;
            Add(new BaseColumnType<Relations.Int64Codec, long>("i64", values, static v => v.ToString(CultureInfo.InvariantCulture), Comparer<long>.Default));
            Add(new WidenedColumnType<int>("i32", values, static v => v, static v => checked((int)v), static v => v.ToString(CultureInfo.InvariantCulture)));
            Add(new BaseColumnType<Relations.BooleanCodec, bool>("bool", values, static v => v ? "true" : "false", Comparer<bool>.Default));
            Add(new DoubleColumnType(values));
            Add(new BaseColumnType<Relations.StringCodec, string>("String", values, Relations.LiteralText.String, Relations.CodePointComparer.Instance));
            Add(new BaseColumnType<Relations.InternedCodec<BigInteger>, BigInteger>("BigInt", values, FormatBigInteger, Comparer<BigInteger>.Default));
            RegisterSet<string>();
            RegisterSet<long>();
            RegisterSet<int>();
        }

        /// <summary>
        /// Gets the number of values interned in every pool of the graph; pools never shrink.
        /// </summary>
        /// <remarks>
        /// Every scope interns into these pools, and what a scope interned outlives it. A write interns each new
        /// constant that is not stored inline (strings, doubles, sets, registered values, and <c>long</c>s outside
        /// <c>[0, 2^31)</c>, and every <c>BigInt</c>); a run interns each new result of a primitive or a lattice merge, a split each new value its
        /// cases assume, a lift each new case join; and a rule or query interns its constants when it is defined. Reads,
        /// checks and query matches never intern.
        /// </remarks>
        public long InternedCount => values.InternedCount;

        /// <summary>
        /// Gets the column type of <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The CLR type.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException"><typeparamref name="T"/> is unknown; the message names the method to
        /// call (<see cref="Register{T}"/>, <see cref="RegisterSet{T}"/>, <see cref="RegisterEnum{TEnum}"/> or
        /// <see cref="EGraph.DefineSort{TSort}(string)"/>).</exception>
        public ColumnType<T> TypeOf<T>() => (ColumnType<T>)TypeOf(typeof(T));

        /// <summary>
        /// Gets the column type of <typeparamref name="T"/>, if it is known.
        /// </summary>
        /// <typeparam name="T">The CLR type.</typeparam>
        /// <param name="type">The column type, when the result is <c>true</c>.</param>
        /// <returns>Whether <typeparamref name="T"/> is known.</returns>
        public bool TryGetType<T>([NotNullWhen(true)] out ColumnType<T>? type)
        {
            if (byType.TryGetValue(typeof(T), out var found))
            {
                type = (ColumnType<T>)found;
                return true;
            }

            type = null;
            return false;
        }

        /// <summary>
        /// Registers an interned value type over <see cref="IEquatable{T}"/>: equal values are stored once. Classes are
        /// never values: <see cref="ClassId"/> and sorts cannot be registered (a sort joins through <see
        /// cref="EGraph.DefineSort{TSort}(string)"/>), and a set type joins through <see cref="RegisterSet{T}"/>.
        /// </summary>
        /// <typeparam name="T">The CLR type; values must be immutable, and equal values must hash alike. Not
        /// <see cref="ClassId"/>, a sort or a <see cref="ValueSet{T}"/>.</typeparam>
        /// <param name="name">The type's name, unique among the store's types.</param>
        /// <param name="order">Extraction's tie-break order and <see cref="ColumnType{T}.Compare"/>; must not depend
        /// on the culture. <c>null</c>: <c>Comparer&lt;T&gt;.Default</c> when <typeparamref name="T"/> is comparable,
        /// else the values' cells.</param>
        /// <param name="format">Formats a value as a literal; <c>null</c>: its culture-invariant
        /// <c>ToString</c>.</param>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException"><typeparamref name="T"/> or <paramref name="name"/> is taken, the name
        /// is empty, or <typeparamref name="T"/> is <see cref="ClassId"/>, a sort or a set.</exception>
        public ColumnType<T> Register<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] T>(
            string name, IComparer<T>? order = null, Func<T, string>? format = null)
            where T : notnull, IEquatable<T>
        {
            EngineCallMarker.ThrowIfActive();
            NameRules.ValidateName(name, nameof(name));
            if (typeof(T) == typeof(ClassId))
                throw new ArgumentException("ClassId cannot be a value type: classes are never values; compare classes with Is and IsNot, and define e-class sorts with EGraph.DefineSort.");

            if (IsSort(typeof(T)))
                throw new ArgumentException($"{DisplayName(typeof(T))} is a sort, not a value type: define it with EGraph.DefineSort.");

            if (IsSet(typeof(T)))
                throw new ArgumentException($"{DisplayName(typeof(T))} is a set: register it with ValueStore.RegisterSet.");

            order ??= typeof(IComparable<T>).IsAssignableFrom(typeof(T)) || typeof(IComparable).IsAssignableFrom(typeof(T)) ? Comparer<T>.Default : null;
            format ??= static value => value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? string.Empty;
            lock (sync)
            {
                CheckFree(typeof(T), name);
                var type = new BaseColumnType<Relations.InternedCodec<T>, T>(name, values, format, order);
                Add(type);
                return type;
            }
        }

        /// <summary>
        /// Registers the set type of <typeparamref name="T"/>, named <c>Set&lt;{element name}&gt;</c>, or gets it when
        /// it is registered already. Sets are interned base values, kept in ascending element order: by code
        /// point for strings, <c>Comparer&lt;T&gt;.Default</c> otherwise, so the element type must be <c>string</c> or comparable
        /// (<see cref="IComparable{T}"/> or <see cref="IComparable"/>); an order given to <see cref="Register{T}"/> is
        /// not the sets' element order.
        /// </summary>
        /// <typeparam name="T">The element type; registered, comparable, and not a sort (there are no sets of
        /// classes).</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">The element type is unknown, a sort, or not comparable, or the name is
        /// taken.</exception>
        public ColumnType<ValueSet<T>> RegisterSet<T>() where T : notnull
        {
            EngineCallMarker.ThrowIfActive();
            lock (sync)
            {
                if (TryGetType<ValueSet<T>>(out var existing))
                    return existing;

                var element = TypeOf<T>();
                if (element.Kind == ColumnKind.Sort)
                    throw new ArgumentException($"Sets hold values only, never classes; {element.Name} is a sort.");

                if (typeof(T) != typeof(string) && typeof(IComparable<T>).IsAssignableFrom(typeof(T)) == false && typeof(IComparable).IsAssignableFrom(typeof(T)) == false)
                    throw new ArgumentException($"Sets keep their elements in Comparer<T>.Default order, and {DisplayName(typeof(T))} implements neither IComparable<{DisplayName(typeof(T))}> nor IComparable.");

                var name = $"Set<{element.Name}>";
                CheckFree(typeof(ValueSet<T>), name);
                var elementOrder = ValueSetOrder<T>.Comparer;
                var order = Comparer<ValueSet<T>>.Create((x, y) =>
                {
                    for (var i = 0; i < x.Count && i < y.Count; i++)
                    {
                        var c = elementOrder.Compare(x[i], y[i]);
                        if (c != 0)
                            return c;
                    }

                    return x.Count.CompareTo(y.Count);
                });

                var type = new BaseColumnType<Relations.InternedCodec<ValueSet<T>>, ValueSet<T>>(
                    name,
                    values,
                    set => set.Count == 0 ? "(set-of)" : "(set-of " + string.Join(" ", set.Select(element.FormatValue)) + ")",
                    order);
                Add(type);
                return type;
            }
        }

        /// <summary>
        /// Registers an enum, named <c>typeof(TEnum).Name</c> and stored as <c>i64</c>, or gets it when it is
        /// registered already. Values format as their member names and compare by their numeric values.
        /// </summary>
        /// <typeparam name="TEnum">The enum.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">The name is taken.</exception>
        public ColumnType<TEnum> RegisterEnum<TEnum>() where TEnum : struct, Enum
        {
            EngineCallMarker.ThrowIfActive();
            lock (sync)
            {
                if (TryGetType<TEnum>(out var existing))
                    return existing;

                var name = typeof(TEnum).Name;
                CheckFree(typeof(TEnum), name);
                var code = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(TEnum)));
                var type = new WidenedColumnType<TEnum>(name, values, v => Widen(v, code), l => Narrow<TEnum>(l, code), static v => v.ToString());
                Add(type);
                return type;
            }
        }

        /// <summary>
        /// Gets the column type of a CLR type.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">The type is unknown; the message names the method to call.</exception>
        internal ColumnType TypeOf(Type clrType)
        {
            if (byType.TryGetValue(clrType, out var type))
                return type;

            throw new ArgumentException($"Type {DisplayName(clrType)} is not registered; call {RegisterMethod(clrType)}.");
        }

        /// <summary>
        /// Gets the column type of a CLR type, if it is known.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <param name="type">The column type, when the result is <c>true</c>.</param>
        /// <returns>Whether the type is known.</returns>
        internal bool TryGetType(Type clrType, [NotNullWhen(true)] out ColumnType? type) => byType.TryGetValue(clrType, out type);

        /// <summary>
        /// Gets whether a sort or value type holds a name.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns>Whether it is taken.</returns>
        internal bool IsNameTaken(string name)
        {
            lock (sync)
                return byName.ContainsKey(name);
        }

        /// <summary>
        /// Registers a sort (<see cref="EGraph.DefineSort{TSort}(string)"/>).
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="name">The sort's name.</param>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException"><typeparamref name="TSort"/> or <paramref name="name"/> is
        /// taken.</exception>
        internal ColumnType<TSort> AddSort<TSort>(string name) where TSort : struct, ISort<TSort>
        {
            lock (sync)
            {
                CheckFree(typeof(TSort), name);
                var type = new SortColumnType<TSort>(name);
                Add(type);
                return type;
            }
        }

        /// <summary>
        /// Gets the method that registers a CLR type: what an unknown-type message tells the caller to call.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>The method, as <c>ValueStore.Register</c>, <c>ValueStore.RegisterSet</c>,
        /// <c>ValueStore.RegisterEnum</c> or <c>EGraph.DefineSort</c>.</returns>
        [UnconditionalSuppressMessage("Trimming", "IL2067",
            Justification = "The result only words a hint in an error message. A sort the program defines keeps its ISort<> "
                + "implementation, because EGraph.DefineSort constrains its type argument to it; a type whose implementation the "
                + "trimmer removed is not a defined sort, and the hint then names ValueStore.Register.")]
        internal static string RegisterMethod(Type clrType)
        {
            if (IsSet(clrType))
                return "ValueStore.RegisterSet";

            if (clrType.IsEnum)
                return "ValueStore.RegisterEnum";

            if (IsSort(clrType))
                return "EGraph.DefineSort";

            return "ValueStore.Register";
        }

        /// <summary>
        /// Gets whether a CLR type is a sort: whether it implements <see cref="ISort{TSort}"/>.
        /// </summary>
        /// <param name="clrType">The CLR type, with its interfaces kept by the trimmer.</param>
        /// <returns>Whether the type is a sort.</returns>
        internal static bool IsSort([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type clrType)
        {
            foreach (var face in clrType.GetInterfaces())
                if (face.IsGenericType && face.GetGenericTypeDefinition() == typeof(ISort<>))
                    return true;

            return false;
        }

        /// <summary>
        /// Gets whether a CLR type is a set type, a <see cref="ValueSet{T}"/>.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>Whether the type is a set type.</returns>
        internal static bool IsSet(Type clrType) => clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(ValueSet<>);

        /// <summary>
        /// Gets a readable name of a CLR type: <c>Foo</c>, <c>ValueSet&lt;Foo&gt;</c>.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>The name.</returns>
        internal static string DisplayName(Type clrType)
        {
            if (clrType.IsGenericType == false)
                return clrType.Name;

            var name = clrType.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
                name = name.Substring(0, tick);

            return name + "<" + string.Join(", ", clrType.GetGenericArguments().Select(DisplayName)) + ">";
        }

        /// <summary>
        /// Throws when a CLR type or a name is taken. Called under the lock.
        /// </summary>
        void CheckFree(Type clrType, string name)
        {
            if (byType.TryGetValue(clrType, out var taken))
                throw new ArgumentException($"Type {DisplayName(clrType)} is already registered, as '{taken.Name}'.", nameof(name));

            if (byName.ContainsKey(name))
                throw new ArgumentException($"A type named '{name}' is already registered.", nameof(name));
        }

        /// <summary>
        /// Adds a type. Called under the lock, or from the constructor.
        /// </summary>
        void Add(ColumnType type)
        {
            type.Store = this;
            byName.Add(type.Name, type);
            byType[type.ClrType] = type;
        }

        /// <summary>
        /// Formats a <c>BigInt</c> as egglog renders one, <c>(from-string "123")</c>: its sort reconstructs a value as
        /// that call (<c>src/sort/bigint.rs</c>, <c>reconstruct_termdag</c>).
        /// </summary>
        internal static string FormatBigInteger(BigInteger value) => "(from-string \"" + value.ToString(CultureInfo.InvariantCulture) + "\")";

        /// <summary>
        /// Widens an enum value to the <c>long</c> it is stored as: sign-extended for signed underlying types,
        /// zero-extended for unsigned ones (a <c>ulong</c> above <c>long.MaxValue</c> wraps, which is still injective).
        /// </summary>
        static long Widen<TEnum>(TEnum value, TypeCode code) where TEnum : struct, Enum => code switch
        {
            TypeCode.SByte => Unsafe.As<TEnum, sbyte>(ref value),
            TypeCode.Byte => Unsafe.As<TEnum, byte>(ref value),
            TypeCode.Int16 => Unsafe.As<TEnum, short>(ref value),
            TypeCode.UInt16 => Unsafe.As<TEnum, ushort>(ref value),
            TypeCode.Int32 => Unsafe.As<TEnum, int>(ref value),
            TypeCode.UInt32 => Unsafe.As<TEnum, uint>(ref value),
            TypeCode.Int64 => Unsafe.As<TEnum, long>(ref value),
            _ => unchecked((long)Unsafe.As<TEnum, ulong>(ref value)),
        };

        /// <summary>
        /// Inverts <see cref="Widen{TEnum}"/>.
        /// </summary>
        static TEnum Narrow<TEnum>(long stored, TypeCode code) where TEnum : struct, Enum
        {
            switch (code)
            {
                case TypeCode.SByte:
                {
                    var narrow = unchecked((sbyte)stored);
                    return Unsafe.As<sbyte, TEnum>(ref narrow);
                }
                case TypeCode.Byte:
                {
                    var narrow = unchecked((byte)stored);
                    return Unsafe.As<byte, TEnum>(ref narrow);
                }
                case TypeCode.Int16:
                {
                    var narrow = unchecked((short)stored);
                    return Unsafe.As<short, TEnum>(ref narrow);
                }
                case TypeCode.UInt16:
                {
                    var narrow = unchecked((ushort)stored);
                    return Unsafe.As<ushort, TEnum>(ref narrow);
                }
                case TypeCode.Int32:
                {
                    var narrow = unchecked((int)stored);
                    return Unsafe.As<int, TEnum>(ref narrow);
                }
                case TypeCode.UInt32:
                {
                    var narrow = unchecked((uint)stored);
                    return Unsafe.As<uint, TEnum>(ref narrow);
                }
                case TypeCode.Int64:
                    return Unsafe.As<long, TEnum>(ref stored);
                default:
                {
                    var narrow = unchecked((ulong)stored);
                    return Unsafe.As<ulong, TEnum>(ref narrow);
                }
            }
        }

    }

}
