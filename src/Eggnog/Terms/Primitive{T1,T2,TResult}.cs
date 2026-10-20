using System;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    /// <summary>
    /// A primitive of 2 parameters: <see cref="Of"/> applies it to terms.
    /// </summary>
    /// <typeparam name="T1">The first parameter type.</typeparam>
    /// <typeparam name="T2">The second parameter type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    public sealed class Primitive<T1, T2, TResult> : Primitive
    {

        readonly PartialFunc<T1, T2, TResult>? partial;

        /// <summary>
        /// Initializes a new instance of a total primitive.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function.</param>
        /// <param name="isLambda">Whether it is an anonymous lambda.</param>
        internal Primitive(string name, Func<T1, T2, TResult> function, bool isLambda)
            : base(name, new[] { typeof(T1), typeof(T2) }, typeof(TResult), isLambda, false)
        {
            Function = function;
        }

        /// <summary>
        /// Initializes a new instance of a named partial primitive.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function, which throws where there is no result.</param>
        /// <param name="partial">The partial function, which says where there is no result.</param>
        internal Primitive(string name, Func<T1, T2, TResult> function, PartialFunc<T1, T2, TResult> partial)
            : base(name, new[] { typeof(T1), typeof(T2) }, typeof(TResult), false, true)
        {
            Function = function;
            this.partial = partial;
        }

        /// <summary>
        /// Gets the function. For a partial primitive (<see cref="Primitive.IsPartial"/>) it throws where there is no
        /// result.
        /// </summary>
        public Func<T1, T2, TResult> Function { get; }

        /// <summary>
        /// The primitive applied to terms: a computed value in a body (a let or a check) or a head.
        /// </summary>
        /// <param name="a">The first argument.</param>
        /// <param name="b">The second argument.</param>
        /// <returns>The term.</returns>
        public Term<TResult> Of(Term<T1> a, Term<T2> b)
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);
            return new ComputeTerm<TResult>(this, new Term[] { a, b });
        }

        /// <summary>
        /// Calls the primitive on values: the partial function for a partial primitive, else the function.
        /// </summary>
        /// <param name="a">The first argument.</param>
        /// <param name="b">The second argument.</param>
        /// <param name="result">The result, when there is one.</param>
        /// <returns>Whether there is a result.</returns>
        internal bool TryInvoke(T1 a, T2 b, [MaybeNullWhen(false)] out TResult result)
        {
            if (partial is null)
            {
                result = Function(a, b);
                return true;
            }

            return partial(a, b, out result);
        }

        /// <inheritdoc />
        internal override uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result) =>
            ((ColumnType<TResult>)result).Encode(Function(((ColumnType<T1>)parameters[0]).Decode(arguments[0], null), ((ColumnType<T2>)parameters[1]).Decode(arguments[1], null)));

        /// <inheritdoc />
        internal override bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, out uint cell)
        {
            var a = ((ColumnType<T1>)parameters[0]).Decode(arguments[0], null);
            var b = ((ColumnType<T2>)parameters[1]).Decode(arguments[1], null);
            if (TryInvoke(a, b, out var value))
            {
                cell = ((ColumnType<TResult>)result).Encode(value);
                return true;
            }

            cell = 0;
            return false;
        }

    }

}
