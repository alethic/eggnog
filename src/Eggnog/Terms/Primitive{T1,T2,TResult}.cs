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
        readonly Func<uint, uint, uint>? cells;
        readonly Func<ColumnType, ColumnType, ColumnType, uint, uint, (bool Found, uint Cell)>? containerCells;
        readonly Func<uint, uint, bool>? cellPredicate;

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
        /// Initializes a new instance of a primitive over values whose calls in a run or a write read its arguments' cells
        /// instead, through their column types: a lookup in a container by cell, which needs no decoding. A read, over
        /// values that may not be interned, calls the function over values.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function over values, which throws where there is no result.</param>
        /// <param name="partial">The partial function over values; <c>null</c> for a total primitive.</param>
        /// <param name="containerCells">The function over the cells, given the parameters' and the result's column types:
        /// whether there is a result, and its cell.</param>
        internal Primitive(
            string name,
            Func<T1, T2, TResult> function,
            PartialFunc<T1, T2, TResult>? partial,
            Func<ColumnType, ColumnType, ColumnType, uint, uint, (bool Found, uint Cell)> containerCells)
            : base(name, new[] { typeof(T1), typeof(T2) }, typeof(TResult), false, partial is not null)
        {
            Function = function;
            this.partial = partial;
            this.containerCells = containerCells;
        }

        /// <summary>
        /// Initializes a new instance of a primitive over its arguments' cells, which returns one of them: egglog's
        /// <c>ordering-min</c> and <c>ordering-max</c>, which compare values' representations.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function over values, which throws: it needs the cells.</param>
        /// <param name="cells">The function over the cells.</param>
        internal Primitive(string name, Func<T1, T2, TResult> function, Func<uint, uint, uint> cells)
            : base(name, new[] { typeof(T1), typeof(T2) }, typeof(TResult), false, false)
        {
            Function = function;
            this.cells = cells;
        }

        /// <summary>
        /// Initializes a new instance of a primitive whose <c>bool</c> result is a predicate over its arguments' cells:
        /// egglog's <c>bool-!=</c>, which compares values' representations, so that it compares classes as their
        /// canonical ids. The function over values serves calls outside the graph.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function over values.</param>
        /// <param name="cellPredicate">The predicate over the cells.</param>
        internal Primitive(string name, Func<T1, T2, TResult> function, Func<uint, uint, bool> cellPredicate)
            : base(name, new[] { typeof(T1), typeof(T2) }, typeof(TResult), false, false)
        {
            if (typeof(TResult) != typeof(bool))
                throw new ArgumentException("A cell predicate's primitive returns a bool.", nameof(cellPredicate));

            Function = function;
            this.cellPredicate = cellPredicate;
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
        /// Calls the primitive on values without throwing for a missing result: the partial function for a partial
        /// primitive (<see cref="Primitive.IsPartial"/>), else the function, which always has a result.
        /// </summary>
        /// <param name="a">The first argument.</param>
        /// <param name="b">The second argument.</param>
        /// <param name="result">The result, when there is one.</param>
        /// <returns>Whether there is a result.</returns>
        public bool TryInvoke(T1 a, T2 b, [MaybeNullWhen(false)] out TResult result)
        {
            if (partial is null)
            {
                result = Function(a, b);
                return true;
            }

            return partial(a, b, out result);
        }

        /// <inheritdoc />
        internal override uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope)
        {
            if (cells is not null)
                return cells(arguments[0], arguments[1]);

            if (containerCells is not null)
            {
                var (found, cell) = containerCells(parameters[0], parameters[1], result, arguments[0], arguments[1]);
                return found ? cell : throw NoResult(Name);
            }

            if (cellPredicate is not null)
                return EncodePredicate(result, cellPredicate(arguments[0], arguments[1]));

            return ((ColumnType<TResult>)result).EncodeIn(Function(((ColumnType<T1>)parameters[0]).Decode(arguments[0], scope), ((ColumnType<T2>)parameters[1]).Decode(arguments[1], scope)), scope);
        }

        /// <summary>
        /// Encodes a cell predicate's answer as the <c>bool</c> result it is.
        /// </summary>
        static uint EncodePredicate(ColumnType result, bool answer) =>
            ((ColumnType<TResult>)result).Encode(System.Runtime.CompilerServices.Unsafe.As<bool, TResult>(ref answer));

        /// <inheritdoc />
        internal override bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope, out uint cell)
        {
            if (cells is not null)
            {
                cell = cells(arguments[0], arguments[1]);
                return true;
            }

            if (containerCells is not null)
            {
                bool found;
                (found, cell) = containerCells(parameters[0], parameters[1], result, arguments[0], arguments[1]);
                return found;
            }

            if (cellPredicate is not null)
            {
                cell = EncodePredicate(result, cellPredicate(arguments[0], arguments[1]));
                return true;
            }

            var a = ((ColumnType<T1>)parameters[0]).Decode(arguments[0], scope);
            var b = ((ColumnType<T2>)parameters[1]).Decode(arguments[1], scope);
            if (TryInvoke(a, b, out var value))
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
            if (cells is not null)
            {
                // The result is one of the arguments, chosen by cell: the argument itself, a class with its origin. A
                // value without a cell would intern after every value there is, above every cell its type has, as egglog's
                // check interns it; two such values would compare by the order egglog interns them, which is unknown.
                var hasA = parameters[0].TryEncodeExistingObject(arguments[0], out var a);
                var hasB = parameters[1].TryEncodeExistingObject(arguments[1], out var b);
                if (hasA || hasB)
                {
                    var chosen = cells(hasA ? a : uint.MaxValue, hasB ? b : uint.MaxValue);
                    value = hasA ? (chosen == a ? arguments[0] : arguments[1]) : (chosen == b ? arguments[1] : arguments[0]);
                    return true;
                }

                value = null;
                return false;
            }

            // Two values never interned are compared as values; a class always has its cell.
            if (cellPredicate is not null && parameters[0].TryEncodeExistingObject(arguments[0], out var x) && parameters[1].TryEncodeExistingObject(arguments[1], out var y))
            {
                value = cellPredicate(x, y);
                return true;
            }

            if (TryInvoke((T1)arguments[0], (T2)arguments[1], out var result))
            {
                value = result;
                return true;
            }

            value = null;
            return false;
        }

    }

}
