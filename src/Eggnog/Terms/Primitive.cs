using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A pure, deterministic, thread-safe function over values and classes, as egglog's primitives take any sort, or
    /// for <c>ordering-min</c> and <c>ordering-max</c> over their cells. An exception thrown by it fails the rule with a
    /// <see cref="RuleEvaluationException"/>; a call into any graph from it throws an
    /// <see cref="InvalidOperationException"/>. Named primitives render by name, and a filter over one as egglog writes
    /// it, its fallible form (<c>&lt;</c> for <c>bool-&lt;</c>) or <c>(guard …)</c>; lambdas (<see cref="Compute"/>,
    /// <see cref="RuleBuilder.Where{T1}(Term{T1}, Func{T1, bool})"/>) render as <c>&lt;lambda&gt;</c>.
    /// </summary>
    /// <remarks>
    /// A primitive may be partial (<see cref="IsPartial"/>), as egglog's <c>-?&gt;</c> primitives are: it has no result
    /// for some arguments. In a rule body, a let or a filter whose call has no result drops the match, as in egglog; in a
    /// head, or in a split's case assumptions, it fails the rule, where egglog panics.
    /// </remarks>
    public abstract class Primitive
    {

        /// <summary>
        /// The name lambdas render as.
        /// </summary>
        internal const string LambdaName = "<lambda>";

        readonly IReadOnlyList<Type> parameterTypes;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="parameterTypes">The CLR types of the parameters.</param>
        /// <param name="resultType">The CLR type of the result.</param>
        /// <param name="isLambda">Whether the primitive is an anonymous lambda.</param>
        /// <param name="isPartial">Whether the primitive is partial.</param>
        private protected Primitive(string name, Type[] parameterTypes, Type resultType, bool isLambda, bool isPartial)
            : this(name, (IReadOnlyList<Type>)Array.AsReadOnly(parameterTypes), resultType, isLambda, isPartial)
        {

        }

        /// <summary>
        /// Initializes a new instance over a read-only list of parameter types, kept as given.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="parameterTypes">The CLR types of the parameters; read-only.</param>
        /// <param name="resultType">The CLR type of the result.</param>
        /// <param name="isLambda">Whether the primitive is an anonymous lambda.</param>
        /// <param name="isPartial">Whether the primitive is partial.</param>
        private protected Primitive(string name, IReadOnlyList<Type> parameterTypes, Type resultType, bool isLambda, bool isPartial)
        {
            Name = name;
            this.parameterTypes = parameterTypes;
            ResultType = resultType;
            IsLambda = isLambda;
            IsPartial = isPartial;
        }

        /// <summary>
        /// Gets the name: an egglog name for the built-ins (<c>set-of</c>, <c>+</c>), <c>&lt;lambda&gt;</c> for
        /// lambdas.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the CLR types of the parameters.
        /// </summary>
        public IReadOnlyList<Type> ParameterTypes => parameterTypes;

        /// <summary>
        /// Gets the CLR type of the result.
        /// </summary>
        public Type ResultType { get; }

        /// <summary>
        /// Gets whether the primitive is partial, as egglog's <c>-?&gt;</c> primitives are: it has no result for some
        /// arguments. In a rule body (a let or a filter) and a query that drops the match, as in egglog; in a head or a
        /// split's case assumptions it fails the rule, where egglog panics. Lambdas (<see cref="Compute"/>) are never
        /// partial: one that throws fails the rule.
        /// </summary>
        public bool IsPartial { get; }

        /// <summary>
        /// Creates a named primitive of one parameter.
        /// </summary>
        /// <typeparam name="T1">The parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function: pure, deterministic and thread-safe.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, TResult> Create<T1, TResult>(string name, Func<T1, TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, TResult>(name, function, false);
        }

        /// <summary>
        /// Creates a named primitive of two parameters.
        /// </summary>
        /// <typeparam name="T1">The first parameter type.</typeparam>
        /// <typeparam name="T2">The second parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function: pure, deterministic and thread-safe.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, T2, TResult> Create<T1, T2, TResult>(string name, Func<T1, T2, TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, T2, TResult>(name, function, false);
        }

        /// <summary>
        /// Creates a named primitive of three parameters.
        /// </summary>
        /// <typeparam name="T1">The first parameter type.</typeparam>
        /// <typeparam name="T2">The second parameter type.</typeparam>
        /// <typeparam name="T3">The third parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function: pure, deterministic and thread-safe.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, T2, T3, TResult> Create<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, T2, T3, TResult>(name, function, false);
        }

        /// <summary>
        /// Creates a named primitive of four parameters.
        /// </summary>
        /// <typeparam name="T1">The first parameter type.</typeparam>
        /// <typeparam name="T2">The second parameter type.</typeparam>
        /// <typeparam name="T3">The third parameter type.</typeparam>
        /// <typeparam name="T4">The fourth parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function: pure, deterministic and thread-safe.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, T2, T3, T4, TResult> Create<T1, T2, T3, T4, TResult>(string name, Func<T1, T2, T3, T4, TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, T2, T3, T4, TResult>(name, function, false);
        }

        /// <summary>
        /// Creates a named primitive of any number of parameters, each of its own type, as egglog's primitives take,
        /// where the typed <c>Create</c> overloads stop at four: <paramref name="function"/> gets the arguments as
        /// objects, one per parameter, in a span valid only during the call.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="parameters">The parameter types: value types or sorts.</param>
        /// <param name="function">The function: pure, deterministic and thread-safe.</param>
        /// <returns>The primitive.</returns>
        /// <exception cref="ArgumentException">The name is invalid, or a parameter type is <c>null</c>.</exception>
        public static PrimitiveN<TResult> Create<TResult>(string name, IReadOnlyList<Type> parameters, FuncN<TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new PrimitiveN<TResult>(name, ParameterArray(parameters), function, null);
        }

        /// <summary>
        /// Creates a named partial primitive of any number of parameters, as egglog's <c>-?&gt;</c> primitives are: where
        /// <paramref name="function"/> returns <c>false</c> the call has no result (<see cref="Primitive"/>).
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="parameters">The parameter types: value types or sorts.</param>
        /// <param name="function">The function: pure, deterministic and thread-safe.</param>
        /// <returns>The primitive; its <see cref="PrimitiveN{TResult}.Function"/> throws
        /// <see cref="InvalidOperationException"/> where there is no result.</returns>
        /// <exception cref="ArgumentException">The name is invalid, or a parameter type is <c>null</c>.</exception>
        public static PrimitiveN<TResult> CreatePartial<TResult>(string name, IReadOnlyList<Type> parameters, PartialFuncN<TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new PrimitiveN<TResult>(name, ParameterArray(parameters), arguments => function(arguments, out var result) ? result : throw NoResult(name), function);
        }

        /// <summary>
        /// Copies parameter types, refusing a missing one.
        /// </summary>
        static Type[] ParameterArray(IReadOnlyList<Type> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            var types = new Type[parameters.Count];
            for (var i = 0; i < types.Length; i++)
                types[i] = parameters[i] ?? throw new ArgumentException($"Parameter type {i} is null.", nameof(parameters));

            return types;
        }

        /// <summary>
        /// Creates a named primitive of any number of arguments of one type, as egglog's <c>or</c> and <c>set-of</c> are.
        /// </summary>
        /// <typeparam name="T">The arguments' type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function over all the arguments: pure, deterministic and thread-safe; it must not
        /// keep the list, which is valid only during the call.</param>
        /// <returns>The primitive.</returns>
        public static VariadicPrimitive<T, TResult> CreateVariadic<T, TResult>(string name, Func<IReadOnlyList<T>, TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new VariadicPrimitive<T, TResult>(name, function);
        }

        /// <summary>
        /// Creates a named partial primitive of one parameter, as egglog's <c>-?&gt;</c> primitives are: where
        /// <paramref name="function"/> returns <c>false</c> the call has no result (<see cref="Primitive"/>).
        /// </summary>
        /// <typeparam name="T1">The parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function: pure, deterministic and thread-safe.</param>
        /// <returns>The primitive; its <see cref="Primitive{T1, TResult}.Function"/> throws
        /// <see cref="InvalidOperationException"/> where there is no result.</returns>
        public static Primitive<T1, TResult> CreatePartial<T1, TResult>(string name, PartialFunc<T1, TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, TResult>(name, a => function(a, out var result) ? result : throw NoResult(name), function);
        }

        /// <summary>
        /// Creates a named partial primitive of two parameters, as egglog's <c>-?&gt;</c> primitives are: where
        /// <paramref name="function"/> returns <c>false</c> the call has no result (<see cref="Primitive"/>).
        /// </summary>
        /// <typeparam name="T1">The first parameter type.</typeparam>
        /// <typeparam name="T2">The second parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function: pure, deterministic and thread-safe.</param>
        /// <returns>The primitive; its <see cref="Primitive{T1, T2, TResult}.Function"/> throws
        /// <see cref="InvalidOperationException"/> where there is no result.</returns>
        public static Primitive<T1, T2, TResult> CreatePartial<T1, T2, TResult>(string name, PartialFunc<T1, T2, TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, T2, TResult>(name, (a, b) => function(a, b, out var result) ? result : throw NoResult(name), function);
        }

        /// <summary>
        /// Creates a named partial primitive of three parameters, as egglog's <c>-?&gt;</c> primitives are: where
        /// <paramref name="function"/> returns <c>false</c> the call has no result (<see cref="Primitive"/>).
        /// </summary>
        /// <typeparam name="T1">The first parameter type.</typeparam>
        /// <typeparam name="T2">The second parameter type.</typeparam>
        /// <typeparam name="T3">The third parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function: pure, deterministic and thread-safe.</param>
        /// <returns>The primitive; its <see cref="Primitive{T1, T2, T3, TResult}.Function"/> throws
        /// <see cref="InvalidOperationException"/> where there is no result.</returns>
        public static Primitive<T1, T2, T3, TResult> CreatePartial<T1, T2, T3, TResult>(string name, PartialFunc<T1, T2, T3, TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, T2, T3, TResult>(name, (a, b, c) => function(a, b, c, out var result) ? result : throw NoResult(name), function);
        }

        /// <summary>
        /// Creates a named partial primitive of four parameters, as egglog's <c>-?&gt;</c> primitives are: where
        /// <paramref name="function"/> returns <c>false</c> the call has no result (<see cref="Primitive"/>).
        /// </summary>
        /// <typeparam name="T1">The first parameter type.</typeparam>
        /// <typeparam name="T2">The second parameter type.</typeparam>
        /// <typeparam name="T3">The third parameter type.</typeparam>
        /// <typeparam name="T4">The fourth parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function: pure, deterministic and thread-safe.</param>
        /// <returns>The primitive; its <see cref="Primitive{T1, T2, T3, T4, TResult}.Function"/> throws
        /// <see cref="InvalidOperationException"/> where there is no result.</returns>
        public static Primitive<T1, T2, T3, T4, TResult> CreatePartial<T1, T2, T3, T4, TResult>(string name,
            PartialFunc<T1, T2, T3, T4, TResult> function)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, T2, T3, T4, TResult>(name, (a, b, c, d) => function(a, b, c, d, out var result) ? result : throw NoResult(name), function);
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;

        /// <summary>
        /// Gets whether the primitive is an anonymous lambda, rendered as <c>&lt;lambda&gt;</c>.
        /// </summary>
        internal bool IsLambda { get; }

        /// <summary>
        /// Gets the name a filter over the primitive renders with (<see cref="RuleBuilder.Where(Term{bool})"/>): egglog's
        /// fallible form of a <c>bool</c> primitive, such as <c>&lt;</c> for <c>bool-&lt;</c> or <c>set-contains</c>,
        /// which matches where the primitive is true; <c>null</c> when egglog has none, and the filter renders as
        /// <c>(guard …)</c>.
        /// </summary>
        internal string? FilterName { get; init; }

        /// <summary>
        /// Decodes each argument cell with its parameter's column type, calls the function and encodes the result
        /// (interning a new value): what a head and a case assumption do, where a partial primitive's call with no result
        /// throws, as its function does. Allocates nothing beyond what the function allocates and what interning costs,
        /// except a <see cref="PrimitiveN{TResult}"/>'s argument list.
        /// </summary>
        /// <param name="arguments">The argument cells.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="result">The result's column type.</param>
        /// <param name="scope">The scope the call runs in, whose origin a class argument carries and in which a class
        /// result must be usable; <c>null</c> where no argument or result is a class.</param>
        /// <returns>The result's cell.</returns>
        internal abstract uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope);

        /// <summary>
        /// As <see cref="InvokeCore"/>, but a partial primitive's call with no result returns <c>false</c> instead of
        /// throwing: what a body let and a filter do.
        /// </summary>
        /// <param name="arguments">The argument cells.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="result">The result's column type.</param>
        /// <param name="scope">The scope the call runs in (see <see cref="InvokeCore"/>).</param>
        /// <param name="cell">The result's cell, when there is a result.</param>
        /// <returns>Whether there is a result: <c>false</c> only for a partial primitive.</returns>
        internal abstract bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope, out uint cell);

        /// <summary>
        /// Calls the primitive on CLR values, the arguments neither encoded nor the result: what a host read does, which
        /// must not intern.
        /// </summary>
        /// <param name="arguments">The arguments, of the parameters' types.</param>
        /// <param name="parameters">The parameters' column types, which a primitive over cells needs.</param>
        /// <param name="value">The result, when there is one.</param>
        /// <returns>Whether there is a result: <c>false</c> for a partial primitive with none, and for a primitive over
        /// cells whose arguments have none.</returns>
        internal abstract bool TryInvokeValue(object[] arguments, ColumnType[] parameters, out object? value);

        /// <summary>
        /// Gets what <c>Function</c> throws for a partial primitive's call that has no result.
        /// </summary>
        /// <param name="name">The primitive's name.</param>
        /// <returns>The exception.</returns>
        internal static InvalidOperationException NoResult(string name) => new($"Primitive '{name}' has no result for these arguments.");

    }

}
