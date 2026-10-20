using System;
using System.Collections.Generic;

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
            : base(name, Repeat(arity), typeof(TResult), false, false)
        {
            this.function = function;
        }

        /// <inheritdoc />
        internal override uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result)
        {
            var values = new T[arguments.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = ((ColumnType<T>)parameters[i]).Decode(arguments[i], null);

            return ((ColumnType<TResult>)result).Encode(function(values));
        }

        /// <inheritdoc />
        internal override bool TryInvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, out uint cell)
        {
            cell = InvokeCore(arguments, parameters, result);
            return true;
        }

        /// <summary>
        /// Gets the parameter types of an arity.
        /// </summary>
        static Type[] Repeat(int arity)
        {
            var types = new Type[arity];
            Array.Fill(types, typeof(T));
            return types;
        }

    }

}
