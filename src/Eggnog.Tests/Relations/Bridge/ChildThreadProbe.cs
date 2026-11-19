using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Records on which thread the engine calls user code for the children that host tasks drive. A task enters its
    /// child (<see cref="Enter"/>) before it runs the child's program; the probe's functions, <see cref="Primitive"/> for
    /// a rule to call on every match and <see cref="Min"/> for a lattice's merge, then count every call made for that
    /// child by the database of the calling state, and count as stray every call that arrives on a thread other than
    /// the task's. A child that runs its rules, its merges and its rebuilds on its task's thread alone has no stray
    /// call.
    /// </summary>
    /// <remarks>
    /// Calls for contexts no task entered (the root, a serial replay) are not counted. Thread-safe: the functions run on
    /// any thread, and tasks enter their children concurrently. Cost per call: a few dictionary operations.
    /// </remarks>
    sealed class ChildThreadProbe
    {

        readonly ConcurrentDictionary<Database, int> owners = new();
        readonly ConcurrentDictionary<Database, int> calls = new();
        readonly ConcurrentDictionary<Database, int> merges = new();
        readonly ConcurrentDictionary<Database, int> strays = new();

        /// <summary>
        /// Registers the probe's functions on <paramref name="g"/>'s e-graph.
        /// </summary>
        public ChildThreadProbe(ContextGraph g)
        {
            var unit = g.Unit.Rep;
            Primitive = BridgeTestUtil.Register(g.EGraph, (state, _) =>
            {
                Observe(state);
                return unit;
            });
            Min = BridgeTestUtil.Register(g.EGraph, (state, args) =>
            {
                Observe(state, merges);
                var values = state.BaseValues;
                var min = Math.Min(values.Unwrap<Int64Codec, long>(new Value(args[0])), values.Unwrap<Int64Codec, long>(new Value(args[1])));
                return values.Get<Int64Codec, long>(min).Rep;
            });
        }

        /// <summary>
        /// Gets a primitive of no arguments that returns the unit value and records the call.
        /// </summary>
        public ExternalFunctionId Primitive { get; }

        /// <summary>
        /// Gets a primitive that returns the smaller of two i64 values and records the call as a merge: a lattice's
        /// merge function as <c>MergeFn.Primitive(Min, MergeFn.Old, MergeFn.New)</c>.
        /// </summary>
        public ExternalFunctionId Min { get; }

        /// <summary>
        /// Records that the calling thread drives <paramref name="child"/>: every call made for it from now on must
        /// arrive on this thread.
        /// </summary>
        /// <param name="child">The child.</param>
        public void Enter(EGraphContext child) => owners[child.Context.Database] = Environment.CurrentManagedThreadId;

        /// <summary>
        /// Counts a call made with <paramref name="state"/>, for the context whose database the state runs against,
        /// and counts it stray when it arrives on a thread other than the one that entered that context; does nothing
        /// for a context no task entered. The probe's own functions call it; another function may too.
        /// </summary>
        /// <param name="state">The calling state.</param>
        public void Observe(ExecutionState state) => Observe(state, null);

        /// <summary>
        /// Gets the number of calls made for <paramref name="child"/>, merges included.
        /// </summary>
        public int CallsOf(EGraphContext child) => calls.GetValueOrDefault(child.Context.Database);

        /// <summary>
        /// Gets the number of calls of <see cref="Min"/> made for <paramref name="child"/>.
        /// </summary>
        public int MergesOf(EGraphContext child) => merges.GetValueOrDefault(child.Context.Database);

        /// <summary>
        /// Gets the number of calls made for <paramref name="child"/> on a thread other than its task's.
        /// </summary>
        public int StraysOf(EGraphContext child) => strays.GetValueOrDefault(child.Context.Database);

        void Observe(ExecutionState state, ConcurrentDictionary<Database, int>? kind)
        {
            if (state.View?.Owner is not { } database || owners.TryGetValue(database, out var owner) == false)
                return;

            Count(calls, database);
            if (kind is not null)
                Count(kind, database);

            if (Environment.CurrentManagedThreadId != owner)
                Count(strays, database);
        }

        static void Count(ConcurrentDictionary<Database, int> counts, Database database) => counts.AddOrUpdate(database, 1, static (_, n) => n + 1);

    }

}
