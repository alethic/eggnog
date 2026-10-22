using System;
using System.Globalization;
using System.Numerics;

namespace Eggnog
{

    /// <summary>
    /// Named built-in primitives, with egglog's names: egglog's <c>i64</c> primitives, over <c>long</c>, and the set
    /// primitives, made once per element type. The other value types' primitives are in <see cref="DoublePrimitives"/>,
    /// <see cref="BigIntegerPrimitives"/>, <see cref="BigRationalPrimitives"/>, <see cref="BooleanPrimitives"/> and
    /// <see cref="StringPrimitives"/>.
    /// </summary>
    /// <remarks>
    /// The <c>i64</c> primitives are partial where egglog's are (<see cref="Primitive.IsPartial"/>): checked arithmetic
    /// has no result on overflow, division and remainder none by zero, shifts none by a count outside 0 to 63, and
    /// <see cref="Log2"/> none for a value below 1, as in egglog 3.0.0. No result drops the match in a rule body and
    /// fails the rule in a head. A partial primitive's <c>Function</c> throws where there is no result, what .NET's own
    /// operation throws: <see cref="OverflowException"/> for an overflow, <see cref="DivideByZeroException"/> for a zero
    /// divisor, and <see cref="InvalidOperationException"/> for a shift count or a <see cref="Log2"/> out of range. The
    /// comparisons return a <c>bool</c>, which is egglog's <c>bool-&lt;</c> family as a value and its <c>&lt;</c> family
    /// as a filter (<see cref="RuleBuilder.Where(Term{bool})"/>), and render as each.
    /// </remarks>
    public static partial class Primitives
    {

        /// <summary>
        /// <c>set-of</c>: the singleton set of a value.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, Set<T>> Singleton<T>() where T : notnull => PrimitiveCache<T>.Singleton;

        /// <summary>
        /// <c>set-of</c> of any number of elements, as egglog's; of none, the empty set, egglog's <c>set-empty</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static VariadicPrimitive<T, Set<T>> SetOf<T>() where T : notnull => PrimitiveCache<T>.SetOf;

        /// <summary>
        /// <c>set-union</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, Set<T>, Set<T>> Union<T>() where T : notnull => PrimitiveCache<T>.Union;

        /// <summary>
        /// <c>set-intersect</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, Set<T>, Set<T>> Intersect<T>() where T : notnull => PrimitiveCache<T>.Intersect;

        /// <summary>
        /// <c>set-diff</c>: the elements of the first set not in the second.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, Set<T>, Set<T>> Except<T>() where T : notnull => PrimitiveCache<T>.Except;

        /// <summary>
        /// <c>set-insert</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, T, Set<T>> Insert<T>() where T : notnull => PrimitiveCache<T>.Insert;

        /// <summary>
        /// <c>set-remove</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, T, Set<T>> Remove<T>() where T : notnull => PrimitiveCache<T>.Remove;

        /// <summary>
        /// <c>set-contains</c>: egglog's filter, which renders so; as a <c>bool</c> value, an addition, it renders as
        /// <c>bool-set-contains</c>, a name egglog does not have, since egglog's <c>set-contains</c> is the unit value or
        /// nothing.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, T, bool> Contains<T>() where T : notnull => PrimitiveCache<T>.Contains;

        /// <summary>
        /// <c>set-not-contains</c>: egglog's filter, which renders so; as a <c>bool</c> value, an addition, it renders as
        /// <c>bool-set-not-contains</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, T, bool> NotContains<T>() where T : notnull => PrimitiveCache<T>.NotContains;

        /// <summary>
        /// <c>set-subset</c>: whether the first set is a subset of the second.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, Set<T>, bool> IsSubsetOf<T>() where T : notnull => PrimitiveCache<T>.IsSubsetOf;

        /// <summary>
        /// <c>set-is-empty</c>: whether the set is empty, an addition: egglog writes <c>(= (set-length s) 0)</c>, and its
        /// <c>set-empty</c> is the empty set, <see cref="Set{T}.Empty"/>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, bool> IsEmpty<T>() where T : notnull => PrimitiveCache<T>.IsEmpty;

        /// <summary>
        /// <c>set-length</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, long> Count<T>() where T : notnull => PrimitiveCache<T>.Count;

        /// <summary>
        /// <c>set-get</c>: the element at an index of the set in cell order, egglog's order of a set (set.rs:122), and no
        /// result for an index out of range. It reads the set's cells: in a run or a write, its container's; in a read,
        /// over a set that may not be interned, an element no write interned comes after every one that has, as egglog's
        /// <c>check</c> would intern it after them, so where two such elements decide the element at the index there is
        /// no result. Its function over values alone throws.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Set<T>, long, T> Get<T>() where T : notnull => PrimitiveCache<T>.Get;

        /// <summary>
        /// <c>bool-!=</c>: whether two values differ, as a <c>bool</c>; as a filter, egglog's <c>!=</c>. Generic over
        /// every type, as egglog's is over every sort: two classes compare by their canonical ids, as egglog compares
        /// representations (classes are also told apart with <see cref="Term{T}.IsNot(Term{T})"/>).
        /// </summary>
        /// <typeparam name="T">The value type or sort.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, T, bool> NotEqual<T>() where T : notnull => PrimitiveCache<T>.NotEqual;

        /// <summary>
        /// <c>pair</c>: the pair of two values, egglog's (src/sort/pair.rs:122-129). Its type is registered with
        /// <see cref="ValueStore.RegisterPair{T1, T2}"/>.
        /// </summary>
        /// <typeparam name="T1">The type of the first element.</typeparam>
        /// <typeparam name="T2">The type of the second element.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, T2, Pair<T1, T2>> PairOf<T1, T2>()
            where T1 : notnull
            where T2 : notnull => PairPrimitiveCache<T1, T2>.Make;

        /// <summary>
        /// <c>pair-first</c>: a pair's first element (pair.rs:131).
        /// </summary>
        /// <typeparam name="T1">The type of the first element.</typeparam>
        /// <typeparam name="T2">The type of the second element.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Pair<T1, T2>, T1> PairFirst<T1, T2>()
            where T1 : notnull
            where T2 : notnull => PairPrimitiveCache<T1, T2>.First;

        /// <summary>
        /// <c>pair-second</c>: a pair's second element (pair.rs:132).
        /// </summary>
        /// <typeparam name="T1">The type of the first element.</typeparam>
        /// <typeparam name="T2">The type of the second element.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Pair<T1, T2>, T2> PairSecond<T1, T2>()
            where T1 : notnull
            where T2 : notnull => PairPrimitiveCache<T1, T2>.Second;

        /// <summary>
        /// <c>vec-empty</c>: the empty vector, egglog's (src/sort/vec.rs:117-120). Its type is registered with
        /// <see cref="ValueStore.RegisterVec{T}"/>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static PrimitiveN<Vec<T>> VecEmpty<T>() where T : notnull => VecPrimitiveCache<T>.Empty;

        /// <summary>
        /// <c>vec-of</c>: the vector of any number of elements, in order (vec.rs:121-124).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static VariadicPrimitive<T, Vec<T>> VecOf<T>() where T : notnull => VecPrimitiveCache<T>.Of;

        /// <summary>
        /// <c>vec-append</c>: the concatenation of any number of vectors (vec.rs:125-128).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static VariadicPrimitive<Vec<T>, Vec<T>> VecAppend<T>() where T : notnull => VecPrimitiveCache<T>.Append;

        /// <summary>
        /// <c>vec-push</c>: the vector with an element appended (vec.rs:130).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Vec<T>, T, Vec<T>> VecPush<T>() where T : notnull => VecPrimitiveCache<T>.Push;

        /// <summary>
        /// <c>vec-pop</c>: the vector without its last element; an empty vector stays empty (vec.rs:131).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Vec<T>, Vec<T>> VecPop<T>() where T : notnull => VecPrimitiveCache<T>.Pop;

        /// <summary>
        /// <c>vec-length</c> (vec.rs:133).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Vec<T>, long> VecLength<T>() where T : notnull => VecPrimitiveCache<T>.Length;

        /// <summary>
        /// <c>vec-contains</c>: whether the vector holds an element, as a <c>bool</c>; as a filter, egglog's
        /// <c>vec-contains</c> (vec.rs:134), and as a value <c>bool-vec-contains</c>, a name egglog has not.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Vec<T>, T, bool> VecContains<T>() where T : notnull => VecPrimitiveCache<T>.Contains;

        /// <summary>
        /// <c>vec-not-contains</c>: whether the vector lacks an element, as a <c>bool</c>; as a filter, egglog's
        /// <c>vec-not-contains</c> (vec.rs:135), and as a value <c>bool-vec-not-contains</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Vec<T>, T, bool> VecNotContains<T>() where T : notnull => VecPrimitiveCache<T>.NotContains;

        /// <summary>
        /// <c>vec-union</c>: unions the classes of two vectors of one length, element by element, and returns the first;
        /// no result for two lengths (vec.rs:140-148, 285-340). A write primitive, as egglog's: it runs as a value a
        /// rule's head or a host write writes, and in a merge expression, such as <c>(vec-union old new)</c>, and not in a
        /// rule's body, a query or a read. Its unions take effect after the fact or the rule's iteration, as egglog's after
        /// the command. It runs only at the root: the classes it unions are held by vectors, which only the root rebuilds
        /// (decisions row 9). Its <c>Function</c> and <c>TryInvoke</c> throw <see cref="InvalidOperationException"/>.
        /// </summary>
        /// <typeparam name="TSort">The sort of the elements.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Vec<TSort>, Vec<TSort>, Vec<TSort>> VecUnion<TSort>()
            where TSort : struct, ISort<TSort> => VecUnionCache<TSort>.Union;

        /// <summary>
        /// <c>vec-get</c>: the element at an index; no result out of range (vec.rs:137).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Vec<T>, long, T> VecGet<T>() where T : notnull => VecPrimitiveCache<T>.Get;

        /// <summary>
        /// <c>vec-set</c>: the vector with the element at an index replaced; no result out of range, as egglog
        /// 3.0.0's, where the pin panics (vec.rs:138).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Vec<T>, long, T, Vec<T>> VecSet<T>() where T : notnull => VecPrimitiveCache<T>.Set;

        /// <summary>
        /// <c>vec-remove</c>: the vector without the element at an index; no result out of range, as egglog
        /// 3.0.0's, where the pin panics (vec.rs:139).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Vec<T>, long, Vec<T>> VecRemove<T>() where T : notnull => VecPrimitiveCache<T>.Remove;

        /// <summary>
        /// <c>map-empty</c>: the empty map, egglog's (src/sort/map.rs:142-146). Its type is registered with
        /// <see cref="ValueStore.RegisterMap{TKey, TValue}"/>.
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <typeparam name="TValue">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static PrimitiveN<Map<TKey, TValue>> MapEmpty<TKey, TValue>()
            where TKey : notnull
            where TValue : notnull => MapPrimitiveCache<TKey, TValue>.Empty;

        /// <summary>
        /// <c>map-insert</c>: the map with a key mapped to a value, replacing any value it had (map.rs:149).
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <typeparam name="TValue">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Map<TKey, TValue>, TKey, TValue, Map<TKey, TValue>> MapInsert<TKey, TValue>()
            where TKey : notnull
            where TValue : notnull => MapPrimitiveCache<TKey, TValue>.Insert;

        /// <summary>
        /// <c>map-get</c>: a key's value; no result for a missing key (map.rs:148).
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <typeparam name="TValue">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Map<TKey, TValue>, TKey, TValue> MapGet<TKey, TValue>()
            where TKey : notnull
            where TValue : notnull => MapPrimitiveCache<TKey, TValue>.Get;

        /// <summary>
        /// <c>map-remove</c>: the map without a key (map.rs:150).
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <typeparam name="TValue">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Map<TKey, TValue>, TKey, Map<TKey, TValue>> MapRemove<TKey, TValue>()
            where TKey : notnull
            where TValue : notnull => MapPrimitiveCache<TKey, TValue>.Remove;

        /// <summary>
        /// <c>map-length</c> (map.rs:152).
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <typeparam name="TValue">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Map<TKey, TValue>, long> MapLength<TKey, TValue>()
            where TKey : notnull
            where TValue : notnull => MapPrimitiveCache<TKey, TValue>.Length;

        /// <summary>
        /// <c>map-contains</c>: whether the map has a key, as a <c>bool</c>; as a filter, egglog's <c>map-contains</c>
        /// (map.rs:153), and as a value <c>bool-map-contains</c>, a name egglog has not.
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <typeparam name="TValue">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Map<TKey, TValue>, TKey, bool> MapContains<TKey, TValue>()
            where TKey : notnull
            where TValue : notnull => MapPrimitiveCache<TKey, TValue>.Contains;

        /// <summary>
        /// <c>map-not-contains</c>: whether the map lacks a key, as a <c>bool</c>; as a filter, egglog's
        /// <c>map-not-contains</c> (map.rs:154), and as a value <c>bool-map-not-contains</c>.
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <typeparam name="TValue">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Map<TKey, TValue>, TKey, bool> MapNotContains<TKey, TValue>()
            where TKey : notnull
            where TValue : notnull => MapPrimitiveCache<TKey, TValue>.NotContains;

        /// <summary>
        /// <c>multiset-of</c>: the multiset of any number of elements, repeats counted (src/sort/multiset.rs:131-134). Its
        /// type is registered with <see cref="ValueStore.RegisterMultiSet{T}"/>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static VariadicPrimitive<T, MultiSet<T>> MultiSetOf<T>() where T : notnull => MultiSetPrimitiveCache<T>.Of;

        /// <summary>
        /// <c>multiset-single</c>: an element repeated a number of times; no result for a negative count
        /// (multiset.rs:136-142).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, long, MultiSet<T>> MultiSetSingle<T>() where T : notnull => MultiSetPrimitiveCache<T>.Single;

        /// <summary>
        /// <c>multiset-pick</c>: the element of least cell, as egglog's picks its <c>BTreeMap</c>'s first key; no result
        /// for an empty multiset, as egglog 3.0.0's, where the pin panics (multiset.rs:143). The cells are the graph's,
        /// so it runs only in a graph: its <c>Function</c> and <c>TryInvoke</c> throw
        /// <see cref="InvalidOperationException"/>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, T> MultiSetPick<T>() where T : notnull => MultiSetPrimitiveCache<T>.Pick;

        /// <summary>
        /// <c>multiset-insert</c>: one more of an element (multiset.rs:144).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, T, MultiSet<T>> MultiSetInsert<T>() where T : notnull => MultiSetPrimitiveCache<T>.Insert;

        /// <summary>
        /// <c>multiset-remove</c>: one fewer of an element; no result when it does not occur (multiset.rs:145).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, T, MultiSet<T>> MultiSetRemove<T>() where T : notnull => MultiSetPrimitiveCache<T>.Remove;

        /// <summary>
        /// <c>multiset-remove-swapped</c>: <c>multiset-remove</c> with its arguments swapped (multiset.rs:146).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, MultiSet<T>, MultiSet<T>> MultiSetRemoveSwapped<T>()
            where T : notnull => MultiSetPrimitiveCache<T>.RemoveSwapped;

        /// <summary>
        /// <c>multiset-subtract</c>: the first minus the second; no result when an element of the second occurs fewer
        /// times in the first (multiset.rs:147).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, MultiSet<T>, MultiSet<T>> MultiSetSubtract<T>()
            where T : notnull => MultiSetPrimitiveCache<T>.Subtract;

        /// <summary>
        /// <c>multiset-subtract-swapped</c>: the second minus the first (multiset.rs:148).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, MultiSet<T>, MultiSet<T>> MultiSetSubtractSwapped<T>()
            where T : notnull => MultiSetPrimitiveCache<T>.SubtractSwapped;

        /// <summary>
        /// <c>multiset-length</c>: the count of elements with their repeats (multiset.rs:149).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, long> MultiSetLength<T>() where T : notnull => MultiSetPrimitiveCache<T>.Length;

        /// <summary>
        /// <c>multiset-contains</c>: whether an element occurs, as a <c>bool</c>; as a filter, egglog's
        /// <c>multiset-contains</c> (multiset.rs:150), and as a value <c>bool-multiset-contains</c>, a name egglog has not.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, T, bool> MultiSetContains<T>() where T : notnull => MultiSetPrimitiveCache<T>.Contains;

        /// <summary>
        /// <c>multiset-not-contains</c>: whether an element does not occur, as a <c>bool</c>; as a filter, egglog's
        /// <c>multiset-not-contains</c> (multiset.rs:151).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, T, bool> MultiSetNotContains<T>() where T : notnull => MultiSetPrimitiveCache<T>.NotContains;

        /// <summary>
        /// <c>multiset-contains-swapped</c>: <c>multiset-contains</c> with its arguments swapped (multiset.rs:152).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, MultiSet<T>, bool> MultiSetContainsSwapped<T>()
            where T : notnull => MultiSetPrimitiveCache<T>.ContainsSwapped;

        /// <summary>
        /// <c>multiset-not-contains-swapped</c>: <c>multiset-not-contains</c> with its arguments swapped
        /// (multiset.rs:153).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, MultiSet<T>, bool> MultiSetNotContainsSwapped<T>()
            where T : notnull => MultiSetPrimitiveCache<T>.NotContainsSwapped;

        /// <summary>
        /// <c>multiset-intersection</c>: each element's lesser count (multiset.rs:154).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, MultiSet<T>, MultiSet<T>> MultiSetIntersection<T>()
            where T : notnull => MultiSetPrimitiveCache<T>.Intersection;

        /// <summary>
        /// <c>multiset-sum</c>: each element's counts added (multiset.rs:155).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, MultiSet<T>, MultiSet<T>> MultiSetSum<T>() where T : notnull => MultiSetPrimitiveCache<T>.Sum;

        /// <summary>
        /// <c>multiset-reset-counts</c>: every count one (multiset.rs:157-163).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, MultiSet<T>> MultiSetResetCounts<T>()
            where T : notnull => MultiSetPrimitiveCache<T>.ResetCounts;

        /// <summary>
        /// <c>multiset-union-values</c>: unions every element of a multiset of classes with its first, the element of
        /// least cell, and returns that; no result for an empty multiset (multiset.rs:198-208, 785-832). A write
        /// primitive, as <see cref="VecUnion{TSort}"/> is: it runs as a value a rule's head or a host write writes, and in
        /// a merge expression, only at the root, its unions taking effect after the fact or the rule's iteration.
        /// </summary>
        /// <typeparam name="TSort">The sort of the elements.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<TSort>, TSort> MultiSetUnionValues<TSort>()
            where TSort : struct, ISort<TSort> => MultiSetUnionValuesCache<TSort>.UnionValues;

        /// <summary>
        /// <c>multiset-pick-max</c>: an element of greatest count, of those the one of greatest cell, as Rust's
        /// <c>max_by_key</c> keeps the last maximum; no result for an empty multiset (multiset.rs:164-166). It runs
        /// only in a graph, as <see cref="MultiSetPick{T}"/> does.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, T> MultiSetPickMax<T>() where T : notnull => MultiSetPrimitiveCache<T>.PickMax;

        /// <summary>
        /// <c>multiset-count</c>: an element's count, 0 when it does not occur (multiset.rs:167-169).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<T>, T, long> MultiSetCount<T>() where T : notnull => MultiSetPrimitiveCache<T>.Count;

        /// <summary>
        /// <c>multiset-sum-multisets</c>: the sum of a multiset of multisets, each counted as often as it occurs, which
        /// egglog registers for multisets of multisets (multiset.rs:171-185, 665-719).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<MultiSet<MultiSet<T>>, MultiSet<T>> MultiSetSumMultiSets<T>()
            where T : notnull => MultiSetPrimitiveCache<T>.SumMultiSets;

        /// <summary>
        /// <c>vec-range</c>: the vector <c>0, 1, …, end - 1</c>, empty for a negative end (vec.rs:151-161), which egglog
        /// registers for vectors of <c>i64</c> only.
        /// </summary>
        public static Primitive<long, Vec<long>> VecRange { get; } = BuiltInPrimitive.Total<long, Vec<long>>("vec-range", static end =>
        {
            var items = new long[end > 0 ? checked((int)end) : 0];
            for (var i = 0; i < items.Length; i++)
                items[i] = i;

            return Vec.Of(items);
        });

        /// <summary>
        /// <c>ordering-min</c>: of two values, the one whose cell is the smaller, as egglog's compares the values'
        /// representations. A cell is a small integer itself, or an intern index, as egglog's representation is, so the
        /// order is that of interning, not of the values. It runs only in a rule: its <c>Function</c> throws
        /// <see cref="InvalidOperationException"/>, since the cells are the graph's.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, T, T> OrderingMin<T>() where T : notnull => PrimitiveCache<T>.OrderingMin;

        /// <summary>
        /// <c>ordering-max</c>: of two values, the one whose cell is the larger (<see cref="OrderingMin{T}"/>).
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, T, T> OrderingMax<T>() where T : notnull => PrimitiveCache<T>.OrderingMax;

        /// <summary>
        /// Gets <c>+</c> over <c>i64</c>: partial, like egglog's (<c>checked_add</c>). An overflow has no result: in a
        /// rule body the match is dropped; in a head the rule fails.
        /// <see cref="Primitive{T1, T2, TResult}.Function"/> throws <see cref="OverflowException"/>.
        /// </summary>
        public static Primitive<long, long, long> Add { get; } = new("+", static (a, b) => checked(a + b), TryAdd);

        /// <summary>
        /// Gets <c>-</c> over <c>i64</c>: partial, like egglog's (<c>checked_sub</c>). An overflow has no result: in a
        /// rule body the match is dropped; in a head the rule fails.
        /// <see cref="Primitive{T1, T2, TResult}.Function"/> throws <see cref="OverflowException"/>.
        /// </summary>
        public static Primitive<long, long, long> Subtract { get; } = new("-", static (a, b) => checked(a - b), TrySubtract);

        /// <summary>
        /// Gets <c>*</c> over <c>i64</c>: partial, like egglog's (<c>checked_mul</c>); an overflow has no result.
        /// </summary>
        public static Primitive<long, long, long> Multiply { get; } =
            BuiltInPrimitive.Partial<long, long, long>("*", static (a, b) => checked(a * b), TryMultiply);

        /// <summary>
        /// Gets <c>/</c> over <c>i64</c>, truncating toward zero: partial, like egglog's (<c>checked_div</c>); a zero
        /// divisor, or <c>long.MinValue / -1</c>, has no result.
        /// </summary>
        public static Primitive<long, long, long> Divide { get; } =
            BuiltInPrimitive.Partial<long, long, long>("/", static (a, b) => CheckDivision(a, b) / b, TryDivide);

        /// <summary>
        /// Gets <c>%</c> over <c>i64</c>, with the dividend's sign: partial, like egglog's (<c>checked_rem</c>); a zero
        /// divisor, or <c>long.MinValue % -1</c>, has no result.
        /// </summary>
        public static Primitive<long, long, long> Remainder { get; } =
            BuiltInPrimitive.Partial<long, long, long>("%", static (a, b) => CheckDivision(a, b) % b, TryRemainder);

        /// <summary>
        /// Gets <c>&amp;</c> over <c>i64</c>: bitwise and.
        /// </summary>
        public static Primitive<long, long, long> BitwiseAnd { get; } = BuiltInPrimitive.Total<long, long, long>("&", static (a, b) => a & b);

        /// <summary>
        /// Gets <c>|</c> over <c>i64</c>: bitwise or.
        /// </summary>
        public static Primitive<long, long, long> BitwiseOr { get; } = BuiltInPrimitive.Total<long, long, long>("|", static (a, b) => a | b);

        /// <summary>
        /// Gets <c>^</c> over <c>i64</c>: bitwise exclusive or (not a power, which <c>^</c> is over <c>f64</c>).
        /// </summary>
        public static Primitive<long, long, long> BitwiseXor { get; } = BuiltInPrimitive.Total<long, long, long>("^", static (a, b) => a ^ b);

        /// <summary>
        /// Gets <c>&lt;&lt;</c> over <c>i64</c>: partial, like egglog's (<c>checked_shl</c>); a count outside 0 to 63 has
        /// no result. Bits shifted out are lost, as in egglog.
        /// </summary>
        public static Primitive<long, long, long> ShiftLeft { get; } = BuiltInPrimitive.Partial<long, long, long>("<<", TryShiftLeft);

        /// <summary>
        /// Gets <c>&gt;&gt;</c> over <c>i64</c>, an arithmetic shift: partial, like egglog's (<c>checked_shr</c>); a count
        /// outside 0 to 63 has no result.
        /// </summary>
        public static Primitive<long, long, long> ShiftRight { get; } = BuiltInPrimitive.Partial<long, long, long>(">>", TryShiftRight);

        /// <summary>
        /// Gets <c>not-i64</c>: bitwise complement.
        /// </summary>
        public static Primitive<long, long> BitwiseNot { get; } = BuiltInPrimitive.Total<long, long>("not-i64", static a => ~a);

        /// <summary>
        /// Gets <c>log2</c> over <c>i64</c>: the floor of the base-2 logarithm. Partial, like egglog 3.0.0's
        /// (<c>checked_ilog2</c>): a value below 1 has no result.
        /// </summary>
        public static Primitive<long, long> Log2 { get; } = BuiltInPrimitive.Partial<long, long>("log2", TryLog2);

        /// <summary>
        /// Gets <c>abs</c> over <c>i64</c>: partial, like egglog's (<c>checked_abs</c>); <c>long.MinValue</c> has no
        /// result.
        /// </summary>
        public static Primitive<long, long> Abs { get; } = BuiltInPrimitive.Partial<long, long>("abs", static a => Math.Abs(a), TryAbs);

        /// <summary>
        /// Gets <c>max</c> over <c>i64</c>.
        /// </summary>
        public static Primitive<long, long, long> Max { get; } = new("max", static (a, b) => Math.Max(a, b), false);

        /// <summary>
        /// Gets <c>min</c> over <c>i64</c>.
        /// </summary>
        public static Primitive<long, long, long> Min { get; } = new("min", static (a, b) => Math.Min(a, b), false);

        /// <summary>
        /// Gets <c>bool-=</c> over <c>i64</c>: whether the two are equal, as a <c>bool</c>.
        /// </summary>
        public static Primitive<long, long, bool> Equal { get; } = BuiltInPrimitive.Total<long, long, bool>("bool-=", static (a, b) => a == b);

        /// <summary>
        /// Gets <c>bool-&lt;</c> over <c>i64</c>, whose filter is egglog's <c>&lt;</c>.
        /// </summary>
        public static Primitive<long, long, bool> Less { get; } = BuiltInPrimitive.Comparison<long>("<", "bool-<", static (a, b) => a < b);

        /// <summary>
        /// Gets <c>bool-&lt;=</c> over <c>i64</c>, whose filter is egglog's <c>&lt;=</c>.
        /// </summary>
        public static Primitive<long, long, bool> LessOrEqual { get; } = BuiltInPrimitive.Comparison<long>("<=", "bool-<=", static (a, b) => a <= b);

        /// <summary>
        /// Gets <c>bool-&gt;</c> over <c>i64</c>, whose filter is egglog's <c>&gt;</c>.
        /// </summary>
        public static Primitive<long, long, bool> Greater { get; } = BuiltInPrimitive.Comparison<long>(">", "bool->", static (a, b) => a > b);

        /// <summary>
        /// Gets <c>bool-&gt;=</c> over <c>i64</c>, whose filter is egglog's <c>&gt;=</c>.
        /// </summary>
        public static Primitive<long, long, bool> GreaterOrEqual { get; } = BuiltInPrimitive.Comparison<long>(">=", "bool->=", static (a, b) => a >= b);

        /// <summary>
        /// Gets <c>to-f64</c>: the nearest <c>double</c>, as Rust's <c>as f64</c>.
        /// </summary>
        public static Primitive<long, double> ToDouble { get; } = BuiltInPrimitive.Total<long, double>("to-f64", static a => a);

        /// <summary>
        /// Gets <c>to-string</c> over <c>i64</c>: the decimal digits, with a leading <c>-</c> when negative.
        /// </summary>
        public static Primitive<long, string> Format { get; } =
            BuiltInPrimitive.Total<long, string>("to-string", static a => a.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Gets the internal <c>is-true</c> filter a rule body applies to a <see cref="Fact.True"/> condition that is
        /// not a computation.
        /// </summary>
        internal static Primitive<bool, bool> IsTrue { get; } = new("is-true", static b => b, false);

        /// <summary>
        /// <c>checked_add</c>: no result on overflow.
        /// </summary>
        static bool TryAdd(long a, long b, out long result)
        {
            result = unchecked(a + b);
            return ((a ^ result) & (b ^ result)) >= 0;
        }

        /// <summary>
        /// <c>checked_sub</c>: no result on overflow.
        /// </summary>
        static bool TrySubtract(long a, long b, out long result)
        {
            result = unchecked(a - b);
            return ((a ^ b) & (a ^ result)) >= 0;
        }

        /// <summary>
        /// <c>checked_mul</c>: no result when the 128-bit product does not fit.
        /// </summary>
        static bool TryMultiply(long a, long b, out long result)
        {
            var high = Math.BigMul(a, b, out result);
            return high == result >> 63;
        }

        /// <summary>
        /// The dividend of a division whose function throws where <c>checked_div</c> and <c>checked_rem</c> have no
        /// result: <see cref="DivideByZeroException"/> for a zero divisor, <see cref="OverflowException"/> for
        /// <c>long.MinValue</c> by -1, which .NET's remainder does not refuse.
        /// </summary>
        static long CheckDivision(long a, long b)
        {
            if (b == 0)
                throw new DivideByZeroException();

            if (a == long.MinValue && b == -1)
                throw new OverflowException("long.MinValue divided by -1 overflows.");

            return a;
        }

        /// <summary>
        /// <c>checked_div</c>: no result for a zero divisor or <c>long.MinValue / -1</c>.
        /// </summary>
        static bool TryDivide(long a, long b, out long result)
        {
            if (b == 0 || (a == long.MinValue && b == -1))
            {
                result = 0;
                return false;
            }

            result = a / b;
            return true;
        }

        /// <summary>
        /// <c>checked_rem</c>: no result for a zero divisor or <c>long.MinValue % -1</c>.
        /// </summary>
        static bool TryRemainder(long a, long b, out long result)
        {
            if (b == 0 || (a == long.MinValue && b == -1))
            {
                result = 0;
                return false;
            }

            result = a % b;
            return true;
        }

        /// <summary>
        /// egglog's <c>&lt;&lt;</c>: the count converts to <c>u32</c> and <c>checked_shl</c> takes it below 64.
        /// </summary>
        static bool TryShiftLeft(long a, long b, out long result)
        {
            if (b < 0 || b >= 64)
            {
                result = 0;
                return false;
            }

            result = a << (int)b;
            return true;
        }

        /// <summary>
        /// egglog's <c>&gt;&gt;</c>: the count converts to <c>u32</c> and <c>checked_shr</c> takes it below 64.
        /// </summary>
        static bool TryShiftRight(long a, long b, out long result)
        {
            if (b < 0 || b >= 64)
            {
                result = 0;
                return false;
            }

            result = a >> (int)b;
            return true;
        }

        /// <summary>
        /// <c>checked_ilog2</c>: no result below 1.
        /// </summary>
        static bool TryLog2(long a, out long result)
        {
            if (a <= 0)
            {
                result = 0;
                return false;
            }

            result = 63 - BitOperations.LeadingZeroCount((ulong)a);
            return true;
        }

        /// <summary>
        /// <c>checked_abs</c>: no result for <c>long.MinValue</c>.
        /// </summary>
        static bool TryAbs(long a, out long result)
        {
            if (a == long.MinValue)
            {
                result = 0;
                return false;
            }

            result = Math.Abs(a);
            return true;
        }

    }

}
