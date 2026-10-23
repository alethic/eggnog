using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Eggnog
{

    /// <summary>
    /// A <see cref="VariadicPrimitive{T, TResult}"/> applied to a fixed number of arguments: the primitive a rule calls,
    /// one per arity.
    /// </summary>
    /// <typeparam name="T">The arguments' type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    internal sealed class VariadicCall<T, TResult> : Primitive
    {

        readonly Func<IReadOnlyList<T>, TResult> function;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="arity">The number of arguments.</param>
        /// <param name="function">The function over all the arguments.</param>
        public VariadicCall(string name, int arity, Func<IReadOnlyList<T>, TResult> function)
            : base(name, new RepeatedTypes(typeof(T), arity), typeof(TResult), false, false)
        {
            this.function = function;
        }

        /// <inheritdoc />
        internal override uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope) =>
            ((ColumnType<TResult>)result).EncodeIn(Call(arguments, parameters, scope), scope);

        /// <summary>
        /// Decodes the arguments into a pooled array and calls the function over them, which must not keep the list: it
        /// is valid only during the call.
        /// </summary>
        TResult Call(ReadOnlySpan<uint> arguments, ColumnType[] parameters, Scope? scope)
        {
            var values = ArrayPool<T>.Shared.Rent(arguments.Length);
            try
            {
                for (var i = 0; i < arguments.Length; i++)
                    values[i] = ((ColumnType<T>)parameters[i]).Decode(arguments[i], scope);

                return function(new ArraySegment<T>(values, 0, arguments.Length));
            }
            finally
            {
                ArrayPool<T>.Shared.Return(values, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            }
        }

        /// <inheritdoc />
        internal override bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, Scope? scope, out uint cell)
        {
            cell = InvokeCore(arguments, parameters, result, scope);
            return true;
        }

        /// <inheritdoc />
        internal override bool TryInvokeValue(object[] arguments, ColumnType[] parameters, Relations.ContainerView view, out object? value)
        {
            var values = new T[arguments.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = (T)arguments[i];

            value = function(values);
            return true;
        }
    }

}
