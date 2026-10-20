using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// A primitive of any number of arguments of one type, as egglog's <c>or</c>, string <c>+</c> and <c>set-of</c> are:
    /// <see cref="Of"/> applies it to terms. A pure, deterministic, thread-safe function over values, never partial; it
    /// renders as <c>(name a b …)</c>.
    /// </summary>
    /// <typeparam name="T">The arguments' type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    public sealed class VariadicPrimitive<T, TResult>
    {

        readonly object sync = new();
        VariadicCall<T, TResult>?[] calls = Array.Empty<VariadicCall<T, TResult>?>();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The name it renders as.</param>
        /// <param name="function">The function over all the arguments.</param>
        internal VariadicPrimitive(string name, Func<IReadOnlyList<T>, TResult> function)
        {
            Name = name;
            Function = function;
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the function over all the arguments.
        /// </summary>
        public Func<IReadOnlyList<T>, TResult> Function { get; }

        /// <summary>
        /// The primitive applied to terms: a computed value in a body (a let or a check) or a head.
        /// </summary>
        /// <param name="arguments">The arguments, any number of them.</param>
        /// <returns>The term.</returns>
        public Term<TResult> Of(params Term<T>[] arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            var copy = new Term[arguments.Length];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = arguments[i] ?? throw new ArgumentNullException(nameof(arguments), $"Argument {i} of '{Name}' is null.");

            return new ComputeTerm<TResult>(Call(arguments.Length), copy);
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;

        /// <summary>
        /// Gets the primitive of an arity, made once.
        /// </summary>
        /// <param name="arity">The number of arguments.</param>
        /// <returns>The primitive.</returns>
        internal Primitive Call(int arity)
        {
            var current = Volatile.Read(ref calls);
            if (arity < current.Length && current[arity] is { } existing)
                return existing;

            lock (sync)
            {
                current = calls;
                if (arity < current.Length && current[arity] is { } made)
                    return made;

                var next = new VariadicCall<T, TResult>?[Math.Max(current.Length, arity + 1)];
                current.CopyTo(next, 0);
                var call = new VariadicCall<T, TResult>(Name, arity, Function);
                next[arity] = call;
                Volatile.Write(ref calls, next);
                return call;
            }
        }

    }

}
