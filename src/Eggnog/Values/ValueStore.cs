using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// The column types of one graph, by CLR type. Built in: <c>long</c> "i64", <c>int</c> "i32", <c>bool</c> "bool",
    /// <c>double</c> "f64", <c>string</c> "String", <see cref="BigInteger"/> "BigInt", <see cref="BigRational"/> "BigRat",
    /// <see cref="ValueTuple"/> "Unit",
    /// <c>Set&lt;string&gt;</c> "Set&lt;String&gt;", <c>Set&lt;long&gt;</c> "Set&lt;i64&gt;" and <c>Set&lt;int&gt;</c>
    /// "Set&lt;i32&gt;". Sorts join through
    /// <see cref="EGraph.DefineSort{TSort}(string)"/>, user types and containers (sets, pairs, vectors, maps, multisets) through
    /// the <c>Register*</c> methods. Thread-safe; the first container type of each kind registered is an engine
    /// definition, which while the root is writable enters its domain.
    /// </summary>
    /// <remarks>
    /// Names are unique across sorts and value types. Interning pools are permanent and shared by every scope of the
    /// graph: a value encodes to the same cell whichever scope interns it, and a value a child scope interned stays
    /// after the child is disposed. A container of classes a child scope makes is the exception: the child keeps it
    /// in its own containers, under a cell of its own, and it goes with the child, as egglog's push keeps it in its
    /// copy (decisions row 9). <see cref="InternedCount"/> reports the pools' growth, and <see cref="EGraph.Usage"/>
    /// reports it beside the graph's ids and epochs, so a long-lived host can recycle a graph.
    /// </remarks>
    public sealed class ValueStore
    {

        readonly EGraph graph;
        readonly Relations.BaseValues values;
        readonly object sync = new();
        Relations.ContainerEnvBase[] containers = [];
        readonly ConcurrentDictionary<Type, ColumnType> byType = new();
        readonly Dictionary<string, ColumnType> byName = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the graph's function values' targets.
        /// </summary>
        internal FnTargets FnTargets { get; }

        /// <summary>
        /// Initializes the store of a graph with the built-in types, registering their codecs with the graph's base value
        /// registry.
        /// </summary>
        /// <param name="graph">The graph, whose <see cref="EGraph.BaseValues"/> is set.</param>
        internal ValueStore(EGraph graph)
        {
            this.graph = graph;
            values = graph.BaseValues;
            FnTargets = new FnTargets(graph);
            Add(new BaseColumnType<Relations.Int64Codec, long>("i64", values, static v => v.ToString(CultureInfo.InvariantCulture), Comparer<long>.Default));
            Add(new WidenedColumnType<int>("i32", values, static v => v, static v => checked((int)v), static v => v.ToString(CultureInfo.InvariantCulture)));
            Add(new BaseColumnType<Relations.BooleanCodec, bool>("bool", values, static v => v ? "true" : "false", Comparer<bool>.Default));
            Add(new DoubleColumnType(values));
            Add(new BaseColumnType<Relations.StringCodec, string>("String", values, Relations.LiteralText.String, Relations.CodePointComparer.Instance));
            Add(new BaseColumnType<Relations.InternedCodec<BigInteger>, BigInteger>("BigInt", values, FormatBigInteger, Comparer<BigInteger>.Default));
        }

        /// <summary>
        /// Adds egglog's <c>Unit</c> sort, of <see cref="ValueTuple"/>: the output of relations, and of functions, function
        /// sorts and primitives that hold no value, such as egglog's fallible comparisons.
        /// </summary>
        /// <param name="unit">The graph's unit type.</param>
        internal void AddUnit(UnitColumnType unit) => Add(unit);

        /// <summary>
        /// Registers the built-in set types, once the graph's root exists: a set is a container, whose environment the
        /// graph registers in the root's domain.
        /// </summary>
        internal void RegisterBuiltInSets()
        {
            Add(new BaseColumnType<BigRationalCodec, BigRational>("BigRat", values, FormatBigRational, Comparer<BigRational>.Default));
            RegisterSet<string>();
            RegisterSet<long>();
            RegisterSet<int>();
        }

        /// <summary>
        /// Gets the number of values interned in every pool of the graph; pools never shrink.
        /// </summary>
        /// <remarks>
        /// Every scope interns into these pools, and what a scope interned outlives it. A write interns each new
        /// constant that is not stored inline (strings, doubles, sets, pairs, vectors, registered values, and <c>long</c>s outside
        /// <c>[0, 2^31)</c>, and every <c>BigInt</c> and <c>BigRat</c>); a run interns each new result of a primitive or a lattice merge,
        /// a split each new value its cases assume, a lift each new case join; and a rule or query interns its constants
        /// when it is defined, and when it runs the new results of the computations in its body. Reads and checks never
        /// intern. A container (a set, pair, vector, map or multiset) also takes an id from the graph's id counter, as egglog's
        /// containers do. A container of classes a child scope makes is not in these pools and not counted: the child
        /// keeps it in its own containers until it is disposed.
        /// </remarks>
        public long InternedCount
        {
            get
            {
                var count = values.InternedCount;
                foreach (var env in Volatile.Read(ref containers))
                    count += env.Count;

                return count;
            }
        }

        /// <summary>
        /// Gets the column type of <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The CLR type.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException"><typeparamref name="T"/> is unknown; the message names the method to
        /// call (<see cref="Register{T}"/>, <see cref="RegisterSet{T}"/>, <see cref="RegisterPair{T1, T2}"/>,
        /// <see cref="RegisterVec{T}"/>, <see cref="RegisterMap{TKey, TValue}"/>, <see cref="RegisterMultiSet{T}"/>,
        /// <see cref="RegisterEnum{TEnum}"/> or <see cref="EGraph.DefineSort{TSort}(string)"/>).</exception>
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
        /// <see cref="ClassId"/>, a sort or a <see cref="Set{T}"/>.</typeparam>
        /// <param name="name">The type's name, unique among the store's types.</param>
        /// <param name="order">The content order of <see cref="ColumnType{T}.Compare"/>, which host reads sort by; must
        /// not depend on the culture. <c>null</c>: <c>Comparer&lt;T&gt;.Default</c> when <typeparamref name="T"/> is comparable,
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

            if (IsPair(typeof(T)))
                throw new ArgumentException($"{DisplayName(typeof(T))} is a pair: register it with ValueStore.RegisterPair.");

            if (IsVec(typeof(T)))
                throw new ArgumentException($"{DisplayName(typeof(T))} is a vector: register it with ValueStore.RegisterVec.");

            if (IsMap(typeof(T)))
                throw new ArgumentException($"{DisplayName(typeof(T))} is a map: register it with ValueStore.RegisterMap.");

            if (IsMultiSet(typeof(T)))
                throw new ArgumentException($"{DisplayName(typeof(T))} is a multiset: register it with ValueStore.RegisterMultiSet.");

            if (IsFn(typeof(T)))
                throw new ArgumentException($"{DisplayName(typeof(T))} is a function sort: register it with ValueStore.RegisterFn.");

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
        /// Registers the set type of <typeparamref name="T"/>, egglog's <c>(sort … (Set T))</c>, named
        /// <c>Set&lt;{element name}&gt;</c>, or gets it when it is registered already. A set is egglog's <c>Set</c>
        /// container: its cell is a container id from the graph's id counter, equal sets have one cell, and its
        /// primitives are <see cref="Primitives"/>' <c>Set*</c> methods. It extracts as <c>(set-of a b)</c>, its
        /// elements in cell order as egglog's, and costs what its elements cost. The elements may be values, containers
        /// included, classes of a sort, or containers of them, and are <c>string</c>, comparable
        /// (<see cref="IComparable{T}"/> or <see cref="IComparable"/>), or classes, which order by cell, as a
        /// <see cref="Set{T}"/> keeps its elements in element order; an order given to <see cref="Register{T}"/> is not
        /// the sets' element order. A set of classes is egglog's eq-sort container: rebuilt when its classes merge,
        /// elements that become one becoming one element, never unioned, and made at a writable root and in a writable
        /// child, which keeps what it makes in its own containers until it is disposed (decisions row 9). The graph
        /// registers <c>Set&lt;String&gt;</c>, <c>Set&lt;i64&gt;</c> and <c>Set&lt;i32&gt;</c> itself; the first set
        /// type that holds classes is an engine definition, which while the root is writable enters the root's domain,
        /// and registers while the root is frozen too, as egglog declares a sort inside a <c>(push)</c>.
        /// </summary>
        /// <typeparam name="T">The element type; registered, and comparable.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">The element type is unknown or not comparable, or the name is
        /// taken.</exception>
        /// <exception cref="InvalidOperationException">The first set type that holds classes is registered while another
        /// thread is using the writable root.</exception>
        public ColumnType<Set<T>> RegisterSet<T>() where T : notnull
        {
            EngineCallMarker.ThrowIfActive();
            if (TryGetType<Set<T>>(out var existing))
                return existing;

            var element = TypeOf<T>();
            if (element.IsOrdered == false)
                throw new ArgumentException($"Sets keep their elements in Comparer<T>.Default order, and {DisplayName(typeof(T))} implements neither IComparable<{DisplayName(typeof(T))}> nor IComparable, or holds an element that does not.");

            var env = element.InvolvesClasses
                ? graph.ClassContainers<Relations.SetContainer>()
                : graph.ValueContainers<Relations.SetContainer>();
            lock (sync)
            {
                if (TryGetType<Set<T>>(out existing))
                    return existing;

                var name = $"Set<{element.Name}>";
                CheckFree(typeof(Set<T>), name);
                var type = new SetColumnType<T>(name, element, env, graph.Engine.Counters);
                Add(type);
                AddContainers(env);
                return type;
            }
        }

        /// <summary>
        /// Registers the pair type of <typeparamref name="T1"/> and <typeparamref name="T2"/>, egglog's <c>(sort …
        /// (Pair T1 T2))</c>, named <c>Pair&lt;{first name}, {second name}&gt;</c>, or gets it when it is registered
        /// already. A pair is egglog's <c>Pair</c> container: its cell is a container id from the graph's id counter,
        /// equal pairs have one cell, and its primitives are <see cref="Primitives.PairOf{T1, T2}"/>,
        /// <see cref="Primitives.PairFirst{T1, T2}"/> and <see cref="Primitives.PairSecond{T1, T2}"/>. It extracts as
        /// <c>(pair a b)</c> and costs what its elements cost, as egglog's containers do. An element may be a value, a
        /// class of a sort, or a container of them. A pair that holds classes is egglog's eq-sort container: rebuilt
        /// when its classes merge, never unioned, and made at a writable root and in a writable child, which keeps what
        /// it makes in its own containers until it is disposed (decisions row 9). The first pair type registered is an
        /// engine definition: while the root is writable, that call enters the root's domain, and so does the first
        /// that holds classes; either registers while the root is frozen too, as egglog declares a sort inside a
        /// <c>(push)</c>.
        /// </summary>
        /// <typeparam name="T1">The type of the first element; registered.</typeparam>
        /// <typeparam name="T2">The type of the second element; registered.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">An element type is unknown, or the name is taken.</exception>
        /// <exception cref="InvalidOperationException">The first pair type, or the first that holds classes, is
        /// registered while another thread is using the writable root.</exception>
        public ColumnType<Pair<T1, T2>> RegisterPair<T1, T2>()
            where T1 : notnull
            where T2 : notnull
        {
            EngineCallMarker.ThrowIfActive();
            if (TryGetType<Pair<T1, T2>>(out var existing))
                return existing;

            var first = TypeOf<T1>();
            var second = TypeOf<T2>();

            // Outside the store's lock: the environment's registration takes the graph's definition lock, which
            // DefineSort takes before this lock.
            var env = first.InvolvesClasses || second.InvolvesClasses
                ? graph.ClassContainers<Relations.PairContainer>()
                : graph.ValueContainers<Relations.PairContainer>();
            lock (sync)
            {
                if (TryGetType<Pair<T1, T2>>(out existing))
                    return existing;

                var name = $"Pair<{first.Name}, {second.Name}>";
                CheckFree(typeof(Pair<T1, T2>), name);
                var type = new PairColumnType<T1, T2>(name, first, second, env, graph.Engine.Counters);
                Add(type);
                AddContainers(env);
                return type;
            }
        }

        /// <summary>
        /// Registers the vector type of <typeparamref name="T"/>, egglog's <c>(sort … (Vec T))</c>, named
        /// <c>Vec&lt;{element name}&gt;</c>, or gets it when it is registered already. A vector is egglog's <c>Vec</c>
        /// container: its cell is a container id from the graph's id counter, equal vectors have one cell, and its
        /// primitives are <see cref="Primitives"/>' <c>Vec*</c> methods. It extracts as <c>(vec-of a b)</c>, or
        /// <c>(vec-empty)</c>, and costs what its elements cost, as egglog's containers do. The elements may be values,
        /// containers of values included, classes of a sort, or containers of them. A vector that holds classes is
        /// egglog's eq-sort container: rebuilt when its classes merge, never unioned, and made at a writable root and
        /// in a writable child, which keeps what it makes in its own containers until it is disposed (decisions row 9).
        /// The first vector type registered is an engine definition: while the root is writable, that call enters the
        /// root's domain, and so does the first that holds classes; either registers while the root is frozen too, as
        /// egglog declares a sort inside a <c>(push)</c>.
        /// </summary>
        /// <typeparam name="T">The element type; registered.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">The element type is unknown, or the name is taken.</exception>
        /// <exception cref="InvalidOperationException">The first vector type, or the first that holds classes, is
        /// registered while another thread is using the writable root.</exception>
        public ColumnType<Vec<T>> RegisterVec<T>() where T : notnull
        {
            EngineCallMarker.ThrowIfActive();
            if (TryGetType<Vec<T>>(out var existing))
                return existing;

            var element = TypeOf<T>();
            var env = element.InvolvesClasses
                ? graph.ClassContainers<Relations.VecContainer>()
                : graph.ValueContainers<Relations.VecContainer>();
            lock (sync)
            {
                if (TryGetType<Vec<T>>(out existing))
                    return existing;

                var name = $"Vec<{element.Name}>";
                CheckFree(typeof(Vec<T>), name);
                var type = new VecColumnType<T>(name, element, env, graph.Engine.Counters);
                Add(type);
                AddContainers(env);
                return type;
            }
        }

        /// <summary>
        /// Registers the map type from <typeparamref name="TKey"/> to <typeparamref name="TValue"/>, egglog's <c>(sort
        /// … (Map K V))</c>, named <c>Map&lt;{key name}, {value name}&gt;</c>, or gets it when it is registered
        /// already. A map is egglog's <c>Map</c> container: its cell is a container id from the graph's id counter,
        /// equal maps have one cell, and its primitives are <see cref="Primitives"/>' <c>Map*</c> methods. It extracts
        /// as a chain of <c>map-insert</c>s over <c>(map-empty)</c>, its entries in key-cell order as egglog's, and
        /// costs what its keys and values cost. Keys and values may be values, containers included, classes of a sort,
        /// or containers of them; the keys are <c>string</c>, comparable, or classes, which order by cell, as a
        /// <see cref="Map{TKey, TValue}"/> enumerates in key order. A map that holds classes is egglog's eq-sort
        /// container: rebuilt when its classes merge, keys that become one collapsing as egglog's do, never unioned,
        /// and made at a writable root and in a writable child, which keeps what it makes in its own containers until
        /// it is disposed (decisions row 9). The first map type registered is an engine definition: while the root is
        /// writable, that call enters the root's domain, and so does the first that holds classes; either registers
        /// while the root is frozen too, as egglog declares a sort inside a <c>(push)</c>.
        /// </summary>
        /// <typeparam name="TKey">The key type; registered, and comparable.</typeparam>
        /// <typeparam name="TValue">The value type; registered.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">A type is unknown, the key type is not comparable, or the name is
        /// taken.</exception>
        /// <exception cref="InvalidOperationException">The first map type, or the first that holds classes, is
        /// registered while another thread is using the writable root.</exception>
        public ColumnType<Map<TKey, TValue>> RegisterMap<TKey, TValue>()
            where TKey : notnull
            where TValue : notnull
        {
            EngineCallMarker.ThrowIfActive();
            if (TryGetType<Map<TKey, TValue>>(out var existing))
                return existing;

            var key = TypeOf<TKey>();
            var value = TypeOf<TValue>();
            if (key.IsOrdered == false)
                throw new ArgumentException($"Maps keep their keys in Comparer<T>.Default order, and {DisplayName(typeof(TKey))} implements neither IComparable<{DisplayName(typeof(TKey))}> nor IComparable, or holds an element that does not.");

            var env = key.InvolvesClasses || value.InvolvesClasses
                ? graph.ClassContainers<Relations.MapContainer>()
                : graph.ValueContainers<Relations.MapContainer>();
            lock (sync)
            {
                if (TryGetType<Map<TKey, TValue>>(out existing))
                    return existing;

                var name = $"Map<{key.Name}, {value.Name}>";
                CheckFree(typeof(Map<TKey, TValue>), name);
                var type = new MapColumnType<TKey, TValue>(name, key, value, env, graph.Engine.Counters);
                Add(type);
                AddContainers(env);
                return type;
            }
        }

        /// <summary>
        /// Registers the multiset type of <typeparamref name="T"/>, egglog's <c>(sort … (MultiSet T))</c>, named
        /// <c>MultiSet&lt;{element name}&gt;</c>, or gets it when it is registered already. A multiset is egglog's
        /// <c>MultiSet</c> container: its cell is a container id from the graph's id counter, equal multisets have one
        /// cell, and its primitives are <see cref="Primitives"/>' <c>MultiSet*</c> methods. It extracts as
        /// <c>(multiset-of a a b)</c>, its elements in cell order as egglog's, and costs what its elements cost. The
        /// elements may be values, containers included, classes of a sort, or containers of them, and are
        /// <c>string</c>, comparable, or classes, which order by cell, as a <see cref="MultiSet{T}"/> enumerates in
        /// element order. A multiset of classes is egglog's eq-sort container: rebuilt when its classes merge, elements
        /// that become one adding their counts, never unioned, and made at a writable root and in a writable child,
        /// which keeps what it makes in its own containers until it is disposed (decisions row 9). The first multiset
        /// type registered is an engine definition: while the root is writable, that call enters the root's domain, and
        /// so does the first that holds classes; either registers while the root is frozen too, as egglog declares a
        /// sort inside a <c>(push)</c>.
        /// </summary>
        /// <typeparam name="T">The element type; registered, and comparable.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">The element type is unknown or not comparable, or the name is
        /// taken.</exception>
        /// <exception cref="InvalidOperationException">The first multiset type, or the first that holds classes, is
        /// registered while another thread is using the writable root.</exception>
        public ColumnType<MultiSet<T>> RegisterMultiSet<T>() where T : notnull
        {
            EngineCallMarker.ThrowIfActive();
            if (TryGetType<MultiSet<T>>(out var existing))
                return existing;

            var element = TypeOf<T>();
            if (element.IsOrdered == false)
                throw new ArgumentException($"Multisets keep their elements in Comparer<T>.Default order, and {DisplayName(typeof(T))} implements neither IComparable<{DisplayName(typeof(T))}> nor IComparable, or holds an element that does not.");

            var env = element.InvolvesClasses
                ? graph.ClassContainers<Relations.MultiSetContainer>()
                : graph.ValueContainers<Relations.MultiSetContainer>();
            lock (sync)
            {
                if (TryGetType<MultiSet<T>>(out existing))
                    return existing;

                var name = $"MultiSet<{element.Name}>";
                CheckFree(typeof(MultiSet<T>), name);
                var type = new MultiSetColumnType<T>(name, element, env, graph.Engine.Counters);
                Add(type);
                AddContainers(env);
                return type;
            }
        }

        /// <summary>
        /// Registers the column type of <see cref="UnstableFn{TResult}"/>, egglog's <c>(UnstableFn () TResult)</c>
        /// function sort (src/sort/fn.rs), named <c>UnstableFn&lt;(inputs), output&gt;</c>, or gets it if it is
        /// registered: a function value, interned as a container, its partially applied arguments rebuilt when their
        /// classes merge, never unioned, and, when it holds classes, made at a writable root and in a writable child,
        /// which keeps what it makes in its own containers until it is disposed (decisions row 9). The first function
        /// sort registered is an engine definition: while the root is writable, that call enters the root's domain, and
        /// it registers while the root is frozen too, as egglog declares a sort inside a <c>(push)</c>; a value that
        /// holds classes is a container of classes whenever its sort was registered.
        /// </summary>
        /// <typeparam name="TResult">The type of the output; registered.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">An input or the output type is unknown, or the name is taken.</exception>
        /// <exception cref="InvalidOperationException">The first function sort is registered while another thread is
        /// using the writable root.</exception>
        public ColumnType<UnstableFn<TResult>> RegisterFn<TResult>()
            where TResult : notnull
        {
            EngineCallMarker.ThrowIfActive();
            if (TryGetType<UnstableFn<TResult>>(out var existing))
                return existing;

            var output = TypeOf<TResult>();
            return RegisterFnType<UnstableFn<TResult>>([], output, static content => new UnstableFn<TResult>(content));
        }

        /// <summary>
        /// Registers the column type of <see cref="UnstableFn{T1, TResult}"/>, egglog's <c>(UnstableFn (T1)
        /// TResult)</c> function sort (src/sort/fn.rs), named <c>UnstableFn&lt;(inputs), output&gt;</c>, or gets it if
        /// it is registered: a function value, interned as a container, its partially applied arguments rebuilt when
        /// their classes merge, never unioned, and, when it holds classes, made at a writable root and in a writable
        /// child, which keeps what it makes in its own containers until it is disposed (decisions row 9). The first
        /// function sort registered is an engine definition: while the root is writable, that call enters the root's
        /// domain, and it registers while the root is frozen too, as egglog declares a sort inside a <c>(push)</c>; a
        /// value that holds classes is a container of classes whenever its sort was registered.
        /// </summary>
        /// <typeparam name="T1">The type of input 1; registered.</typeparam>
        /// <typeparam name="TResult">The type of the output; registered.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">An input or the output type is unknown, or the name is taken.</exception>
        /// <exception cref="InvalidOperationException">The first function sort is registered while another thread is
        /// using the writable root.</exception>
        public ColumnType<UnstableFn<T1, TResult>> RegisterFn<T1, TResult>()
            where T1 : notnull
            where TResult : notnull
        {
            EngineCallMarker.ThrowIfActive();
            if (TryGetType<UnstableFn<T1, TResult>>(out var existing))
                return existing;

            var input1 = TypeOf<T1>();
            var output = TypeOf<TResult>();
            return RegisterFnType<UnstableFn<T1, TResult>>([input1], output, static content => new UnstableFn<T1, TResult>(content));
        }

        /// <summary>
        /// Registers the column type of <see cref="UnstableFn{T1, T2, TResult}"/>, egglog's <c>(UnstableFn (T1 T2)
        /// TResult)</c> function sort (src/sort/fn.rs), named <c>UnstableFn&lt;(inputs), output&gt;</c>, or gets it if
        /// it is registered: a function value, interned as a container, its partially applied arguments rebuilt when
        /// their classes merge, never unioned, and, when it holds classes, made at a writable root and in a writable
        /// child, which keeps what it makes in its own containers until it is disposed (decisions row 9). The first
        /// function sort registered is an engine definition: while the root is writable, that call enters the root's
        /// domain, and it registers while the root is frozen too, as egglog declares a sort inside a <c>(push)</c>; a
        /// value that holds classes is a container of classes whenever its sort was registered.
        /// </summary>
        /// <typeparam name="T1">The type of input 1; registered.</typeparam>
        /// <typeparam name="T2">The type of input 2; registered.</typeparam>
        /// <typeparam name="TResult">The type of the output; registered.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">An input or the output type is unknown, or the name is taken.</exception>
        /// <exception cref="InvalidOperationException">The first function sort is registered while another thread is
        /// using the writable root.</exception>
        public ColumnType<UnstableFn<T1, T2, TResult>> RegisterFn<T1, T2, TResult>()
            where T1 : notnull
            where T2 : notnull
            where TResult : notnull
        {
            EngineCallMarker.ThrowIfActive();
            if (TryGetType<UnstableFn<T1, T2, TResult>>(out var existing))
                return existing;

            var input1 = TypeOf<T1>();
            var input2 = TypeOf<T2>();
            var output = TypeOf<TResult>();
            return RegisterFnType<UnstableFn<T1, T2, TResult>>([input1, input2], output, static content => new UnstableFn<T1, T2, TResult>(content));
        }

        /// <summary>
        /// Registers the column type of <see cref="UnstableFn{T1, T2, T3, TResult}"/>, egglog's <c>(UnstableFn (T1 T2
        /// T3) TResult)</c> function sort (src/sort/fn.rs), named <c>UnstableFn&lt;(inputs), output&gt;</c>, or gets it
        /// if it is registered: a function value, interned as a container, its partially applied arguments rebuilt when
        /// their classes merge, never unioned, and, when it holds classes, made at a writable root and in a writable
        /// child, which keeps what it makes in its own containers until it is disposed (decisions row 9). The first
        /// function sort registered is an engine definition: while the root is writable, that call enters the root's
        /// domain, and it registers while the root is frozen too, as egglog declares a sort inside a <c>(push)</c>; a
        /// value that holds classes is a container of classes whenever its sort was registered.
        /// </summary>
        /// <typeparam name="T1">The type of input 1; registered.</typeparam>
        /// <typeparam name="T2">The type of input 2; registered.</typeparam>
        /// <typeparam name="T3">The type of input 3; registered.</typeparam>
        /// <typeparam name="TResult">The type of the output; registered.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">An input or the output type is unknown, or the name is taken.</exception>
        /// <exception cref="InvalidOperationException">The first function sort is registered while another thread is
        /// using the writable root.</exception>
        public ColumnType<UnstableFn<T1, T2, T3, TResult>> RegisterFn<T1, T2, T3, TResult>()
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where TResult : notnull
        {
            EngineCallMarker.ThrowIfActive();
            if (TryGetType<UnstableFn<T1, T2, T3, TResult>>(out var existing))
                return existing;

            var input1 = TypeOf<T1>();
            var input2 = TypeOf<T2>();
            var input3 = TypeOf<T3>();
            var output = TypeOf<TResult>();
            return RegisterFnType<UnstableFn<T1, T2, T3, TResult>>([input1, input2, input3], output, static content => new UnstableFn<T1, T2, T3, TResult>(content));
        }

        /// <summary>
        /// Registers the column type of <see cref="UnstableFn{T1, T2, T3, T4, TResult}"/>, egglog's <c>(UnstableFn (T1
        /// T2 T3 T4) TResult)</c> function sort (src/sort/fn.rs), named <c>UnstableFn&lt;(inputs), output&gt;</c>, or
        /// gets it if it is registered: a function value, interned as a container, its partially applied arguments
        /// rebuilt when their classes merge, never unioned, and, when it holds classes, made at a writable root and in
        /// a writable child, which keeps what it makes in its own containers until it is disposed (decisions row 9).
        /// The first function sort registered is an engine definition: while the root is writable, that call enters the
        /// root's domain, and it registers while the root is frozen too, as egglog declares a sort inside a
        /// <c>(push)</c>; a value that holds classes is a container of classes whenever its sort was registered.
        /// </summary>
        /// <typeparam name="T1">The type of input 1; registered.</typeparam>
        /// <typeparam name="T2">The type of input 2; registered.</typeparam>
        /// <typeparam name="T3">The type of input 3; registered.</typeparam>
        /// <typeparam name="T4">The type of input 4; registered.</typeparam>
        /// <typeparam name="TResult">The type of the output; registered.</typeparam>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentException">An input or the output type is unknown, or the name is taken.</exception>
        /// <exception cref="InvalidOperationException">The first function sort is registered while another thread is
        /// using the writable root.</exception>
        public ColumnType<UnstableFn<T1, T2, T3, T4, TResult>> RegisterFn<T1, T2, T3, T4, TResult>()
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where T4 : notnull
            where TResult : notnull
        {
            EngineCallMarker.ThrowIfActive();
            if (TryGetType<UnstableFn<T1, T2, T3, T4, TResult>>(out var existing))
                return existing;

            var input1 = TypeOf<T1>();
            var input2 = TypeOf<T2>();
            var input3 = TypeOf<T3>();
            var input4 = TypeOf<T4>();
            var output = TypeOf<TResult>();
            return RegisterFnType<UnstableFn<T1, T2, T3, T4, TResult>>([input1, input2, input3, input4], output, static content => new UnstableFn<T1, T2, T3, T4, TResult>(content));
        }

        /// <summary>
        /// Registers a function sort's column type (<see cref="FnColumnType{TFn}"/>) of its inputs and output.
        /// </summary>
        ColumnType<TFn> RegisterFnType<TFn>(ColumnType[] inputs, ColumnType output, Func<FnValue, TFn> wrap)
            where TFn : struct, IFnValue
        {
            // Outside the store's lock, as RegisterPair registers its environment: a value's partially applied arguments
            // may be classes whatever the sort's inputs, so the environment holds ids, as egglog's function sorts' do,
            // whether the root is writable or frozen.
            var env = graph.ClassContainers<Relations.FunctionContainer>();
            lock (sync)
            {
                if (TryGetType<TFn>(out var existing))
                    return existing;

                var name = $"UnstableFn<({string.Join(", ", Array.ConvertAll(inputs, input => input.Name))}), {output.Name}>";
                CheckFree(typeof(TFn), name);
                var type = new FnColumnType<TFn>(name, inputs, output, wrap, env, graph.Engine.Counters, FnTargets,
                    values.GetTypeId<Relations.StringCodec, string>());
                Add(type);
                AddContainers(env);
                return type;
            }
        }

        /// <summary>
        /// Counts a container environment in <see cref="InternedCount"/>, once. Called under the lock.
        /// </summary>
        void AddContainers(Relations.ContainerEnvBase env)
        {
            if (System.Array.IndexOf(containers, env) < 0)
                Volatile.Write(ref containers, [.. containers, env]);
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

                // A map's keys and a multiset's elements order a sort's classes by cell, as egglog's BTreeMap does.
                ElementOrder<TSort>.Use(ClassOrder<TSort>.Instance);
                return type;
            }
        }

        /// <summary>
        /// Gets the method that registers a CLR type: what an unknown-type message tells the caller to call.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>The method, as <c>ValueStore.Register</c>, <c>ValueStore.RegisterSet</c>,
        /// <c>ValueStore.RegisterPair</c>, <c>ValueStore.RegisterVec</c>, <c>ValueStore.RegisterMap</c>,
        /// <c>ValueStore.RegisterMultiSet</c>, <c>ValueStore.RegisterEnum</c> or <c>EGraph.DefineSort</c>.</returns>
        [UnconditionalSuppressMessage("Trimming", "IL2067",
            Justification = "The result only words a hint in an error message. A sort the program defines keeps its ISort<> "
                + "implementation, because EGraph.DefineSort constrains its type argument to it; a type whose implementation the "
                + "trimmer removed is not a defined sort, and the hint then names ValueStore.Register.")]
        internal static string RegisterMethod(Type clrType)
        {
            if (IsSet(clrType))
                return "ValueStore.RegisterSet";

            if (IsPair(clrType))
                return "ValueStore.RegisterPair";

            if (IsVec(clrType))
                return "ValueStore.RegisterVec";

            if (IsMap(clrType))
                return "ValueStore.RegisterMap";

            if (IsMultiSet(clrType))
                return "ValueStore.RegisterMultiSet";

            if (IsFn(clrType))
                return "ValueStore.RegisterFn";

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
        /// Gets whether a CLR type is a set type, a <see cref="Set{T}"/>.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>Whether the type is a set type.</returns>
        internal static bool IsSet(Type clrType) => clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(Set<>);

        /// <summary>
        /// Gets whether a CLR type is a pair type, a <see cref="Pair{T1, T2}"/>.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>Whether the type is a pair type.</returns>
        internal static bool IsPair(Type clrType) => clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(Pair<,>);

        /// <summary>
        /// Gets whether a CLR type is a vector type, a <see cref="Vec{T}"/>.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>Whether the type is a vector type.</returns>
        internal static bool IsVec(Type clrType) => clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(Vec<>);

        /// <summary>
        /// Gets whether a CLR type is a map type, a <see cref="Map{TKey, TValue}"/>.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>Whether the type is a map type.</returns>
        internal static bool IsMap(Type clrType) => clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(Map<,>);

        /// <summary>
        /// Gets whether a CLR type is a multiset type, a <see cref="MultiSet{T}"/>.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>Whether the type is a multiset type.</returns>
        internal static bool IsMultiSet(Type clrType) => clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(MultiSet<>);

        /// <summary>
        /// Gets whether a CLR type is a function sort's value type, an <see cref="UnstableFn{T1, TResult}"/> of any arity.
        /// </summary>
        /// <param name="clrType">The CLR type.</param>
        /// <returns>Whether the type is a function sort's.</returns>
        internal static bool IsFn(Type clrType)
        {
            if (clrType.IsGenericType == false)
                return false;

            var definition = clrType.GetGenericTypeDefinition();
            return definition == typeof(UnstableFn<>) || definition == typeof(UnstableFn<,>) || definition == typeof(UnstableFn<,,>)
                || definition == typeof(UnstableFn<,,,>) || definition == typeof(UnstableFn<,,,,>);
        }

        /// <summary>
        /// Gets a readable name of a CLR type: <c>Foo</c>, <c>Set&lt;Foo&gt;</c>.
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
        /// Formats a <c>BigRat</c> as egglog renders one, <c>(bigrat (from-string "3") (from-string "4"))</c>: its sort
        /// reconstructs a value as that call (<c>src/sort/bigrat.rs</c>, <c>reconstruct_termdag</c>).
        /// </summary>
        internal static string FormatBigRational(BigRational value) =>
            "(bigrat " + FormatBigInteger(value.Numerator) + " " + FormatBigInteger(value.Denominator) + ")";

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
