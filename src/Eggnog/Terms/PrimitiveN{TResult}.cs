using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    /// <summary>
    /// A primitive of any number of parameters, each of its own type, as egglog's primitives take, where the typed
    /// primitives stop at four: <see cref="Of"/> applies it to terms. Made by
    /// <see cref="Primitive.Create{TResult}(string, IReadOnlyList{Type}, Func{IReadOnlyList{object}, TResult})"/> and
    /// <see cref="Primitive.CreatePartial{TResult}(string, IReadOnlyList{Type}, PartialFuncN{TResult})"/>. Its functions
    /// take the arguments as objects, one per parameter, each of its parameter's type: a new list on each call from a
    /// rule, a host write or a read, which the function may keep. Such a call allocates that list and boxes its
    /// values, where a typed primitive's allocates nothing.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    public sealed class PrimitiveN<TResult> : Primitive
    {

        readonly PartialFuncN<TResult>? partial;

        /// <summary>
        /// Initializes a new instance of a named primitive.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="parameters">The parameter types.</param>
        /// <param name="function">The function, which throws where a partial primitive has no result.</param>
        /// <param name="partial">The partial function, which says where there is no result; <c>null</c> for a total
        /// primitive.</param>
        internal PrimitiveN(string name, Type[] parameters, Func<IReadOnlyList<object>, TResult> function, PartialFuncN<TResult>? partial)
            : base(name, parameters, typeof(TResult), false, partial is not null)
        {
            Function = function;
            this.partial = partial;
        }

        /// <summary>
        /// Gets the function. For a partial primitive (<see cref="Primitive.IsPartial"/>) it throws where there is no
        /// result.
        /// </summary>
        public Func<IReadOnlyList<object>, TResult> Function { get; }

        /// <summary>
        /// The primitive applied to terms: a computed value in a body (a let or a check) or a head.
        /// </summary>
        /// <param name="arguments">One argument per parameter, of the parameter's type: a term, or a value, which becomes
        /// a constant (an instance of the parameter's type).</param>
        /// <returns>The term.</returns>
        /// <exception cref="ArgumentException">The number of arguments is not the primitive's, or an argument's type is
        /// not its parameter's.</exception>
        public Term<TResult> Of(params object[] arguments) =>
            new ComputeTerm<TResult>(this, BoxedConstantTerm.Convert(arguments, ParameterTypes, Name));

        /// <summary>
        /// Calls the primitive on values without throwing for a missing result: the partial function for a partial
        /// primitive, else the function, which always has a result.
        /// </summary>
        /// <param name="arguments">The arguments, one per parameter, passed to the function as they are.</param>
        /// <param name="result">The result, when there is one.</param>
        /// <returns>Whether there is a result.</returns>
        public bool TryInvoke(IReadOnlyList<object> arguments, [MaybeNullWhen(false)] out TResult result)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            if (partial is null)
            {
                result = Function(arguments);
                return true;
            }

            return partial(arguments, out result);
        }

        /// <inheritdoc />
        internal override uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope) =>
            ((ColumnType<TResult>)result).EncodeIn(Function(Decode(arguments, parameters, scope)), scope);

        /// <inheritdoc />
        internal override bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope, out uint cell)
        {
            if (TryInvoke(Decode(arguments, parameters, scope), out var value))
            {
                cell = ((ColumnType<TResult>)result).EncodeIn(value, scope);
                return true;
            }

            cell = 0;
            return false;
        }

        /// <inheritdoc />
        internal override bool TryInvokeValue(object[] arguments, ColumnType[] parameters, out object? value)
        {
            if (TryInvoke(arguments, out var result))
            {
                value = result;
                return true;
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Decodes the argument cells into a new array, which the function may keep.
        /// </summary>
        static object[] Decode(ReadOnlySpan<uint> arguments, ColumnType[] parameters, Scope? scope)
        {
            var values = new object[arguments.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = parameters[i].DecodeObject(arguments[i], scope);

            return values;
        }

    }

}
