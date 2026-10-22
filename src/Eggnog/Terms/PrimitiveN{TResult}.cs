using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    /// <summary>
    /// A primitive of any number of parameters, each of its own type, as egglog's primitives take, where the typed
    /// primitives stop at four: <see cref="Of"/> applies it to terms. Made by
    /// <see cref="Primitive.Create{TResult}(string, IReadOnlyList{Type}, FuncN{TResult})"/> and
    /// <see cref="Primitive.CreatePartial{TResult}(string, IReadOnlyList{Type}, PartialFuncN{TResult})"/>. Its functions
    /// take the arguments as a span of objects, one per parameter, each of its parameter's type, valid only during the
    /// call: a call from a rule or a host write decodes them into a pooled buffer, cleared after the call. Such a call
    /// boxes its values, where a typed primitive's allocates nothing.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    public sealed class PrimitiveN<TResult> : Primitive
    {

        readonly PartialFuncN<TResult>? partial;
        readonly TableFuncN<ITables, TResult>? tables;

        /// <summary>
        /// Initializes a new instance of a named primitive.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="parameters">The parameter types.</param>
        /// <param name="function">The function, which throws where a partial primitive has no result.</param>
        /// <param name="partial">The partial function, which says where there is no result; <c>null</c> for a total
        /// primitive.</param>
        internal PrimitiveN(string name, Type[] parameters, FuncN<TResult> function, PartialFuncN<TResult>? partial)
            : base(name, parameters, typeof(TResult), false, partial is not null)
        {
            Function = function;
            this.partial = partial;
        }

        /// <summary>
        /// Initializes a new instance of a primitive over the tables (<see cref="Primitive.CreateRead{TResult}"/> and its
        /// kin), partial as egglog's read, write and full primitives are.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="parameters">The parameter types.</param>
        /// <param name="access">What it may do to the tables: not <see cref="PrimitiveAccess.Pure"/>.</param>
        /// <param name="tables">The function over the tables.</param>
        internal PrimitiveN(string name, Type[] parameters, PrimitiveAccess access, TableFuncN<ITables, TResult> tables)
            : base(name, parameters, typeof(TResult), false, true, access)
        {
            Function = _ => throw new InvalidOperationException(
                $"Primitive '{name}' reads or writes the graph's tables; it runs only in a graph, where its access allows.");
            this.tables = tables;
        }

        /// <summary>
        /// Gets the function. For a partial primitive (<see cref="Primitive.IsPartial"/>) it throws where there is no
        /// result, and for one over the tables (<see cref="Primitive.Access"/> other than
        /// <see cref="PrimitiveAccess.Pure"/>) it always throws <see cref="InvalidOperationException"/>: it needs the
        /// tables.
        /// </summary>
        public FuncN<TResult> Function { get; }

        /// <summary>
        /// The primitive applied to terms: a computed value in a body (a let or a check) or a head.
        /// </summary>
        /// <param name="arguments">One argument per parameter, of the parameter's type: a term, or a value, which becomes
        /// a constant (an instance of the parameter's type).</param>
        /// <returns>The term.</returns>
        /// <exception cref="ArgumentException">The number of arguments is not the primitive's, or an argument's type is
        /// not its parameter's.</exception>
        public Term<TResult> Of(params ReadOnlySpan<object> arguments) =>
            new ComputeTerm<TResult>(this, BoxedConstantTerm.Convert(arguments, ParameterTypes, Name));

        /// <summary>
        /// Calls the primitive on values without throwing for a missing result: the partial function for a partial
        /// primitive, else the function, which always has a result.
        /// </summary>
        /// <param name="arguments">The arguments, one per parameter, passed to the function as they are.</param>
        /// <param name="result">The result, when there is one.</param>
        /// <returns>Whether there is a result.</returns>
        /// <exception cref="InvalidOperationException">The primitive reads or writes tables, which it gets only in a
        /// graph.</exception>
        public bool TryInvoke(ReadOnlySpan<object> arguments, [MaybeNullWhen(false)] out TResult result)
        {
            if (tables is not null)
            {
                result = Function(arguments);
                return true;
            }

            if (partial is null)
            {
                result = Function(arguments);
                return true;
            }

            return partial(arguments, out result);
        }

        /// <inheritdoc />
        internal override uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope)
        {
            var buffer = Decode(arguments, parameters, scope);
            try
            {
                return ((ColumnType<TResult>)result).EncodeIn(Function(buffer.AsSpan(0, arguments.Length)), scope);
            }
            finally
            {
                Release(buffer, arguments.Length);
            }
        }

        /// <inheritdoc />
        internal override bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope, out uint cell)
        {
            var buffer = Decode(arguments, parameters, scope);
            try
            {
                if (TryInvoke(buffer.AsSpan(0, arguments.Length), out var value))
                {
                    cell = ((ColumnType<TResult>)result).EncodeIn(value, scope);
                    return true;
                }
            }
            finally
            {
                Release(buffer, arguments.Length);
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

        /// <inheritdoc />
        internal override bool TryInvokeTables(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, PrimitiveTables tables, out uint cell)
        {
            if (this.tables is null)
                return base.TryInvokeTables(arguments, parameters, result, tables, out cell);

            var scope = tables.Scope;
            var buffer = Decode(arguments, parameters, scope);
            try
            {
                if (this.tables(tables, buffer.AsSpan(0, arguments.Length), out var value))
                {
                    cell = tables.EncodeResult((ColumnType<TResult>)result, value);
                    return true;
                }
            }
            finally
            {
                Release(buffer, arguments.Length);
            }

            cell = 0;
            return false;
        }

        /// <inheritdoc />
        internal override bool TryInvokeValueTables(object[] arguments, ColumnType[] parameters, PrimitiveTables tables, out object? value)
        {
            if (this.tables is null)
                return base.TryInvokeValueTables(arguments, parameters, tables, out value);

            if (this.tables(tables, arguments, out var result))
            {
                value = result;
                return true;
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Decodes the argument cells into a buffer from the shared pool, at least as long as the arguments, which
        /// <see cref="Release"/> gives back.
        /// </summary>
        static object[] Decode(ReadOnlySpan<uint> arguments, ColumnType[] parameters, Scope? scope)
        {
            var values = ArrayPool<object>.Shared.Rent(arguments.Length);
            try
            {
                for (var i = 0; i < arguments.Length; i++)
                    values[i] = parameters[i].DecodeObject(arguments[i], scope);
            }
            catch
            {
                Release(values, arguments.Length);
                throw;
            }

            return values;
        }

        /// <summary>
        /// Clears the decoded arguments, so the pool holds no value alive, and gives the buffer back.
        /// </summary>
        static void Release(object[] buffer, int length)
        {
            Array.Clear(buffer, 0, length);
            ArrayPool<object>.Shared.Return(buffer);
        }

    }

}
