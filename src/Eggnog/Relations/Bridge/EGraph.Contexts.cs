using System;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class EGraph
    {

        /// <summary>
        /// The contradiction function's id plus one; 0 while none is designated.
        /// </summary>
        int contradiction;

        /// <summary>
        /// The number of context-local slots made so far (<see cref="NewLocalSlot{T}"/>).
        /// </summary>
        int localSlotCount;

        /// <summary>
        /// Gets the e-graph's contradiction function, or <c>null</c> when none is designated
        /// (<see cref="DesignateContradiction"/>).
        /// </summary>
        internal FunctionId? Contradiction => Volatile.Read(ref contradiction) is var c and > 0 ? new FunctionId(c - 1) : null;

        /// <summary>
        /// Gets the number of context-local slots made so far.
        /// </summary>
        internal int LocalSlotCount => Volatile.Read(ref localSlotCount);

        /// <summary>
        /// Designates <paramref name="function"/> as the e-graph's contradiction function: a nullary relation whose
        /// row, once it merges into a context that stops on contradictions
        /// (<see cref="ContextOptions.StopOnContradiction"/>, cases), stops that context. Maps to
        /// <see cref="Relations.Store.DesignateContradiction"/>. Once per e-graph.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A rule's head write into the function (an insert or a lookup-or-insert) is an ordinary write: the row stops
        /// the context when the merge after the rule set's run applies it, and the run itself is not cut short. The rule
        /// layer designates its contradiction relation when it builds the graph, before any rule runs.
        /// </para>
        /// <para>
        /// Refused once the store has had a child: a child created before the designation would have missed the check
        /// made at its creation (whether its parent's view holds a contradiction row).
        /// </para>
        /// </remarks>
        /// <param name="function">A function with no key columns: the schema is its output alone (the rule layer's
        /// contradiction relation is <c>[Unit]</c>).</param>
        /// <exception cref="ArgumentException">The function is not declared, or it has key columns.</exception>
        /// <exception cref="InvalidOperationException">A contradiction function is designated already, or the store has
        /// had a child context.</exception>
        public void DesignateContradiction(FunctionId function)
        {
            lock (definitionGate)
            {
                var info = GetFunctionInfo(function);
                if (info.Math.KeyCount != 0)
                    throw new ArgumentException($"The contradiction function must be nullary; {info.Name} has {info.Math.KeyCount} key column(s).", nameof(function));

                if (contradiction != 0)
                    throw new InvalidOperationException($"{GetFunctionName(new FunctionId(contradiction - 1))} is the e-graph's contradiction function already.");

                // Atomic with child creation: the first child notes itself under the store's definition lock before its
                // constructor reads the contradiction table (Context.AddChild), so either this designation lands first
                // and every child checks it at its creation, or the refusal below sees that child.
                lock (store.DefinitionLock)
                {
                    if (store.HasHadChildren)
                        throw new InvalidOperationException("The contradiction function must be designated before any child context exists.");

                    store.DesignateContradiction(info.Table);
                }

                Volatile.Write(ref contradiction, function.Index + 1);
            }
        }

        /// <summary>
        /// Makes a slot of per-context state: every context of the e-graph holds one value of type
        /// <typeparamref name="T"/> for it, built by <paramref name="factory"/> the first time the context is asked
        /// (<see cref="EGraphContext.GetLocal{T}"/>). Thread-safe.
        /// </summary>
        /// <typeparam name="T">The type of the per-context value.</typeparam>
        /// <param name="factory">Builds a context's value; may run on any thread that first asks, and more than once when
        /// threads race (one value is kept).</param>
        /// <returns>The slot.</returns>
        public ContextLocalSlot<T> NewLocalSlot<T>(Func<EGraphContext, T> factory)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(factory);
            return new ContextLocalSlot<T>(this, Interlocked.Increment(ref localSlotCount) - 1, factory);
        }

    }

}
