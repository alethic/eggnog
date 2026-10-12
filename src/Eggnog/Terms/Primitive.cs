using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Eggnog
{

    /// <summary>
    /// A pure, deterministic, thread-safe function over values (never over classes). An exception thrown by it fails the
    /// rule (D31); a call into any graph from it throws (D47). Named primitives render by name; lambdas
    /// (<see cref="Compute"/>, <see cref="RuleBuilder.Where{T1}(Term{T1}, Func{T1, bool})"/>) render as
    /// <c>&lt;lambda&gt;</c>.
    /// </summary>
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
        private protected Primitive(string name, Type[] parameterTypes, Type resultType, bool isLambda)
        {
            Name = name;
            this.parameterTypes = Array.AsReadOnly(parameterTypes);
            ResultType = resultType;
            IsLambda = isLambda;
        }

        /// <summary>
        /// Gets the name: an egglog name for the built-ins (<c>set-of</c>, <c>+</c>), <c>&lt;lambda&gt;</c> for lambdas.
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
        /// Gets the name.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;

        /// <summary>
        /// Gets whether the primitive is an anonymous lambda, rendered as <c>&lt;lambda&gt;</c>.
        /// </summary>
        internal bool IsLambda { get; }

        /// <summary>
        /// Gets whether the primitive is partial, as egglog's checked arithmetic is (its <c>-?&gt;</c> primitives, which
        /// return no result on overflow): an <see cref="OverflowException"/> from it means "no result". In a rule body
        /// that drops the match silently, as in egglog; in a head it fails the rule (D31), where egglog panics. Only the
        /// built-in <see cref="Primitives.Add"/> and <see cref="Primitives.Subtract"/> are partial.
        /// </summary>
        internal bool IsPartial { get; init; }

        /// <summary>
        /// Decodes each argument cell with its parameter's column type, calls the function and encodes the result
        /// (interning a new value). Allocates nothing beyond what the function allocates and what interning costs.
        /// </summary>
        /// <param name="arguments">The argument cells.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="result">The result's column type.</param>
        /// <returns>The result's cell.</returns>
        internal abstract uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result);

    }

}
