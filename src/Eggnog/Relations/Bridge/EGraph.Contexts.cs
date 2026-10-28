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
        /// The early-stop function (<see cref="StopOnContradictionFunction"/>), registered with the contradiction
        /// function.
        /// </summary>
        ExternalFunctionId stopOnContradiction;

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
        /// Gets the early-stop function, or <c>null</c> when no contradiction function is designated.
        /// </summary>
        internal ExternalFunctionId? StopOnContradiction => Contradiction is null ? null : stopOnContradiction;

        /// <summary>
        /// Gets the contradiction function's table and the early-stop function, which rule lowering appends after every
        /// head write into that table (an insert or a lookup-or-insert); <c>null</c> when no contradiction function is
        /// designated.
        /// </summary>
        internal (TableId Table, ExternalFunctionId Stop)? ContradictionHook => Contradiction is { } c ? (GetTable(c), stopOnContradiction) : null;

        /// <summary>
        /// Gets the threads that concurrent children lease from: as many as the degree of parallelism the e-graph was
        /// built with (<see cref="ParallelHeuristics.DegreeOfParallelism"/> of the root's heuristics).
        /// <see cref="ConcurrentChildren"/> runs each child's body under a lease of the child's own degree of
        /// parallelism, so children running side by side never use more threads than the root alone may.
        /// </summary>
        internal ParallelismBudget ParallelismBudget => parallelismBudget;

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
        /// Rules planned after the designation follow every head write into the function (an insert or a
        /// lookup-or-insert) with the early-stop call (<see cref="StopOnContradictionFunction"/>), which cuts a stopping
        /// case's search short; a rule planned
        /// before the designation keeps its plan, so its write only sets the stop flag when it merges. The rule layer
        /// designates its contradiction relation when it builds the graph, before any rule runs.
        /// </para>
        /// <para>
        /// Refused once the store has had a child: a child created before the designation would have missed the check
        /// made at its creation (whether its parent's view holds a contradiction row), and plans run in it would lack
        /// the early stop.
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

                stopOnContradiction = RegisterExternalFunction(new StopOnContradictionFunction());
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
