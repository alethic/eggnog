using System;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    /// <summary>
    /// A primitive of 3 parameters: <see cref="Of"/> applies it to terms.
    /// </summary>
    /// <typeparam name="T1">The first parameter type.</typeparam>
    /// <typeparam name="T2">The second parameter type.</typeparam>
    /// <typeparam name="T3">The third parameter type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    public sealed class Primitive<T1, T2, T3, TResult> : Primitive
    {

        readonly PartialFunc<T1, T2, T3, TResult>? partial;

        /// <summary>
        /// Initializes a new instance of a total primitive.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function.</param>
        /// <param name="isLambda">Whether it is an anonymous lambda.</param>
        internal Primitive(string name, Func<T1, T2, T3, TResult> function, bool isLambda)
            : base(name, new[] { typeof(T1), typeof(T2), typeof(T3) }, typeof(TResult), isLambda, false)
        {
            Function = function;
        }

        /// <summary>
        /// Initializes a new instance of a named partial primitive.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function, which throws where there is no result.</param>
        /// <param name="partial">The partial function, which says where there is no result.</param>
        internal Primitive(string name, Func<T1, T2, T3, TResult> function, PartialFunc<T1, T2, T3, TResult> partial)
            : base(name, new[] { typeof(T1), typeof(T2), typeof(T3) }, typeof(TResult), false, true)
        {
            Function = function;
            this.partial = partial;
        }

        /// <summary>
        /// Gets the function. For a partial primitive (<see cref="Primitive.IsPartial"/>) it throws where there is no
        /// result.
        /// </summary>
        public Func<T1, T2, T3, TResult> Function { get; }

        /// <summary>
        /// The primitive applied to terms: a computed value in a body (a let or a check) or a head.
        /// </summary>
        /// <param name="a">The first argument.</param>
        /// <param name="b">The second argument.</param>
        /// <param name="c">The third argument.</param>
        /// <returns>The term.</returns>
        public Term<TResult> Of(Term<T1> a, Term<T2> b, Term<T3> c)
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);
            ArgumentNullException.ThrowIfNull(c);
            return new ComputeTerm<TResult>(this, new Term[] { a, b, c });
        }

        /// <summary>
        /// Calls the primitive on values without throwing for a missing result: the partial function for a partial
        /// primitive (<see cref="Primitive.IsPartial"/>), else the function, which always has a result.
        /// </summary>
        /// <param name="a">The first argument.</param>
        /// <param name="b">The second argument.</param>
        /// <param name="c">The third argument.</param>
        /// <param name="result">The result, when there is one.</param>
        /// <returns>Whether there is a result.</returns>
        public bool TryInvoke(T1 a, T2 b, T3 c, [MaybeNullWhen(false)] out TResult result)
        {
            if (partial is null)
            {
                result = Function(a, b, c);
                return true;
            }

            return partial(a, b, c, out result);
        }

        /// <inheritdoc />
        internal override uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result) =>
            ((ColumnType<TResult>)result).Encode(Function(((ColumnType<T1>)parameters[0]).Decode(arguments[0], null), ((ColumnType<T2>)parameters[1]).Decode(arguments[1], null), ((ColumnType<T3>)parameters[2]).Decode(arguments[2], null)));

        /// <inheritdoc />
        internal override bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, out uint cell)
        {
            var a = ((ColumnType<T1>)parameters[0]).Decode(arguments[0], null);
            var b = ((ColumnType<T2>)parameters[1]).Decode(arguments[1], null);
            var c = ((ColumnType<T3>)parameters[2]).Decode(arguments[2], null);
            if (TryInvoke(a, b, c, out var value))
            {
                cell = ((ColumnType<TResult>)result).Encode(value);
                return true;
            }

            cell = 0;
            return false;
        }

        /// <inheritdoc />
        internal override bool TryInvokeValue(object[] arguments, ColumnType[] parameters, out object? value)
        {
            if (TryInvoke((T1)arguments[0], (T2)arguments[1], (T3)arguments[2], out var result))
            {
                value = result;
                return true;
            }

            value = null;
            return false;
        }

    }

}
