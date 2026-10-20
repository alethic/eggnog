using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Eggnog
{

    /// <summary>
    /// A pure, deterministic, thread-safe function over values (never over classes). An exception thrown by it fails
    /// the rule with a <see cref="RuleEvaluationException"/>; a call into any graph from it throws an
    /// <see cref="InvalidOperationException"/>. Named primitives render by name; lambdas (<see cref="Compute"/>,
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

        readonly ReadOnlyCollection<Type> parameterTypes;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="parameterTypes">The CLR types of the parameters.</param>
        /// <param name="resultType">The CLR type of the result.</param>
        /// <param name="isLambda">Whether the primitive is an anonymous lambda.</param>
        /// <param name="isPartial">Whether the primitive is partial.</param>
        private protected Primitive(string name, Type[] parameterTypes, Type resultType, bool isLambda, bool isPartial)
        {
            Name = name;
            this.parameterTypes = Array.AsReadOnly(parameterTypes);
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
        /// Decodes each argument cell with its parameter's column type, calls the function and encodes the result
        /// (interning a new value): what a head and a case assumption do, where a partial primitive's call with no result
        /// throws, as its function does. Allocates nothing beyond what the function allocates and what interning costs.
        /// </summary>
        /// <param name="arguments">The argument cells.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="result">The result's column type.</param>
        /// <returns>The result's cell.</returns>
        internal abstract uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result);

        /// <summary>
        /// As <see cref="InvokeCore"/>, but a partial primitive's call with no result returns <c>false</c> instead of
        /// throwing: what a body let and a filter do.
        /// </summary>
        /// <param name="arguments">The argument cells.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="result">The result's column type.</param>
        /// <param name="cell">The result's cell, when there is a result.</param>
        /// <returns>Whether there is a result: <c>false</c> only for a partial primitive.</returns>
        internal abstract bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, out uint cell);

        /// <summary>
        /// Gets what <c>Function</c> throws for a partial primitive's call that has no result.
        /// </summary>
        /// <param name="name">The primitive's name.</param>
        /// <returns>The exception.</returns>
        internal static InvalidOperationException NoResult(string name) => new($"Primitive '{name}' has no result for these arguments.");

    }

}
