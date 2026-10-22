using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Orders boxed values of any type a column type has been made for, as <see cref="ElementOrder{T}"/> orders that
    /// type: for the arguments a function value holds as objects (<see cref="FnValue.CompareTo"/>), whose types are known
    /// only at run time. Each column type registers its CLR type when it is made, so the order needs no reflection.
    /// </summary>
    internal static class ValueOrder
    {

        static readonly ConcurrentDictionary<Type, Func<object, object, int>> Orders = new();

        /// <summary>
        /// Registers the order of <typeparamref name="T"/>, once.
        /// </summary>
        /// <typeparam name="T">The CLR type of a column type's values.</typeparam>
        public static void Register<T>() =>
            Orders.TryAdd(typeof(T), static (x, y) => ElementOrder<T>.Comparer.Compare((T)x, (T)y));

        /// <summary>
        /// Compares two values: of one registered type as that type orders, else by type name then by
        /// <see cref="Comparer{T}.Default"/>.
        /// </summary>
        /// <param name="x">One value.</param>
        /// <param name="y">The other.</param>
        /// <returns>Negative, zero or positive.</returns>
        /// <exception cref="ArgumentException">The values are of a type no column type orders, and not comparable.</exception>
        public static int Compare(object x, object y)
        {
            var type = x.GetType();
            if (type != y.GetType())
                return Relations.CodePointComparer.Instance.Compare(type.FullName, y.GetType().FullName);

            return Orders.TryGetValue(type, out var order) ? order(x, y) : Comparer<object>.Default.Compare(x, y);
        }

    }

}
