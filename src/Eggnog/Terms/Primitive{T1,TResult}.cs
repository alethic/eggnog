using System;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    /// <summary>
    /// A primitive of 1 parameter: <see cref="Of"/> applies it to terms.
    /// </summary>
    /// <typeparam name="T1">The first parameter type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    public sealed class Primitive<T1, TResult> : Primitive
    {

        readonly PartialFunc<T1, TResult>? partial;
        readonly Func<ColumnType, uint, (bool Found, uint Cell)>? cells;
        readonly Func<ColumnType, T1, (bool Found, TResult Value)>? values;

        /// <summary>
        /// Initializes a new instance of a total primitive.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function.</param>
        /// <param name="isLambda">Whether it is an anonymous lambda.</param>
        internal Primitive(string name, Func<T1, TResult> function, bool isLambda)
            : base(name, new[] { typeof(T1) }, typeof(TResult), isLambda, false)
        {
            Function = function;
        }

        /// <summary>
        /// Initializes a new instance of a named partial primitive.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function, which throws where there is no result.</param>
        /// <param name="partial">The partial function, which says where there is no result.</param>
        internal Primitive(string name, Func<T1, TResult> function, PartialFunc<T1, TResult> partial)
            : base(name, new[] { typeof(T1) }, typeof(TResult), false, true)
        {
            Function = function;
            this.partial = partial;
        }

        /// <summary>
        /// Initializes a new instance of a partial primitive over its argument's cell, whose result is a cell the argument
        /// holds: egglog's primitives that read a container's order of cells, such as <c>multiset-pick</c>. Its function
        /// throws: it needs the graph's cells.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="cells">The function over the argument's cell, given the argument's column type: whether there is a
        /// result, and its cell.</param>
        /// <param name="values">The function over a value that may not be interned, given the argument's column type, for a
        /// read, which never interns: whether there is a result, and the result.</param>
        internal Primitive(
            string name,
            Func<ColumnType, uint, (bool Found, uint Cell)> cells,
            Func<ColumnType, T1, (bool Found, TResult Value)> values)
            : base(name, new[] { typeof(T1) }, typeof(TResult), false, true)
        {
            Function = _ => throw new InvalidOperationException(
                $"Primitive '{name}' reads the graph's cells; it has no function of values alone.");
            this.cells = cells;
            this.values = values;
        }

        /// <summary>
        /// Gets the function. For a partial primitive (<see cref="Primitive.IsPartial"/>) it throws where there is no
        /// result, and for one over the graph's cells, such as <see cref="Primitives.MultiSetPick{T}"/>, it always
        /// throws <see cref="InvalidOperationException"/>.
        /// </summary>
        public Func<T1, TResult> Function { get; }

        /// <summary>
        /// The primitive applied to terms: a computed value in a body (a let or a check) or a head.
        /// </summary>
        /// <param name="a">The first argument.</param>
        /// <returns>The term.</returns>
        public Term<TResult> Of(Term<T1> a)
        {
            ArgumentNullException.ThrowIfNull(a);
            return new ComputeTerm<TResult>(this, new Term[] { a });
        }

        /// <summary>
        /// Calls the primitive on values without throwing for a missing result: the partial function for a partial
        /// primitive (<see cref="Primitive.IsPartial"/>), else the function, which always has a result.
        /// </summary>
        /// <param name="a">The first argument.</param>
        /// <param name="result">The result, when there is one.</param>
        /// <returns>Whether there is a result.</returns>
        /// <exception cref="InvalidOperationException">The primitive reads the graph's cells, as
        /// <see cref="Primitives.MultiSetPick{T}"/> does; it runs only in a graph.</exception>
        public bool TryInvoke(T1 a, [MaybeNullWhen(false)] out TResult result)
        {
            if (cells is not null)
                throw new InvalidOperationException($"Primitive '{Name}' reads the graph's cells; it has no function of values alone.");

            if (partial is null)
            {
                result = Function(a);
                return true;
            }

            return partial(a, out result);
        }

        /// <inheritdoc />
        internal override uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope)
        {
            if (cells is not null)
            {
                var (found, picked) = cells(parameters[0], arguments[0]);
                return found ? picked : throw NoResult(Name);
            }

            return ((ColumnType<TResult>)result).EncodeIn(Function(((ColumnType<T1>)parameters[0]).Decode(arguments[0], scope)), scope);
        }

        /// <inheritdoc />
        internal override bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope, out uint cell)
        {
            if (cells is not null)
            {
                bool found;
                (found, cell) = cells(parameters[0], arguments[0]);
                return found;
            }

            var a = ((ColumnType<T1>)parameters[0]).Decode(arguments[0], scope);
            if (TryInvoke(a, out var value))
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
            if (values is not null)
            {
                var (found, picked) = values(parameters[0], (T1)arguments[0]);
                value = found ? picked : null;
                return found;
            }

            if (TryInvoke((T1)arguments[0], out var result))
            {
                value = result;
                return true;
            }

            value = null;
            return false;
        }

    }

}
