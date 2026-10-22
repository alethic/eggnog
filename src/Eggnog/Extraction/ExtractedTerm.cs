using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// One node of an extracted term: its head, its class, its tree cost and its arguments, each a decoded value or, for a
    /// sort column, a subterm. Shared subterms are the same instance. Renders as egglog's extraction output.
    /// </summary>
    public sealed class ExtractedTerm
    {

        readonly object[] arguments;
        readonly uint[] cells;
        readonly Scope scope;
        readonly Relations.ExtractedTerm engine;
        string? text;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="head">The constructor.</param>
        /// <param name="class">The node's class, with the extractor scope's origin.</param>
        /// <param name="cost">The tree cost.</param>
        /// <param name="arguments">Each argument: a subterm for a sort column, the decoded value otherwise.</param>
        /// <param name="cells">Each argument's cell.</param>
        /// <param name="scope">The extractor's scope.</param>
        /// <param name="engine">The engine's term, which renders it.</param>
        internal ExtractedTerm(ConstructorHandle head, ClassId @class, ulong cost, object[] arguments, uint[] cells, Scope scope, Relations.ExtractedTerm engine)
        {
            Head = head;
            Class = @class;
            Cost = cost;
            this.arguments = arguments;
            this.cells = cells;
            this.scope = scope;
            this.engine = engine;
        }

        /// <summary>
        /// Gets the node's constructor.
        /// </summary>
        public ConstructorHandle Head { get; }

        /// <summary>
        /// Gets the node's class: canonical in the extractor's scope, whose origin it carries.
        /// </summary>
        public ClassId Class { get; }

        /// <summary>
        /// Gets the tree cost: the node's head and every argument, shared subterms counted as often as they occur.
        /// </summary>
        public ulong Cost { get; }

        /// <summary>
        /// Gets the number of arguments.
        /// </summary>
        public int Arity => arguments.Length;

        /// <summary>
        /// Gets whether the head is <paramref name="head"/>.
        /// </summary>
        /// <param name="head">The constructor.</param>
        /// <returns>Whether it is the head.</returns>
        public bool Is(ConstructorHandle head) => ReferenceEquals(Head, head);

        /// <summary>
        /// Gets whether argument <paramref name="index"/> is a subterm (a sort column).
        /// </summary>
        /// <param name="index">The argument, from 0.</param>
        /// <returns>Whether it is a subterm.</returns>
        public bool IsChild(int index) => ArgumentAt(index) is ExtractedTerm;

        /// <summary>
        /// Gets the subterm at argument <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The argument, from 0.</param>
        /// <returns>The subterm.</returns>
        /// <exception cref="InvalidOperationException">The argument is a value.</exception>
        public ExtractedTerm Child(int index) =>
            ArgumentAt(index) as ExtractedTerm ?? throw new InvalidOperationException($"Argument {index} of {Head.Name} is a value, not a subterm.");

        /// <summary>
        /// Gets argument <paramref name="index"/> decoded: the value, or for a sort column the subterm's class.
        /// </summary>
        /// <typeparam name="T">The argument's type.</typeparam>
        /// <param name="index">The argument, from 0.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException"><typeparamref name="T"/> is not the argument's type.</exception>
        public T Value<T>(int index)
        {
            ArgumentAt(index);
            if (Head.KeyArray[index] is not ColumnType<T> type)
                throw new ArgumentException($"Argument {index} of {Head.Name} is a {Head.KeyArray[index].Name}, not a {ValueStore.DisplayName(typeof(T))}.", nameof(T));

            return type.Decode(cells[index], scope);
        }

        /// <summary>
        /// Gets argument <paramref name="index"/>: an <see cref="ExtractedTerm"/> for a sort column, the decoded value
        /// otherwise.
        /// </summary>
        /// <param name="index">The argument, from 0.</param>
        /// <returns>The argument.</returns>
        public object Argument(int index) => ArgumentAt(index);

        /// <summary>
        /// Gets whether <paramref name="head"/> occurs anywhere in the term.
        /// </summary>
        /// <param name="head">The constructor.</param>
        /// <returns>Whether it occurs.</returns>
        public bool Mentions(ConstructorHandle head)
        {
            ArgumentNullException.ThrowIfNull(head);
            var seen = new HashSet<ExtractedTerm>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<ExtractedTerm>();
            stack.Push(this);
            seen.Add(this);
            while (stack.Count > 0)
            {
                var term = stack.Pop();
                if (ReferenceEquals(term.Head, head))
                    return true;

                foreach (var argument in term.arguments)
                    if (argument is ExtractedTerm child && seen.Add(child))
                        stack.Push(child);
            }

            return false;
        }

        /// <summary>
        /// Gets whether the head is <paramref name="head"/>: for translating a term into another language.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="head">The constructor.</param>
        /// <returns>Whether it is the head.</returns>
        public bool TryMatch<TSort>(Constructor<TSort> head) where TSort : struct, ISort<TSort> => Is(head);

        /// <summary>
        /// Gets whether the head is <paramref name="head"/>, binding its argument.
        /// </summary>
        /// <typeparam name="T1">The argument's type.</typeparam>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="head">The constructor.</param>
        /// <param name="a">The argument, when the result is <c>true</c>.</param>
        /// <returns>Whether it is the head.</returns>
        public bool TryMatch<T1, TSort>(Constructor<T1, TSort> head, out ExtractedArgument<T1> a) where TSort : struct, ISort<TSort>
        {
            if (Is(head) == false)
            {
                a = default;
                return false;
            }

            a = ArgumentOf<T1>(0);
            return true;
        }

        /// <summary>
        /// Gets whether the head is <paramref name="head"/>, binding its arguments.
        /// </summary>
        /// <typeparam name="T1">The first argument's type.</typeparam>
        /// <typeparam name="T2">The second argument's type.</typeparam>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="head">The constructor.</param>
        /// <param name="a">The first argument, when the result is <c>true</c>.</param>
        /// <param name="b">The second argument, when the result is <c>true</c>.</param>
        /// <returns>Whether it is the head.</returns>
        public bool TryMatch<T1, T2, TSort>(Constructor<T1, T2, TSort> head, out ExtractedArgument<T1> a, out ExtractedArgument<T2> b) where TSort : struct, ISort<TSort>
        {
            if (Is(head) == false)
            {
                a = default;
                b = default;
                return false;
            }

            a = ArgumentOf<T1>(0);
            b = ArgumentOf<T2>(1);
            return true;
        }

        /// <summary>
        /// Gets whether the head is <paramref name="head"/>, binding its arguments.
        /// </summary>
        /// <typeparam name="T1">The first argument's type.</typeparam>
        /// <typeparam name="T2">The second argument's type.</typeparam>
        /// <typeparam name="T3">The third argument's type.</typeparam>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="head">The constructor.</param>
        /// <param name="a">The first argument, when the result is <c>true</c>.</param>
        /// <param name="b">The second argument, when the result is <c>true</c>.</param>
        /// <param name="c">The third argument, when the result is <c>true</c>.</param>
        /// <returns>Whether it is the head.</returns>
        public bool TryMatch<T1, T2, T3, TSort>(Constructor<T1, T2, T3, TSort> head, out ExtractedArgument<T1> a, out ExtractedArgument<T2> b, out ExtractedArgument<T3> c)
            where TSort : struct, ISort<TSort>
        {
            if (Is(head) == false)
            {
                a = default;
                b = default;
                c = default;
                return false;
            }

            a = ArgumentOf<T1>(0);
            b = ArgumentOf<T2>(1);
            c = ArgumentOf<T3>(2);
            return true;
        }

        /// <summary>
        /// Gets whether the head is <paramref name="head"/>, binding its arguments.
        /// </summary>
        /// <typeparam name="T1">The first argument's type.</typeparam>
        /// <typeparam name="T2">The second argument's type.</typeparam>
        /// <typeparam name="T3">The third argument's type.</typeparam>
        /// <typeparam name="T4">The fourth argument's type.</typeparam>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="head">The constructor.</param>
        /// <param name="a">The first argument, when the result is <c>true</c>.</param>
        /// <param name="b">The second argument, when the result is <c>true</c>.</param>
        /// <param name="c">The third argument, when the result is <c>true</c>.</param>
        /// <param name="d">The fourth argument, when the result is <c>true</c>.</param>
        /// <returns>Whether it is the head.</returns>
        public bool TryMatch<T1, T2, T3, T4, TSort>(Constructor<T1, T2, T3, T4, TSort> head, out ExtractedArgument<T1> a, out ExtractedArgument<T2> b,
            out ExtractedArgument<T3> c, out ExtractedArgument<T4> d) where TSort : struct, ISort<TSort>
        {
            if (Is(head) == false)
            {
                a = default;
                b = default;
                c = default;
                d = default;
                return false;
            }

            a = ArgumentOf<T1>(0);
            b = ArgumentOf<T2>(1);
            c = ArgumentOf<T3>(2);
            d = ArgumentOf<T4>(3);
            return true;
        }

        /// <summary>
        /// Renders the term as egglog's extraction output: <c>(StrEq "kind" "order")</c>, literals formatted by each column
        /// type's extraction sort.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => text ??= engine.ToString();

        /// <summary>
        /// Gets an argument, checking the index.
        /// </summary>
        object ArgumentAt(int index)
        {
            if ((uint)index >= (uint)arguments.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"{Head.Name} has {arguments.Length} arguments.");

            return arguments[index];
        }

        /// <summary>
        /// Gets an argument as an <see cref="ExtractedArgument{T}"/>.
        /// </summary>
        ExtractedArgument<T> ArgumentOf<T>(int index) => new(Value<T>(index), arguments[index] as ExtractedTerm);

    }

}
