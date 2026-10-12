using System;

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

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function.</param>
        /// <param name="isLambda">Whether it is an anonymous lambda.</param>
        internal Primitive(string name, Func<T1, T2, TResult> function, bool isLambda)
            : base(name, new[] { typeof(T1), typeof(T2) }, typeof(TResult), isLambda)
        {
            Function = function;
        }

        /// <summary>
        /// Gets the function.
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

        /// <inheritdoc />
        internal override uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result) =>
            ((ColumnType<TResult>)result).Encode(Function(((ColumnType<T1>)parameters[0]).Decode(arguments[0], null), ((ColumnType<T2>)parameters[1]).Decode(arguments[1], null)));

    }

}
