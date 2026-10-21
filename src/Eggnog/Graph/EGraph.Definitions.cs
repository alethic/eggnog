using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace Eggnog
{

    public sealed partial class EGraph
    {

        static readonly TableOptions DefaultTableOptions = new();

        readonly Dictionary<string, TableHandle> tablesByName = new(StringComparer.Ordinal);
        readonly Dictionary<string, Ruleset> rulesetsByName = new(StringComparer.Ordinal);
        TableHandle[] tables = Array.Empty<TableHandle>();
        ReadOnlyCollection<TableHandle> tablesView = Array.AsReadOnly(Array.Empty<TableHandle>());
        TableHandle?[] tablesByFunction = Array.Empty<TableHandle?>();
        Ruleset[] rulesets = Array.Empty<Ruleset>();
        ReadOnlyCollection<Ruleset> rulesetsView = Array.AsReadOnly(Array.Empty<Ruleset>());

        /// <summary>
        /// Registers <typeparamref name="TSort"/> as an e-class sort.
        /// </summary>
        /// <typeparam name="TSort">The sort: a struct wrapping a <see cref="ClassId"/>.</typeparam>
        /// <param name="name">The sort's name, unique among the graph's column types.</param>
        /// <returns>The sort's column type.</returns>
        /// <exception cref="ArgumentException"><typeparamref name="TSort"/> or <paramref name="name"/> is taken, or the name
        /// is empty.</exception>
        public ColumnType<TSort> DefineSort<TSort>(string name) where TSort : struct, ISort<TSort>
        {
            EngineCallMarker.ThrowIfActive();
            NameRules.ValidateName(name, nameof(name));
            lock (DefinitionLock)
                return Values.AddSort<TSort>(name);
        }

        /// <summary>
        /// Defines a nullary relation: a fact that holds or not.
        /// </summary>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="options">The role, lift policy and subsumability; <c>null</c> for a fact.</param>
        /// <returns>The relation.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, or the role is <see cref="TableRole.Term"/>.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Relation DefineRelation(string name, TableOptions? options = null) =>
            DefineTable(static () => new Relation(), name, TableKind.Relation, Array.Empty<ColumnType>(), null, options, null, 0);

        /// <summary>
        /// Defines a relation over one column.
        /// </summary>
        /// <typeparam name="T1">The column's type: a registered value type or a defined sort.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="options">The role, lift policy and subsumability; <c>null</c> for a fact.</param>
        /// <returns>The relation.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, or the role is
        /// <see cref="TableRole.Term"/>.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Relation<T1> DefineRelation<T1>(string name, TableOptions? options = null) =>
            DefineTable(static () => new Relation<T1>(), name, TableKind.Relation, new ColumnType[] { Values.TypeOf<T1>() }, null, options, null, 0);

        /// <summary>
        /// Defines a relation over two columns.
        /// </summary>
        /// <typeparam name="T1">The first column's type.</typeparam>
        /// <typeparam name="T2">The second column's type.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="options">The role, lift policy and subsumability; <c>null</c> for a fact.</param>
        /// <returns>The relation.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, or the role is
        /// <see cref="TableRole.Term"/>.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Relation<T1, T2> DefineRelation<T1, T2>(string name, TableOptions? options = null) =>
            DefineTable(static () => new Relation<T1, T2>(), name, TableKind.Relation, new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>() }, null, options, null, 0);

        /// <summary>
        /// Defines a relation over three columns.
        /// </summary>
        /// <typeparam name="T1">The first column's type.</typeparam>
        /// <typeparam name="T2">The second column's type.</typeparam>
        /// <typeparam name="T3">The third column's type.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="options">The role, lift policy and subsumability; <c>null</c> for a fact.</param>
        /// <returns>The relation.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, or the role is
        /// <see cref="TableRole.Term"/>.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Relation<T1, T2, T3> DefineRelation<T1, T2, T3>(string name, TableOptions? options = null) =>
            DefineTable(static () => new Relation<T1, T2, T3>(), name, TableKind.Relation,
                new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>(), Values.TypeOf<T3>() }, null, options, null, 0);

        /// <summary>
        /// Defines a relation over four columns.
        /// </summary>
        /// <typeparam name="T1">The first column's type.</typeparam>
        /// <typeparam name="T2">The second column's type.</typeparam>
        /// <typeparam name="T3">The third column's type.</typeparam>
        /// <typeparam name="T4">The fourth column's type.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="options">The role, lift policy and subsumability; <c>null</c> for a fact.</param>
        /// <returns>The relation.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, or the role is
        /// <see cref="TableRole.Term"/>.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Relation<T1, T2, T3, T4> DefineRelation<T1, T2, T3, T4>(string name, TableOptions? options = null) =>
            DefineTable(static () => new Relation<T1, T2, T3, T4>(), name, TableKind.Relation,
                new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>(), Values.TypeOf<T3>(), Values.TypeOf<T4>() }, null, options, null, 0);

        /// <summary>
        /// Defines a nullary function. With no lattice, two different values are an error: a host
        /// write throws <see cref="FactConflictException"/> and writes nothing, and a rule's write fails its run with
        /// <see cref="RuleEvaluationException"/>, faulting the scope.
        /// </summary>
        /// <typeparam name="TOut">The output type: a value type, or a sort, whose classes the function holds without
        /// making terms (egglog's <c>function</c> of an e-class sort).</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="lattice">The merge of two values, not of classes; <c>null</c> for none.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact. A function cannot be
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, a lattice is given for a
        /// sort output or one with a case join for a lifted container output, the role is <see cref="TableRole.Term"/>, or
        /// the options make it subsumable.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Function<TOut> DefineFunction<TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null) =>
            DefineTable(static () => new Function<TOut>(), name, TableKind.Function, Array.Empty<ColumnType>(), Values.TypeOf<TOut>(), options, lattice, 0);

        /// <summary>
        /// Defines a function of one key. With no lattice, two different values are an error: a
        /// host write throws <see cref="FactConflictException"/> and writes nothing, and a rule's write fails its run
        /// with <see cref="RuleEvaluationException"/>, faulting the scope.
        /// </summary>
        /// <typeparam name="T1">The key's type.</typeparam>
        /// <typeparam name="TOut">The output type: a value type, or a sort, whose classes the function holds without
        /// making terms (egglog's <c>function</c> of an e-class sort).</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="lattice">The merge of two values, not of classes; <c>null</c> for none.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact. A function cannot be
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, a lattice is given for a
        /// sort output or one with a case join for a lifted container output, the role is <see cref="TableRole.Term"/>, or
        /// the options make it subsumable.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Function<T1, TOut> DefineFunction<T1, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null) =>
            DefineTable(static () => new Function<T1, TOut>(), name, TableKind.Function, new ColumnType[] { Values.TypeOf<T1>() }, Values.TypeOf<TOut>(), options, lattice, 0);

        /// <summary>
        /// Defines a function of two keys. With no lattice, two different values are an error: a
        /// host write throws <see cref="FactConflictException"/> and writes nothing, and a rule's write fails its run
        /// with <see cref="RuleEvaluationException"/>, faulting the scope.
        /// </summary>
        /// <typeparam name="T1">The first key's type.</typeparam>
        /// <typeparam name="T2">The second key's type.</typeparam>
        /// <typeparam name="TOut">The output type: a value type, or a sort, whose classes the function holds without
        /// making terms (egglog's <c>function</c> of an e-class sort).</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="lattice">The merge of two values, not of classes; <c>null</c> for none.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact. A function cannot be
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, a lattice is given for a
        /// sort output or one with a case join for a lifted container output, the role is <see cref="TableRole.Term"/>, or
        /// the options make it subsumable.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Function<T1, T2, TOut> DefineFunction<T1, T2, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null) =>
            DefineTable(static () => new Function<T1, T2, TOut>(), name, TableKind.Function,
                new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>() }, Values.TypeOf<TOut>(), options, lattice, 0);

        /// <summary>
        /// Defines a function of three keys. With no lattice, two different values are an error: a
        /// host write throws <see cref="FactConflictException"/> and writes nothing, and a rule's write fails its run
        /// with <see cref="RuleEvaluationException"/>, faulting the scope.
        /// </summary>
        /// <typeparam name="T1">The first key's type.</typeparam>
        /// <typeparam name="T2">The second key's type.</typeparam>
        /// <typeparam name="T3">The third key's type.</typeparam>
        /// <typeparam name="TOut">The output type: a value type, or a sort, whose classes the function holds without
        /// making terms (egglog's <c>function</c> of an e-class sort).</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="lattice">The merge of two values, not of classes; <c>null</c> for none.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact. A function cannot be
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, a lattice is given for a
        /// sort output or one with a case join for a lifted container output, the role is <see cref="TableRole.Term"/>, or
        /// the options make it subsumable.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Function<T1, T2, T3, TOut> DefineFunction<T1, T2, T3, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null) =>
            DefineTable(static () => new Function<T1, T2, T3, TOut>(), name, TableKind.Function,
                new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>(), Values.TypeOf<T3>() }, Values.TypeOf<TOut>(), options, lattice, 0);

        /// <summary>
        /// Defines a function of four keys. With no lattice, two different values are an error: a
        /// host write throws <see cref="FactConflictException"/> and writes nothing, and a rule's write fails its run
        /// with <see cref="RuleEvaluationException"/>, faulting the scope.
        /// </summary>
        /// <typeparam name="T1">The first key's type.</typeparam>
        /// <typeparam name="T2">The second key's type.</typeparam>
        /// <typeparam name="T3">The third key's type.</typeparam>
        /// <typeparam name="T4">The fourth key's type.</typeparam>
        /// <typeparam name="TOut">The output type: a value type, or a sort, whose classes the function holds without
        /// making terms (egglog's <c>function</c> of an e-class sort).</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="lattice">The merge of two values, not of classes; <c>null</c> for none.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact. A function cannot be
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, a lattice is given for a
        /// sort output or one with a case join for a lifted container output, the role is <see cref="TableRole.Term"/>, or
        /// the options make it subsumable.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Function<T1, T2, T3, T4, TOut> DefineFunction<T1, T2, T3, T4, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null) =>
            DefineTable(static () => new Function<T1, T2, T3, T4, TOut>(), name, TableKind.Function,
                new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>(), Values.TypeOf<T3>(), Values.TypeOf<T4>() }, Values.TypeOf<TOut>(), options, lattice, 0);

        /// <summary>
        /// Defines a nullary function whose merge is not a lattice, egglog's <c>:merge new</c>, <c>:merge old</c> or
        /// <c>:merge (f old new)</c>, or any expression over <c>old</c> and <c>new</c> (<see cref="FunctionMerge"/>): a second value for a key is merged with the first, in
        /// the order the writes come. Only the root writes it: a write from a child scope is refused.
        /// </summary>
        /// <typeparam name="TOut">The output type: a value type, or a sort, whose classes the function holds without
        /// making terms (egglog's <c>function</c> of an e-class sort).</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="merge">The merge of the old and the new value.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact. A function cannot be
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, the role is
        /// <see cref="TableRole.Term"/>, the options make it subsumable, the merge is a default value, or the output is
        /// a sort and the merge is a function of values (<see cref="FunctionMerge.Of{T}"/>; <c>New</c>, <c>Old</c> and
        /// an expression merge classes).</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Function<TOut> DefineFunction<TOut>(string name, FunctionMerge<TOut> merge, TableOptions? options = null) =>
            DefineTable(static () => new Function<TOut>(), name, TableKind.Function, Array.Empty<ColumnType>(), Values.TypeOf<TOut>(), options, null, 0,
                merge: merge.Definition);

        /// <summary>
        /// Defines a function of one key whose merge is not a lattice, egglog's <c>:merge new</c>, <c>:merge old</c> or
        /// <c>:merge (f old new)</c>, or any expression over <c>old</c> and <c>new</c> (<see cref="FunctionMerge"/>): a second value for a key is merged with the first, in
        /// the order the writes come. Only the root writes it: a write from a child scope is refused.
        /// </summary>
        /// <typeparam name="T1">The key's type.</typeparam>
        /// <typeparam name="TOut">The output type: a value type, or a sort, whose classes the function holds without
        /// making terms (egglog's <c>function</c> of an e-class sort).</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="merge">The merge of the old and the new value.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact. A function cannot be
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, the role is
        /// <see cref="TableRole.Term"/>, the options make it subsumable, the merge is a default value, or the output is
        /// a sort and the merge is a function of values (<see cref="FunctionMerge.Of{T}"/>; <c>New</c>, <c>Old</c> and
        /// an expression merge classes).</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Function<T1, TOut> DefineFunction<T1, TOut>(string name, FunctionMerge<TOut> merge, TableOptions? options = null) =>
            DefineTable(static () => new Function<T1, TOut>(), name, TableKind.Function, new ColumnType[] { Values.TypeOf<T1>() }, Values.TypeOf<TOut>(), options, null, 0,
                merge: merge.Definition);

        /// <summary>
        /// Defines a function of two keys whose merge is not a lattice, egglog's <c>:merge new</c>, <c>:merge old</c> or
        /// <c>:merge (f old new)</c>, or any expression over <c>old</c> and <c>new</c> (<see cref="FunctionMerge"/>): a second value for a key is merged with the first, in
        /// the order the writes come. Only the root writes it: a write from a child scope is refused.
        /// </summary>
        /// <typeparam name="T1">The first key's type.</typeparam>
        /// <typeparam name="T2">The second key's type.</typeparam>
        /// <typeparam name="TOut">The output type: a value type, or a sort, whose classes the function holds without
        /// making terms (egglog's <c>function</c> of an e-class sort).</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="merge">The merge of the old and the new value.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact. A function cannot be
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, the role is
        /// <see cref="TableRole.Term"/>, the options make it subsumable, the merge is a default value, or the output is
        /// a sort and the merge is a function of values (<see cref="FunctionMerge.Of{T}"/>; <c>New</c>, <c>Old</c> and
        /// an expression merge classes).</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Function<T1, T2, TOut> DefineFunction<T1, T2, TOut>(string name, FunctionMerge<TOut> merge, TableOptions? options = null) =>
            DefineTable(static () => new Function<T1, T2, TOut>(), name, TableKind.Function, new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>() }, Values.TypeOf<TOut>(), options, null, 0,
                merge: merge.Definition);

        /// <summary>
        /// Defines a function of three keys whose merge is not a lattice, egglog's <c>:merge new</c>, <c>:merge old</c> or
        /// <c>:merge (f old new)</c>, or any expression over <c>old</c> and <c>new</c> (<see cref="FunctionMerge"/>): a second value for a key is merged with the first, in
        /// the order the writes come. Only the root writes it: a write from a child scope is refused.
        /// </summary>
        /// <typeparam name="T1">The first key's type.</typeparam>
        /// <typeparam name="T2">The second key's type.</typeparam>
        /// <typeparam name="T3">The third key's type.</typeparam>
        /// <typeparam name="TOut">The output type: a value type, or a sort, whose classes the function holds without
        /// making terms (egglog's <c>function</c> of an e-class sort).</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="merge">The merge of the old and the new value.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact. A function cannot be
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, the role is
        /// <see cref="TableRole.Term"/>, the options make it subsumable, the merge is a default value, or the output is
        /// a sort and the merge is a function of values (<see cref="FunctionMerge.Of{T}"/>; <c>New</c>, <c>Old</c> and
        /// an expression merge classes).</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Function<T1, T2, T3, TOut> DefineFunction<T1, T2, T3, TOut>(string name, FunctionMerge<TOut> merge, TableOptions? options = null) =>
            DefineTable(static () => new Function<T1, T2, T3, TOut>(), name, TableKind.Function, new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>(), Values.TypeOf<T3>() }, Values.TypeOf<TOut>(), options, null, 0,
                merge: merge.Definition);

        /// <summary>
        /// Defines a function of four keys whose merge is not a lattice, egglog's <c>:merge new</c>, <c>:merge old</c> or
        /// <c>:merge (f old new)</c>, or any expression over <c>old</c> and <c>new</c> (<see cref="FunctionMerge"/>): a second value for a key is merged with the first, in
        /// the order the writes come. Only the root writes it: a write from a child scope is refused.
        /// </summary>
        /// <typeparam name="T1">The first key's type.</typeparam>
        /// <typeparam name="T2">The second key's type.</typeparam>
        /// <typeparam name="T3">The third key's type.</typeparam>
        /// <typeparam name="T4">The fourth key's type.</typeparam>
        /// <typeparam name="TOut">The output type: a value type, or a sort, whose classes the function holds without
        /// making terms (egglog's <c>function</c> of an e-class sort).</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="merge">The merge of the old and the new value.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact. A function cannot be
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, the role is
        /// <see cref="TableRole.Term"/>, the options make it subsumable, the merge is a default value, or the output is
        /// a sort and the merge is a function of values (<see cref="FunctionMerge.Of{T}"/>; <c>New</c>, <c>Old</c> and
        /// an expression merge classes).</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Function<T1, T2, T3, T4, TOut> DefineFunction<T1, T2, T3, T4, TOut>(string name, FunctionMerge<TOut> merge, TableOptions? options = null) =>
            DefineTable(static () => new Function<T1, T2, T3, T4, TOut>(), name, TableKind.Function, new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>(), Values.TypeOf<T3>(), Values.TypeOf<T4>() }, Values.TypeOf<TOut>(), options, null, 0,
                merge: merge.Definition);

        /// <summary>
        /// Defines a nullary constructor (a term head).
        /// </summary>
        /// <typeparam name="TSort">The sort of its terms, defined with <see cref="DefineSort{TSort}(string)"/>.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="cost">The cost of a node with this head, at least 0.</param>
        /// <param name="options">Whether extraction may use it, whether it is subsumable, and the lift policy; the role
        /// is always a term.</param>
        /// <returns>The constructor.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, or a type is unknown.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Constructor<TSort> DefineConstructor<TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort> =>
            DefineTable(static () => new Constructor<TSort>(), name, TableKind.Constructor, Array.Empty<ColumnType>(), Values.TypeOf<TSort>(), options, null, cost);

        /// <summary>
        /// Defines a constructor of one key (a term head).
        /// </summary>
        /// <typeparam name="T1">The key's type.</typeparam>
        /// <typeparam name="TSort">The sort of its terms, defined with <see cref="DefineSort{TSort}(string)"/>.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="cost">The cost of a node with this head, at least 0.</param>
        /// <param name="options">Whether extraction may use it, whether it is subsumable, and the lift policy; the role
        /// is always a term.</param>
        /// <returns>The constructor.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, or a type is unknown.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Constructor<T1, TSort> DefineConstructor<T1, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort> =>
            DefineTable(static () => new Constructor<T1, TSort>(), name, TableKind.Constructor, new ColumnType[] { Values.TypeOf<T1>() }, Values.TypeOf<TSort>(), options, null, cost);

        /// <summary>
        /// Defines a constructor of two keys (a term head).
        /// </summary>
        /// <typeparam name="T1">The first key's type.</typeparam>
        /// <typeparam name="T2">The second key's type.</typeparam>
        /// <typeparam name="TSort">The sort of its terms, defined with <see cref="DefineSort{TSort}(string)"/>.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="cost">The cost of a node with this head, at least 0.</param>
        /// <param name="options">Whether extraction may use it, whether it is subsumable, and the lift policy; the role
        /// is always a term.</param>
        /// <returns>The constructor.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, or a type is unknown.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Constructor<T1, T2, TSort> DefineConstructor<T1, T2, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort> =>
            DefineTable(static () => new Constructor<T1, T2, TSort>(), name, TableKind.Constructor,
                new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>() }, Values.TypeOf<TSort>(), options, null, cost);

        /// <summary>
        /// Defines a constructor of three keys (a term head).
        /// </summary>
        /// <typeparam name="T1">The first key's type.</typeparam>
        /// <typeparam name="T2">The second key's type.</typeparam>
        /// <typeparam name="T3">The third key's type.</typeparam>
        /// <typeparam name="TSort">The sort of its terms, defined with <see cref="DefineSort{TSort}(string)"/>.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="cost">The cost of a node with this head, at least 0.</param>
        /// <param name="options">Whether extraction may use it, whether it is subsumable, and the lift policy; the role
        /// is always a term.</param>
        /// <returns>The constructor.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, or a type is unknown.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Constructor<T1, T2, T3, TSort> DefineConstructor<T1, T2, T3, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort> =>
            DefineTable(static () => new Constructor<T1, T2, T3, TSort>(), name, TableKind.Constructor,
                new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>(), Values.TypeOf<T3>() }, Values.TypeOf<TSort>(), options, null, cost);

        /// <summary>
        /// Defines a constructor of four keys (a term head).
        /// </summary>
        /// <typeparam name="T1">The first key's type.</typeparam>
        /// <typeparam name="T2">The second key's type.</typeparam>
        /// <typeparam name="T3">The third key's type.</typeparam>
        /// <typeparam name="T4">The fourth key's type.</typeparam>
        /// <typeparam name="TSort">The sort of its terms, defined with <see cref="DefineSort{TSort}(string)"/>.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="cost">The cost of a node with this head, at least 0.</param>
        /// <param name="options">Whether extraction may use it, whether it is subsumable, and the lift policy; the role
        /// is always a term.</param>
        /// <returns>The constructor.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, or a type is unknown.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public Constructor<T1, T2, T3, T4, TSort> DefineConstructor<T1, T2, T3, T4, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort> =>
            DefineTable(static () => new Constructor<T1, T2, T3, T4, TSort>(), name, TableKind.Constructor,
                new ColumnType[] { Values.TypeOf<T1>(), Values.TypeOf<T2>(), Values.TypeOf<T3>(), Values.TypeOf<T4>() }, Values.TypeOf<TSort>(), options, null, cost);

        /// <summary>
        /// Defines a named ruleset. Any thread, any time.
        /// </summary>
        /// <param name="name">The name, unique among the graph's rulesets.</param>
        /// <returns>The ruleset.</returns>
        /// <exception cref="ArgumentException">The name is empty or taken.</exception>
        public Ruleset DefineRuleset(string name) => DefineRulesetCore(name, null);

        /// <summary>
        /// Defines a named ruleset with an optional licence.
        /// </summary>
        Ruleset DefineRulesetCore(string name, ScopeTag? licence)
        {
            EngineCallMarker.ThrowIfActive();
            NameRules.ValidateName(name, nameof(name));
            lock (DefinitionLock)
            {
                if (rulesetsByName.ContainsKey(name))
                    throw new ArgumentException($"A ruleset named '{name}' is already defined.", nameof(name));

                var ruleset = new Ruleset(this, name, licence);
                rulesetsByName.Add(name, ruleset);
                var next = new Ruleset[rulesets.Length + 1];
                rulesets.CopyTo(next, 0);
                next[^1] = ruleset;
                rulesets = next;
                Volatile.Write(ref rulesetsView, Array.AsReadOnly(next));
                return ruleset;
            }
        }

        /// <summary>
        /// The untyped relation definition: any number of columns, where the typed overloads stop at four.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="keys">The column types, of this graph.</param>
        /// <param name="options">The role, lift policy and subsumability; <c>null</c> for a fact.</param>
        /// <returns>The relation.</returns>
        internal RelationHandle DefineRelation(string name, IReadOnlyList<ColumnType> keys, TableOptions? options = null) =>
            DefineTable(static () => new UntypedRelation(), name, TableKind.Relation, Copy(keys), null, options, null, 0);

        /// <summary>
        /// Defines a relation over any number of key columns, as egglog's relations take, where the typed
        /// <c>DefineRelation</c> overloads stop at four: its facts are <see cref="RelationN.Of"/> of one argument per
        /// column.
        /// </summary>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="keys">The key columns' types: value types or sorts, each registered with this graph.</param>
        /// <param name="options">The role, lift policy and subsumability; <c>null</c> for a fact.</param>
        /// <returns>The relation.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, or the role is
        /// <see cref="TableRole.Term"/>.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public RelationN DefineRelationN(string name, IReadOnlyList<Type> keys, TableOptions? options = null) =>
            DefineTable(static () => new RelationN(), name, TableKind.Relation, TypesOf(keys), null, options, null, 0);

        /// <summary>
        /// Defines a function over any number of key columns, as egglog's functions take, where the typed
        /// <c>DefineFunction</c> overloads stop at four: its terms are <see cref="FunctionN{TOut}.Of"/> of one argument per
        /// key. Otherwise as those overloads, with or without a lattice.
        /// </summary>
        /// <typeparam name="TOut">The output type: a value type, or a sort.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="keys">The key columns' types: value types or sorts, each registered with this graph.</param>
        /// <param name="lattice">The merge of two values, not of classes; <c>null</c> for none.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, a type is unknown, a lattice is given for a
        /// sort output or one with a case join for a lifted container output, the role is <see cref="TableRole.Term"/>, or
        /// the options make it subsumable.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public FunctionN<TOut> DefineFunctionN<TOut>(string name, IReadOnlyList<Type> keys, ValueLattice<TOut>? lattice = null, TableOptions? options = null) =>
            DefineTable(static () => new FunctionN<TOut>(), name, TableKind.Function, TypesOf(keys), Values.TypeOf<TOut>(), options, lattice, 0);

        /// <summary>
        /// Defines a function over any number of key columns whose merge is not a lattice (<see cref="FunctionMerge"/>),
        /// as the typed overloads with a merge do; only the root writes it.
        /// </summary>
        /// <typeparam name="TOut">The output type: a value type, or a sort.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="keys">The key columns' types: value types or sorts, each registered with this graph.</param>
        /// <param name="merge">The merge of the old and the new value.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact.</param>
        /// <returns>The function.</returns>
        /// <exception cref="ArgumentException">As the typed overloads with a merge.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public FunctionN<TOut> DefineFunctionN<TOut>(string name, IReadOnlyList<Type> keys, FunctionMerge<TOut> merge, TableOptions? options = null) =>
            DefineTable(static () => new FunctionN<TOut>(), name, TableKind.Function, TypesOf(keys), Values.TypeOf<TOut>(), options, null, 0,
                merge: merge.Definition);

        /// <summary>
        /// Defines a constructor over any number of key columns, as egglog's constructors take, where the typed
        /// <c>DefineConstructor</c> overloads stop at four: its terms are <see cref="ConstructorN{TSort}.Of"/> of one
        /// argument per key.
        /// </summary>
        /// <typeparam name="TSort">The sort of its terms, defined with <see cref="DefineSort{TSort}(string)"/>.</typeparam>
        /// <param name="name">The name: unique among the graph's tables, non-empty, without '@', not starting with '$'.</param>
        /// <param name="keys">The key columns' types: value types or sorts, each registered with this graph.</param>
        /// <param name="cost">The cost of a node with this head, at least 0.</param>
        /// <param name="options">Whether extraction may use it, whether it is subsumable, and the lift policy; the role
        /// is always a term.</param>
        /// <returns>The constructor.</returns>
        /// <exception cref="ArgumentException">The name is invalid or taken, or a type is unknown.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the root.</exception>
        public ConstructorN<TSort> DefineConstructorN<TSort>(string name, IReadOnlyList<Type> keys, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort> =>
            DefineTable(static () => new ConstructorN<TSort>(), name, TableKind.Constructor, TypesOf(keys), Values.TypeOf<TSort>(), options, null, cost);

        /// <summary>
        /// Gets the column types of key types, each registered with this graph.
        /// </summary>
        /// <exception cref="ArgumentException">A type is unknown.</exception>
        ColumnType[] TypesOf(IReadOnlyList<Type> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            var types = new ColumnType[keys.Count];
            for (var i = 0; i < types.Length; i++)
                types[i] = Values.TypeOf(keys[i] ?? throw new ArgumentNullException(nameof(keys), $"Key {i} is null."));

            return types;
        }

        /// <summary>
        /// The untyped function definition: any number of keys, where the typed overloads stop at four.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="keys">The key types, of this graph.</param>
        /// <param name="output">The output type, of this graph: a value type or a sort.</param>
        /// <param name="lattice">The merge, of values of the output type; <c>null</c> for none.</param>
        /// <param name="options">The role and lift policy; <c>null</c> for a fact.</param>
        /// <returns>The function.</returns>
        internal FunctionHandle DefineFunction(string name, IReadOnlyList<ColumnType> keys, ColumnType output, ValueLattice? lattice = null, TableOptions? options = null) =>
            DefineTable(static () => new UntypedFunction(), name, TableKind.Function, Copy(keys), output ?? throw new ArgumentNullException(nameof(output)), options, lattice, 0);

        /// <summary>
        /// The untyped constructor definition: any number of keys, where the typed overloads stop at four.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="keys">The key types, of this graph.</param>
        /// <param name="sort">The sort of its terms, of this graph.</param>
        /// <param name="cost">The cost of a node with this head, at least 0.</param>
        /// <param name="options">Whether extraction may use it and the lift policy.</param>
        /// <returns>The constructor.</returns>
        internal ConstructorHandle DefineConstructor(string name, IReadOnlyList<ColumnType> keys, ColumnType sort, long cost = 1, TableOptions? options = null) =>
            DefineTable(static () => new UntypedConstructor(), name, TableKind.Constructor, Copy(keys), sort ?? throw new ArgumentNullException(nameof(sort)), options, null, cost);

        /// <summary>
        /// Defines the built-in nullary relation <see cref="Contradiction"/>, under a name a user's table cannot take
        /// (<c>$contradiction</c>, a reserved prefix), so that it takes no name an egglog program may use.
        /// </summary>
        /// <returns>The relation.</returns>
        Relation DefineContradiction() =>
            DefineTable(static () => new Relation(), ContradictionName, TableKind.Relation, Array.Empty<ColumnType>(), null, null, null, 0, builtIn: true);

        /// <summary>
        /// Defines a table: validates, then under the definition lock adds the engine function
        /// (<see cref="AddEngineTable"/>, which enters the root's guard while the root is writable) and publishes the
        /// handle.
        /// </summary>
        T DefineTable<T>(Func<T> create, string name, TableKind kind, ColumnType[] keys, ColumnType? output, TableOptions? options, ValueLattice? lattice, long cost,
            bool builtIn = false, FunctionMergeDefinition? merge = null)
            where T : TableHandle
        {
            EngineCallMarker.ThrowIfActive();
            if (builtIn == false)
                NameRules.ValidateTableName(name, nameof(name));
            options ??= DefaultTableOptions;
            foreach (var key in keys)
                CheckOwnType(key ?? throw new ArgumentNullException(nameof(keys), "A column type is null."));

            if (output is not null)
                CheckOwnType(output);

            var role = options.Role;
            switch (kind)
            {
                case TableKind.Relation:
                case TableKind.Function:
                    if (role == TableRole.Term)
                        throw new ArgumentException($"The role of {(kind == TableKind.Relation ? "relation" : "function")} '{name}' is Term; only constructors are terms.", nameof(options));

                    // A function may output a class, as egglog's function of an e-class sort: its merge then asserts
                    // the classes equal, or keeps the old or the new one; a lattice and a merge function decode values.
                    if (kind == TableKind.Function && output!.Kind == ColumnKind.Sort)
                    {
                        if (lattice is not null)
                            throw new ArgumentException($"Function '{name}' outputs the sort {output.Name}; lattice '{lattice.Name}' merges values, not classes.", nameof(lattice));

                        if (merge is { DecodesValues: true })
                            throw new ArgumentException($"Function '{name}' outputs the sort {output.Name}; merge '{merge.Name}' is a function of values, and only FunctionMerge.New, Old and Expression merge classes.", nameof(merge));
                    }

                    if (lattice is not null && lattice.ValueType != output!.ClrType)
                        throw new ArgumentException($"Lattice '{lattice.Name}' merges {lattice.ValueType.Name}, not {output.ClrType.Name}.", nameof(lattice));

                    // A case join combines a lifted function's base values; a container's cell is an id.
                    if (lattice is not null && output is IContainerColumnType && (options.Lift ?? role == TableRole.Fact)
                        && lattice.CreateCaseJoin(output, name) is not null)
                        throw new ArgumentException($"Function '{name}' outputs the container {output.Name}, which a lift cannot join across cases; define lattice '{lattice.Name}' without a case join, or the function with Lift = false.", nameof(lattice));

                    // egglog subsumes only constructors and relations (SubsumeMergeError, src/lib.rs:2590-2598).
                    if (kind == TableKind.Function && options.Subsumable == true)
                        throw new ArgumentException($"Function '{name}' cannot be subsumable; only relations and constructors can be subsumed.", nameof(options));

                    break;
                default:
                    ArgumentOutOfRangeException.ThrowIfNegative(cost);
                    if (output!.Kind != ColumnKind.Sort)
                        throw new ArgumentException($"Constructor '{name}' outputs {output.Name}, which is not a sort defined with DefineSort.", nameof(name));

                    role = TableRole.Term;
                    break;
            }

            lock (DefinitionLock)
            {
                if (tablesByName.ContainsKey(name))
                    throw new ArgumentException($"A table named '{name}' is already defined.", nameof(name));

                var columns = new ColumnType[keys.Length + 1];
                keys.CopyTo(columns, 0);
                columns[^1] = output ?? Unit;
                // As egglog, every relation and constructor is subsumable unless the options opt it out.
                var subsumable = options.Subsumable ?? kind != TableKind.Function;
                var function = AddEngineTable(name, kind, columns, lattice, output, subsumable, merge);
                var handle = create();
                handle.Graph = this;
                handle.Name = name;
                handle.Index = tables.Length;
                handle.Kind = kind;
                handle.Role = role;
                handle.KeyArray = keys;
                handle.Output = output;
                handle.Lift = options.Lift ?? (kind == TableKind.Constructor || role == TableRole.Fact);
                handle.Subsumable = subsumable;
                handle.Function = function;
                handle.Columns = columns;
                if (handle is FunctionHandle functionHandle)
                {
                    functionHandle.Lattice = lattice;
                    functionHandle.Merge = merge;
                    functionHandle.CaseJoin = lattice?.CreateCaseJoin(output!, name);
                }

                if (handle is ConstructorHandle constructor)
                {
                    constructor.Cost = cost;
                    constructor.Extractable = options.Extractable;
                    Extraction.Declare(constructor);
                }

                tablesByName.Add(name, handle);
                var nextTables = new TableHandle[tables.Length + 1];
                tables.CopyTo(nextTables, 0);
                nextTables[^1] = handle;
                PublishFunction(handle);
                tables = nextTables;
                Volatile.Write(ref tablesView, Array.AsReadOnly(nextTables));
                return handle;
            }
        }

        /// <summary>
        /// Adds the engine function of a table: a relation's unit default and assert-equal merge, a
        /// function's lattice merge (its own <c>LatticeMerge</c> external) or assert-equal, a constructor's fresh-id
        /// default and union merge; with a subsumption column when <paramref name="subsumable"/> is set (egglog's
        /// <c>can_subsume</c>). A table definition: while the root is writable, the call enters the root's guard,
        /// so it throws when another thread is using the root; once the root is frozen, any thread may define one. Called
        /// under the definition lock. Nothing is registered when the engine refuses the table.
        /// </summary>
        /// <exception cref="InvalidOperationException">Another thread is using the writable root.</exception>
        Relations.FunctionId AddEngineTable(string name, TableKind kind, ColumnType[] columns, ValueLattice? lattice, ColumnType? output,
            bool subsumable, FunctionMergeDefinition? functionMerge = null, bool contextPrivate = false)
        {
            var guarded = Root.IsFrozen == false;
            if (guarded)
                Root.Guard.Enter(Root);

            try
            {
                var schema = new Relations.ColumnTy[columns.Length];
                for (var i = 0; i < columns.Length; i++)
                    schema[i] = columns[i].EngineType;

                var registered = new List<Relations.ExternalFunctionId>();
                try
                {
                    Relations.DefaultVal defaultValue;
                    Relations.MergeFn mergeFn;
                    switch (kind)
                    {
                        case TableKind.Relation:
                            defaultValue = Relations.DefaultVal.Const(new Relations.Value(Unit.Unit));
                            mergeFn = Relations.MergeFn.AssertEq;
                            break;
                        case TableKind.Function:
                            defaultValue = Relations.DefaultVal.Fail;
                            if (lattice is not null)
                            {
                                var merge = Engine.RegisterExternalFunction(lattice.CreateMerge(this, name, output!));
                                registered.Add(merge);
                                mergeFn = Relations.MergeFn.Primitive(merge, Relations.MergeFn.Old, Relations.MergeFn.New).AsLattice();
                            }
                            else if (functionMerge is not null)
                            {
                                // Not a lattice merge: the engine refuses a write of the function from a child (decisions row 2).
                                mergeFn = functionMerge.CreateMergeFn(this, name, output!, registered);
                            }
                            else
                            {
                                mergeFn = Relations.MergeFn.AssertEq;
                            }

                            break;
                        default:
                            defaultValue = Relations.DefaultVal.FreshId;
                            mergeFn = Relations.MergeFn.UnionId;
                            break;
                    }

                    return Engine.AddTable(new Relations.FunctionConfig
                    {
                        Schema = schema,
                        Default = defaultValue,
                        Merge = mergeFn,
                        Name = name,
                        CanSubsume = subsumable,
                        ContextPrivate = contextPrivate,
                    });
                }
                catch
                {
                    // A refused definition leaves nothing behind: the external functions its merge registered go too.
                    foreach (var id in registered)
                        Engine.FreeExternalFunction(id);

                    throw;
                }
            }
            finally
            {
                if (guarded)
                    Root.Guard.Exit();
            }
        }

        /// <summary>
        /// Publishes a handle by its engine function (<see cref="TableOf"/>), copy-on-write. Called under the definition
        /// lock.
        /// </summary>
        void PublishFunction(TableHandle handle)
        {
            var byFunction = tablesByFunction;
            if (handle.Function.Rep >= byFunction.Length)
                Array.Resize(ref byFunction, Math.Max(handle.Function.Rep + 1, byFunction.Length * 2));
            else
                byFunction = (TableHandle?[])byFunction.Clone();

            byFunction[handle.Function.Rep] = handle;
            Volatile.Write(ref tablesByFunction, byFunction);
        }

        /// <summary>
        /// Checks that a column type belongs to this graph.
        /// </summary>
        void CheckOwnType(ColumnType type)
        {
            if (ReferenceEquals(type.Store, Values) == false)
                throw new ArgumentException($"Column type {type.Name} belongs to another graph.");
        }

        /// <summary>
        /// Copies a list of column types.
        /// </summary>
        static ColumnType[] Copy(IReadOnlyList<ColumnType> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            var copy = new ColumnType[keys.Count];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = keys[i];

            return copy;
        }

    }

}
