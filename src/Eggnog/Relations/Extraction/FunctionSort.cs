using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A function sort over <see cref="FunctionContainer"/>: egglog's <c>FunctionSort</c> as extraction sees it
    /// (src/sort/fn.rs:72-240), whose elements are a value's partially applied arguments and whose term is
    /// <c>(unstable-fn "name" args)</c>. Its inner sorts are those of the partially applied arguments of the values made so
    /// far, as egglog's <c>partial_arcsorts</c> collects them. Thread-safe.
    /// </summary>
    internal sealed class FunctionSort : ContainerSort
    {

        readonly Func<int, (string Name, Sort[] Partial)> target;
        readonly BaseValueTypeId stringType;
        readonly object sync = new();
        Sort[] inner = [];

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <param name="target">Gets the name and the partially applied arguments' sorts of a target's id.</param>
        /// <param name="stringType">The base value type of strings, whose literal names the function.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        public FunctionSort(string name, Func<int, (string Name, Sort[] Partial)> target, BaseValueTypeId stringType)
            : base(name)
        {
            this.target = target;
            this.stringType = stringType;
        }

        /// <inheritdoc />
        public override IReadOnlyList<Sort> InnerSorts => Volatile.Read(ref inner);

        /// <summary>
        /// Adds the sorts of a target's partially applied arguments to the inner sorts, as egglog's <c>unstable-fn</c>
        /// extends <c>partial_arcsorts</c>.
        /// </summary>
        /// <param name="sorts">The sorts.</param>
        public void AddInnerSorts(ReadOnlySpan<Sort> sorts)
        {
            lock (sync)
            {
                var current = inner;
                List<Sort>? grown = null;
                foreach (var sort in sorts)
                {
                    if (Array.IndexOf(current, sort) >= 0 || (grown is not null && grown.Contains(sort)))
                        continue;

                    (grown ??= new List<Sort>(current)).Add(sort);
                }

                if (grown is not null)
                    Volatile.Write(ref inner, grown.ToArray());
            }
        }

        /// <inheritdoc />
        public override void AppendInnerValues(ContainerView containers, Value value, List<SortedValue> destination)
        {
            if (containers.TryGetContainer<FunctionContainer>(value.Rep, out var fn) == false)
                throw new InvalidOperationException($"No function value of sort {Name} has id {value}.");

            var sorts = target(fn.Target).Partial;
            var arguments = fn.Arguments;
            for (var i = 0; i < arguments.Length; i++)
                destination.Add(new SortedValue(sorts[i], new Value(arguments[i])));
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>reconstruct_termdag_container</c> (fn.rs:225-238): the name's literal, then the
        /// arguments.</remarks>
        public override TermId Reconstruct(ContainerView containers, Value value, TermDag dag, ReadOnlySpan<TermId> elements)
        {
            if (containers.TryGetContainer<FunctionContainer>(value.Rep, out var fn) == false)
                throw new InvalidOperationException($"No function value of sort {Name} has id {value}.");

            var name = target(fn.Target).Name;
            var children = new TermId[elements.Length + 1];
            children[0] = dag.Lit(new TermLiteral(stringType, default, LiteralText.String(name)));
            elements.CopyTo(children.AsSpan(1));
            return dag.App("unstable-fn", children);
        }

    }

}
