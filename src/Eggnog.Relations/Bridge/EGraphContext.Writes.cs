using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// The widest row built on the stack.
        /// </summary>
        const int MaxStackRow = 32;

        /// <summary>
        /// Stages a row of <paramref name="function"/> (its keys and output) on the context's bridge side, for the next
        /// <see cref="FlushUpdates"/> (colored-contexts spec §10.4, X24). The row's id cells are canonicalized in this
        /// context when it is flushed, at every level, the root included (the rule layer's invariant C).
        /// </summary>
        /// <remarks>
        /// Nothing is visible until the flush; the context is not clean meanwhile (<see cref="IsClean"/>). A stopped
        /// context ignores the row (rule-layer D45). In a child, a function whose merge is not a lattice merge is refused
        /// here, before anything is staged (decision 2). O(row) amortized.
        /// </remarks>
        /// <param name="function">The function.</param>
        /// <param name="row">One value per schema column: the keys, then the output.</param>
        /// <exception cref="ArgumentException">The function is not declared, or the row's width is not its column
        /// count.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen or faulted; the function cannot be written in a
        /// child (decision 2); the domain is held by another thread; or the call comes from inside a callback of the
        /// domain (a raw escape's action or a visitor, §8.3).</exception>
        public void StageRow(FunctionId function, ReadOnlySpan<Value> row)
        {
            ThrowIfUnwritable();
            var domain = context.Domain;
            using var scope = domain.Enter();
            Context.ThrowIfInCallback(domain, nameof(StageRow));
            var info = owner.GetFunctionInfo(function);
            CheckRow(info, row.Length);
            CheckWritable(info);
            if (IsStopped)
                return;

            hostWrites.AddRow(function, MemoryMarshal.Cast<Value, uint>(row), canonicalizeIds: true);
        }

        /// <summary>
        /// Stages the term <paramref name="function"/>(<paramref name="key"/>) with a fresh id as its output and returns
        /// the id (§10.4): minted from the context's id blocks in a child (so it is local, I2), from the store's counter at
        /// the root. After the flush the term's canonical id is <see cref="Find"/> of it: the fresh id for a new key, else
        /// the existing term's class, which the constructor's union merge reconciles.
        /// </summary>
        /// <param name="function">The function, usually a constructor.</param>
        /// <param name="key">One value per key column.</param>
        /// <returns>The fresh id.</returns>
        /// <exception cref="ArgumentException">The function is not declared, or the key's width is not its key
        /// count.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen, faulted or stopped; the function cannot be
        /// written in a child (decision 2); the domain is held by another thread; or the call comes from inside a callback
        /// of the domain (§8.3).</exception>
        public Value StageTerm(FunctionId function, ReadOnlySpan<Value> key)
        {
            ThrowIfUnwritable();
            var domain = context.Domain;
            using var scope = domain.Enter();
            Context.ThrowIfInCallback(domain, nameof(StageTerm));
            context.ThrowIfContradicted();
            return StageTermCore(function, key);
        }

        /// <summary>
        /// Stages the union of <paramref name="a"/> and <paramref name="b"/> for the next <see cref="FlushUpdates"/>
        /// (§10.4). A stopped context ignores it.
        /// </summary>
        /// <param name="a">One id.</param>
        /// <param name="b">The other.</param>
        /// <exception cref="InvalidOperationException">The context is frozen or faulted; the domain is held by another
        /// thread; or the call comes from inside a callback of the domain (§8.3).</exception>
        public void StageUnion(Value a, Value b)
        {
            ThrowIfUnwritable();
            var domain = context.Domain;
            using var scope = domain.Enter();
            Context.ThrowIfInCallback(domain, nameof(StageUnion));
            if (IsStopped)
                return;

            hostWrites.AddUnion(a.Rep, b.Rep);
        }

        /// <summary>
        /// Mints a fresh id in this context (§10.4, §4.2): from its id blocks in a child, from the store's counter at the
        /// root. Thread-safe.
        /// </summary>
        /// <returns>The id.</returns>
        /// <exception cref="InvalidOperationException">The context is frozen, faulted or stopped.</exception>
        /// <exception cref="OverflowException">The store has run out of ids.</exception>
        public Value FreshId() => new(context.NewId());

        /// <summary>
        /// Stages <paramref name="values"/> (<see cref="StageRow"/>) and flushes (<see cref="FlushUpdates"/>), §10.4. If a
        /// row is malformed, nothing is staged.
        /// </summary>
        /// <param name="values">The rows, each with its function.</param>
        /// <exception cref="ArgumentException">A function is not declared, or a row's width is not its column
        /// count.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen or faulted; the domain is held by another
        /// thread; or the call comes from inside a callback of the domain (§8.3), and nothing is staged.</exception>
        /// <exception cref="PanicException">The flush recorded a failure.</exception>
        public void AddValues(IEnumerable<(FunctionId Function, Value[] Row)> values)
        {
            ThrowIfUnwritable();
            var domain = context.Domain;
            using var scope = domain.Enter();
            Context.ThrowIfInCallback(domain, nameof(AddValues));
            AddValuesCore(values);
            FlushUpdates();
        }

        /// <summary>
        /// Adds the term <paramref name="function"/>(<paramref name="inputs"/>) and returns its canonical id here
        /// (§10.4): <see cref="StageTerm"/>, <see cref="FlushUpdates"/>, then <see cref="Find"/> of the fresh id.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="inputs">The key.</param>
        /// <returns>The term's canonical id in this context.</returns>
        /// <exception cref="InvalidOperationException">The context is frozen or faulted; the domain is held by another
        /// thread; or the call comes from inside a callback of the domain (§8.3), and nothing is staged.</exception>
        /// <exception cref="PanicException">The flush recorded a failure.</exception>
        public Value AddTerm(FunctionId function, ReadOnlySpan<Value> inputs)
        {
            ThrowIfUnwritable();
            var domain = context.Domain;
            using var scope = domain.Enter();
            Context.ThrowIfInCallback(domain, nameof(AddTerm));
            var id = StageTerm(function, inputs);
            FlushUpdates();
            return new Value(context.UnionFind.Find(id.Rep));
        }

        /// <summary>
        /// Merges the host writes staged here, and everything else staged in the context, at an epoch of its own, then
        /// rebuilds if the merge unioned anything: egglog's <c>flush_updates</c> (lib.rs:946-959) for a context
        /// (colored-contexts spec §10.4, X24, X38).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A parent that is not frozen is brought up to date first, and the context catches up with it
        /// (<see cref="Context.PrepareForWrite"/>) before anything is staged. Then the staged rows and unions are replayed
        /// into one state stamped with the flush's epoch <c>T</c> (rows not subsumed; the id cells of rows staged through
        /// this API canonicalized here), merged at <c>T</c>, and the clock advances; a flush that grew the union-find
        /// rebuilds. A failure the merge or the rebuild recorded is taken and thrown as <see cref="PanicException"/> (the
        /// e-graph's egglog-compatible <see cref="EGraph.FlushUpdates"/> leaves it to the next run instead). A stopped
        /// context drops what is staged and returns <c>false</c>.
        /// </para>
        /// <para>
        /// An exception escaping a child's flush faults it (X12).
        /// </para>
        /// </remarks>
        /// <returns>Whether the merge changed the context; changes made by the rebuild are not reported.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen or faulted; its domain is held by another
        /// thread; or the call comes from inside an operation of the same domain.</exception>
        /// <exception cref="PanicException">A failure was recorded.</exception>
        public bool FlushUpdates() => FlushCore(throwOnFailure: true);

        /// <summary>
        /// Runs <paramref name="action"/> with a fresh execution state over the context's database, stamped with the
        /// current clock, and flushes what it staged; the writes land at the next <see cref="FlushUpdates"/>. egglog's
        /// <c>with_execution_state</c> (lib.rs:928-944) for a context: a raw escape for one-shot manipulation, with
        /// <see cref="TableAction"/>s and <see cref="UnionAction"/>s, say. Rows staged in a child are canonicalized and
        /// re-stamped when they merge (X38).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Writes land in the order the host issued them (colored-contexts spec §10.4): rows and unions this context staged
        /// earlier through <see cref="HostWrites"/> are flushed first (<see cref="FlushStagedFirst"/>), so an
        /// order-dependent merge sees them before the raw rows. One emptiness test when nothing is staged.
        /// </para>
        /// <para>
        /// The action runs as a callback of the context's domain (<see cref="RunRawAction{TResult}"/>, §8.3), as egglog's
        /// closure borrows the e-graph shared: it may read, and every call that writes or changes a context of the domain
        /// (staging, <see cref="AddValues"/>, <see cref="AddTerm"/>, <see cref="FlushUpdates"/>, <see cref="RunRules"/>, a
        /// rebuild with work to do, a clear) is refused inside it. Its rows, stamped with the clock read before it, would
        /// otherwise merge after writes issued later, or below a watermark a run inside it committed (I31).
        /// </para>
        /// </remarks>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="action">The action.</param>
        /// <returns>The action's result.</returns>
        /// <exception cref="InvalidOperationException">The context is frozen, faulted or stopped, or the domain is held by
        /// another thread.</exception>
        internal TResult WithExecutionState<TResult>(Func<ExecutionState, TResult> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            context.ThrowIfNotWritable();
            using var scope = context.Domain.Enter();
            context.ThrowIfNotWritable();
            FlushStagedFirst();
            return RunRawAction(action);
        }

        /// <summary>
        /// The root's egglog-compatible raw escape, for <see cref="EGraph.WithExecutionState{TResult}"/>: egglog's
        /// <c>with_execution_state</c> (lib.rs:928-944) on the root database, stamped with the current clock, with no
        /// writability check beyond egglog's. Like <see cref="WithExecutionState{TResult}"/> it first flushes what the root
        /// staged through the context API, so writes land in the order the host issued them (§10.4); under egglog's API
        /// alone that is one emptiness test and the timestamp sequence is egglog's. The action runs as a callback of the
        /// root's domain (<see cref="RunRawAction{TResult}"/>), so writes and runs issued from inside it are refused, as
        /// egglog's shared borrow of the e-graph refuses them.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="action">The action.</param>
        /// <returns>The action's result.</returns>
        /// <exception cref="InvalidOperationException">The root's domain is held by another thread.</exception>
        internal TResult WithExecutionStateAtRoot<TResult>(Func<ExecutionState, TResult> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            Debug.Assert(parent is null, "Only the root has the egglog-compatible raw escape.");
            using var scope = context.Domain.Enter();
            FlushStagedFirst();
            return RunRawAction(action);
        }

        /// <summary>
        /// Runs a raw escape's <paramref name="action"/> with a fresh execution state over the context's database, stamped
        /// with the current clock, then flushes the state into the database's pending buffers and records a failure it
        /// reports: <see cref="Database.WithExecutionState{TResult}"/> (egglog's <c>with_execution_state</c>,
        /// mod.rs:446-450), with the action marked as a callback of the context's domain, as <c>Context.Write</c> marks its
        /// batch (§8.3). Every call that writes or changes a context of the domain then refuses inside the action
        /// (<see cref="Context.ThrowIfInCallback"/>); reads and a nested raw escape are allowed. Called with the domain
        /// held. Two counter updates beyond the database's escape, and no allocation beyond the state.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="action">The action.</param>
        /// <returns>The action's result.</returns>
        TResult RunRawAction<TResult>(Func<ExecutionState, TResult> action)
        {
            var database = context.Database;
            var domain = context.Domain;
            var state = database.CreateExecutionState(owner.Store.ReadClock());
            TResult result;
            domain.EnterCallback();
            try
            {
                result = action(state);
            }
            finally
            {
                domain.ExitCallback();
            }

            state.Flush();
            database.RecordFailure(state);
            return result;
        }

        /// <summary>
        /// Removes every row of <paramref name="function"/> from the context's view (§6.7, <see cref="Context.ClearTable"/>),
        /// and every row of it staged here and not flushed: egglog's <c>clear_table</c> at the root (lib.rs:409-424, which
        /// drops what in-flight buffers staged for the table); in a child, the child's layer is cleared and the ancestors'
        /// rows of the function disappear from its views. Refused while the context has live descendants, and in a child
        /// whose parent is not frozen (decision 1, R1); a refused clear drops nothing.
        /// </summary>
        /// <remarks>Rows staged for other functions, and staged unions, are kept for the next flush. O(the clear) plus
        /// O(staged cells).</remarks>
        /// <param name="function">The function.</param>
        /// <exception cref="InvalidOperationException">The clear is refused, or the context is frozen, faulted or
        /// stopped.</exception>
        public void ClearTable(FunctionId function)
        {
            var table = owner.GetTable(function);
            ThrowIfUnwritable();
            using var scope = context.Domain.Enter();
            context.ClearTable(table);
            hostWrites.RemoveRows(function);
        }

        /// <summary>
        /// Interns <paramref name="container"/> in this context and returns its id (colored-contexts spec §10.7): egglog's
        /// <c>get_container_value</c>. At the root the type is registered first if needed (as one holding ids); a child
        /// needs it registered already, since registration is a definition of the e-graph
        /// (<see cref="EGraph.RegisterContainerType{TContainer}"/>). Container ids are global, minted on the store's shared
        /// counter, never local (§4.2). In a child an eq-sort container (one that holds ids) cannot be interned (G-a,
        /// decision 9); a container over base values can.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="container">The container; it must not change afterwards.</param>
        /// <returns>The container's id.</returns>
        /// <exception cref="InvalidOperationException">The context is frozen, faulted or stopped; it is a child and the
        /// type is not registered; or it is a child and the type holds ids (G-a).</exception>
        internal Value GetContainerValue<TContainer>(in TContainer container)
            where TContainer : struct, IContainerValue<TContainer>
        {
            context.ThrowIfNotWritable();
            using var scope = context.Domain.Enter();
            context.ThrowIfNotWritable();
            if (parent is null)
                owner.RegisterContainerType<TContainer>();
            else if (owner.ContainerValues.TryGetTypeId<TContainer>(out _) == false)
                throw new InvalidOperationException($"The container type {typeof(TContainer).Name} must be registered with the e-graph before a child interns one.");

            var database = context.Database;
            var state = database.CreateExecutionState(owner.Store.ReadClock());
            var id = database.Containers.RegisterValue(container, state);
            state.Flush();
            database.RecordFailure(state);
            return new Value(id);
        }

        /// <summary>
        /// The body of <see cref="FlushUpdates"/> and of the e-graph's egglog-compatible <see cref="EGraph.FlushUpdates"/>,
        /// which leaves a failure recorded for the next run (<paramref name="throwOnFailure"/> <c>false</c>).
        /// </summary>
        internal bool FlushCore(bool throwOnFailure)
        {
            ThrowIfUnwritable();
            var domain = context.Domain;
            using var scope = domain.Enter();
            ThrowIfUnwritable();
            Context.ThrowIfInCallback(domain, nameof(FlushUpdates));
            if (IsStopped)
            {
                hostWrites.Clear();
                return false;
            }

            if (parent is { IsFrozen: false } p)
                p.EnsureClean();

            // Catch up before anything is staged (X24): replayed rows are stamped after every ancestor change they could
            // collide with, and canonicalized against the current union-find.
            context.PrepareForWrite();
            if (IsStopped)
            {
                hostWrites.Clear();
                return false;
            }

            var database = context.Database;
            var timestamp = context.BeginEpoch();
            var unionsBefore = context.DisplacedCount;
            bool changed;
            try
            {
                if (hostWrites.IsEmpty == false)
                {
                    var state = database.CreateExecutionState(timestamp);
                    try
                    {
                        hostWrites.ReplayInto(state, timestamp, owner, context.UnionFind);
                    }
                    finally
                    {
                        hostWrites.Clear();
                    }

                    state.Flush();
                    database.RecordFailure(state);
                }

                changed = database.MergeAll(timestamp);
            }
            catch when (IsRoot == false)
            {
                context.Fault();
                throw;
            }

            // As egglog's flush_updates: the clock advances only after the merge succeeded.
            context.EndEpoch();
            if (context.DisplacedCount != unionsBefore)
                context.Rebuild();

            if (throwOnFailure && TakeFailure(out var message))
                throw new PanicException(message);

            return changed;
        }

        /// <summary>
        /// Stages every row of <paramref name="values"/> as it validates it (the body of <see cref="AddValues"/>): when one
        /// is malformed or refused, the rows staged by this call are dropped again, so nothing is staged. No allocation per
        /// row beyond the arena's amortized growth.
        /// </summary>
        void AddValuesCore(IEnumerable<(FunctionId Function, Value[] Row)> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            ThrowIfUnwritable();
            using var scope = context.Domain.Enter();
            var mark = hostWrites.RowCount;
            var stopped = IsStopped;
            try
            {
                foreach (var (function, row) in values)
                {
                    ArgumentNullException.ThrowIfNull(row, nameof(values));
                    var info = owner.GetFunctionInfo(function);
                    if (row.Length != info.Math.FunctionColumns)
                        throw new ArgumentException($"A row of {info.Name} has {info.Math.FunctionColumns} values, not {row.Length}.", nameof(values));

                    CheckWritable(info);
                    if (stopped == false)
                        hostWrites.AddRow(function, MemoryMarshal.Cast<Value, uint>(row.AsSpan()), canonicalizeIds: true);
                }
            }
            catch
            {
                hostWrites.TruncateRows(mark);
                throw;
            }
        }

        /// <summary>
        /// Loads rows at the root the way egglog's <c>add_values</c> does (lib.rs:322-356), for the e-graph's
        /// egglog-compatible <see cref="EGraph.AddValues"/>: each row, keys and output as given, is stamped with the
        /// current clock and staged into a database buffer of its table; the buffers are published once every row was
        /// accepted, then the root flushes (<see cref="FlushCore"/>, which leaves a recorded failure to the next run).
        /// </summary>
        /// <remarks>
        /// Bypasses <see cref="HostWrites"/>, as M8 did: rows replayed as given need no catch-up at the root, so keeping
        /// them bridge-side would only retain a copy of the batch (§14.1: nothing per staged insert). If a row is malformed
        /// nothing of the batch is published. A stopped root ignores the rows.
        /// <para>
        /// Writes land in the order the host issued them (colored-contexts spec §10.4): rows and unions the root staged
        /// earlier through the context API (<see cref="StageRow"/>, <see cref="StageTerm"/>, <see cref="AddValues"/>,
        /// <see cref="StageUnion"/>) are flushed first, in an epoch of their own, so an order-dependent merge
        /// (<c>:merge new</c>, <c>:merge old</c>) sees them before this batch. When nothing is staged bridge-side, as
        /// under egglog's API alone, this costs one test and the timestamp sequence is egglog's.
        /// </para>
        /// </remarks>
        internal void AddValuesAtRoot(IEnumerable<(FunctionId Function, Value[] Row)> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            Debug.Assert(parent is null, "Only the root loads rows as given.");
            ThrowIfUnwritable();
            var domain = context.Domain;
            using (domain.Enter())
            {
                Context.ThrowIfInCallback(domain, nameof(EGraph.AddValues));
                FlushStagedFirst();
                var database = context.Database;
                var timestamp = owner.Store.ReadClock();
                var buffers = new DenseIdMap<TableId, MutationBuffer>();
                var scratch = new uint[8];
                foreach (var (function, row) in values)
                {
                    ArgumentNullException.ThrowIfNull(row, nameof(values));
                    var info = owner.GetFunctionInfo(function);
                    var math = info.Math;
                    if (row.Length != math.FunctionColumns)
                        throw new ArgumentException($"A row of {info.Name} has {math.FunctionColumns} values, not {row.Length}.", nameof(values));

                    if (scratch.Length < math.TableColumns)
                        scratch = new uint[math.TableColumns];

                    var full = scratch.AsSpan(0, math.TableColumns);
                    MemoryMarshal.Cast<Value, uint>(row.AsSpan()).CopyTo(full);
                    math.WriteTail(full, timestamp);
                    if (buffers.TryGetValue(info.Table, out var buffer) == false)
                    {
                        buffer = database.NewBuffer(info.Table);
                        buffers.Insert(info.Table, buffer);
                    }

                    buffer.StageInsert(full);
                }

                // Publish the staged rows (egglog drops the buffers) only once every row was accepted.
                if (IsStopped)
                    return;

                foreach (var (_, buffer) in buffers)
                    buffer.Flush();
            }

            FlushCore(throwOnFailure: false);
        }

        /// <summary>
        /// Flushes the rows and unions this context staged through the context API (<see cref="HostWrites"/>) before a raw
        /// path (the root's egglog-compatible <see cref="AddValuesAtRoot"/>, <see cref="AddTermAtRoot"/> and
        /// <see cref="EGraph.RunIteration"/>, or <c>WithExecutionState</c> in any context) stages into database buffers of
        /// its own, so that writes merge in the order the host issued them (§10.4). One emptiness test when nothing is
        /// staged; otherwise one <see cref="FlushCore"/>, which leaves a recorded failure to the next run or flush. Called
        /// with the context's domain held.
        /// </summary>
        internal void FlushStagedFirst()
        {
            if (hostWrites.IsEmpty == false)
                FlushCore(throwOnFailure: false);
        }

        /// <summary>
        /// Adds a term at the root the way egglog's <c>add_term</c> does (lib.rs:358-385), for the e-graph's
        /// egglog-compatible <see cref="EGraph.AddTerm"/>: a fresh id from the root's counter, the row (keys as given)
        /// stamped with the current clock and staged into a database buffer, then the root's flush. Bypasses
        /// <see cref="HostWrites"/>, as <see cref="AddValuesAtRoot"/> does, and like it flushes first what the root staged
        /// earlier through the context API, so writes land in the order the host issued them.
        /// </summary>
        /// <returns>The fresh id; its canonical id after the flush is the term's.</returns>
        internal Value AddTermAtRoot(FunctionId function, ReadOnlySpan<Value> inputs)
        {
            Debug.Assert(parent is null, "Only the root adds terms as given.");
            ThrowIfUnwritable();
            var info = owner.GetFunctionInfo(function);
            var math = info.Math;
            if (inputs.Length != math.KeyCount)
                throw new ArgumentException($"{info.Name} takes {math.KeyCount} keys, not {inputs.Length}.", nameof(inputs));

            Value result;
            var domain = context.Domain;
            using (domain.Enter())
            {
                Context.ThrowIfInCallback(domain, nameof(EGraph.AddTerm));
                FlushStagedFirst();
                result = owner.FreshId();
                if (IsStopped)
                    return result;

                var columns = math.TableColumns;
                Span<uint> row = columns <= MaxStackRow ? stackalloc uint[MaxStackRow] : new uint[columns];
                row = row.Slice(0, columns);
                MemoryMarshal.Cast<Value, uint>(inputs).CopyTo(row);
                row[math.OutputColumn] = result.Rep;
                math.WriteTail(row, owner.Store.ReadClock());
                var buffer = context.Database.NewBuffer(info.Table);
                buffer.StageInsert(row);
                buffer.Flush();
            }

            FlushCore(throwOnFailure: false);
            return result;
        }

        /// <summary>
        /// Stages a term with a fresh id from this context as its output and returns the id: the body of
        /// <see cref="StageTerm"/>. The key's id cells are canonicalized at the flush.
        /// </summary>
        Value StageTermCore(FunctionId function, ReadOnlySpan<Value> key)
        {
            ThrowIfUnwritable();
            using var scope = context.Domain.Enter();
            var info = owner.GetFunctionInfo(function);
            var math = info.Math;
            if (key.Length != math.KeyCount)
                throw new ArgumentException($"{info.Name} takes {math.KeyCount} keys, not {key.Length}.", nameof(key));

            CheckWritable(info);
            var id = FreshId();
            Span<uint> row = math.FunctionColumns <= MaxStackRow ? stackalloc uint[MaxStackRow] : new uint[math.FunctionColumns];
            row = row.Slice(0, math.FunctionColumns);
            MemoryMarshal.Cast<Value, uint>(key).CopyTo(row);
            row[math.OutputColumn] = id.Rep;
            hostWrites.AddRow(function, row, canonicalizeIds: true);
            return id;
        }

        /// <summary>
        /// Throws unless a row has one value per schema column of <paramref name="info"/>'s function.
        /// </summary>
        static void CheckRow(FunctionInfo info, int width)
        {
            if (width != info.Math.FunctionColumns)
                throw new ArgumentException($"A row of {info.Name} has {info.Math.FunctionColumns} values, not {width}.", "row");
        }

        /// <summary>
        /// Refuses, in a child, a function whose merge is not a lattice merge (decision 2), before anything is staged: a
        /// write would otherwise fail at the merge and fault the child.
        /// </summary>
        void CheckWritable(FunctionInfo info)
        {
            if (parent is null)
                return;

            if (owner.Store.Schema[info.Table] is { IsLattice: false, IsContextPrivate: false })
                throw new InvalidOperationException($"{info.Name} has a merge that is not a lattice merge, so only the root may write it (decision 2).");
        }

    }

}
