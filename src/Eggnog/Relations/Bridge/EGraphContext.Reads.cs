using System;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// The number of row positions <see cref="ForEach{TVisitor}"/> scans per batch: egglog's 32 (lib.rs:468).
        /// </summary>
        const int ScanBatchSize = 32;

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/> in this context (§10.5): the smallest member of its class here.
        /// Requires the context to be clean (<see cref="IsClean"/>). O(depth) probes.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The canonical id.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted, stopped or not clean, or its domain is held
        /// by another thread.</exception>
        public Value Find(Value id)
        {
            var current = context.IsFrozen ? null : context.Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            return new Value(context.UnionFind.Find(id.Rep));
        }

        /// <summary>
        /// Looks up the output of <paramref name="function"/> for <paramref name="key"/> in this context's view (§10.5):
        /// the key's id cells are canonicalized here first, then the row the context sees for it is read (the root's
        /// table at the root; the child's layers through its view, hidden rows filtered, Lemma F). Subsumed rows count.
        /// Requires the context to be clean. O(id cells × depth) finds plus one probe per layer.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="key">One value per key column.</param>
        /// <param name="value">The output, or default.</param>
        /// <returns><c>true</c> if the context sees a row for the key.</returns>
        /// <exception cref="ArgumentException">The function is not declared, or the key's width is not its key
        /// count.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted, stopped or not clean, or its domain is held
        /// by another thread.</exception>
        public bool TryLookup(FunctionId function, ReadOnlySpan<Value> key, out Value value)
        {
            var current = context.IsFrozen ? null : context.Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            var info = owner.GetFunctionInfo(function);
            var math = info.Math;
            if (key.Length != math.KeyCount)
                throw new ArgumentException($"{info.Name} takes {math.KeyCount} keys, not {key.Length}.", nameof(key));

            Span<uint> canonical = key.Length <= MaxStackRow ? stackalloc uint[MaxStackRow] : new uint[key.Length];
            canonical = canonical.Slice(0, key.Length);
            var schema = info.Schema;
            var finder = context.UnionFind;
            for (var i = 0; i < key.Length; i++)
                canonical[i] = schema[i].IsId ? finder.Find(key[i].Rep) : key[i].Rep;

            var output = new ColumnId(math.OutputColumn);
            bool found;
            uint cell;
            if (parent is null)
                found = context.Database.GetTable(info.Table).TryGetRowColumn(canonical, output, out cell);
            else
                found = context.Tables!.GetView(info.Table).TryGetColumn(canonical, output.Rep, out cell);

            value = new Value(cell);
            return found;
        }

        /// <summary>
        /// Gets whether this context sees a row of <paramref name="function"/> for <paramref name="key"/>
        /// (<see cref="TryLookup"/>).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="key">One value per key column.</param>
        /// <returns><c>true</c> if the context sees a row for the key.</returns>
        public bool ContainsKey(FunctionId function, ReadOnlySpan<Value> key) => TryLookup(function, key, out _);

        /// <summary>
        /// Gets the output of <paramref name="function"/> for <paramref name="key"/> in this context, or <c>null</c>:
        /// egglog's <c>lookup_id</c> (lib.rs:387-399) for a context, with the key's id cells canonicalized here
        /// (<see cref="TryLookup"/>).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="key">One value per key column.</param>
        /// <returns>The output, or <c>null</c>.</returns>
        public Value? LookupId(FunctionId function, ReadOnlySpan<Value> key) => TryLookup(function, key, out var value) ? value : null;

        /// <summary>
        /// Gets the number of rows of <paramref name="function"/> this context sees, subsumed ones included (§10.5):
        /// egglog's <c>table_size</c> at the root (O(1)); a child's view's exact visible count (O(hidden rows) the first
        /// time per view). Requires the context to be clean.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>The row count.</returns>
        public int TableSize(FunctionId function)
        {
            var current = context.IsFrozen ? null : context.Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            var table = owner.GetTable(function);
            return parent is null ? context.Database.GetTable(table).Count : checked((int)context.Tables!.GetView(table).VisibleCount);
        }

        /// <summary>
        /// Visits every row of <paramref name="function"/> this context sees (§10.5): egglog's <c>for_each</c>
        /// (lib.rs:426-434) for a context.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="callback">Receives each row; must not change the e-graph.</param>
        public void ForEach(FunctionId function, FunctionRowCallback callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var visitor = new FunctionRowCallbackVisitor(callback);
            ForEach(function, ref visitor);
        }

        /// <summary>
        /// Visits the rows of <paramref name="function"/> this context sees, until the visitor returns <c>false</c>
        /// (§10.5): egglog's <c>for_each_while</c> (lib.rs:436-473) for a context. Each row is the function's schema columns
        /// and whether it is subsumed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// At the root, the table's rows in table order; in a child, each layer of the context's view from the root up,
        /// hidden rows skipped (the hidden-aware scan, §9.3), so every visible row is visited once (I12). Rows are read in
        /// place, in batches of 32 positions; tombstones are skipped. Requires the context to be clean.
        /// </para>
        /// <para>
        /// The visitor runs as a callback of a writable context's thread domain (§8.3): it may read, and writes, rebuilds
        /// with work to do and structural calls on the domain are refused inside it. O(rows of the view) plus the visits,
        /// without allocation.
        /// </para>
        /// </remarks>
        /// <typeparam name="TVisitor">The visitor type; a struct, so the scan is specialized for it.</typeparam>
        /// <param name="function">The function.</param>
        /// <param name="visitor">The visitor; its final state is written back.</param>
        public void ForEach<TVisitor>(FunctionId function, ref TVisitor visitor)
            where TVisitor : struct, IFunctionRowVisitor
        {
            var current = context.IsFrozen ? null : context.Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            var info = owner.GetFunctionInfo(function);
            var sink = new FunctionRowSink<TVisitor>(visitor, info.Math);
            current?.EnterCallback();
            try
            {
                if (parent is null)
                {
                    ScanAll(context.Database.GetTable(info.Table), HiddenRows.None, ref sink);
                }
                else
                {
                    var view = context.Tables!.GetView(info.Table);
                    for (var l = 0; l < view.LayerCount && sink.Stopped == false; l++)
                        if (view.Layers[l] is { } layer)
                            ScanAll(layer.Table, layer.Hidden, ref sink);
                }
            }
            finally
            {
                current?.ExitCallback();
            }

            visitor = sink.Visitor;
        }

        /// <summary>
        /// Gets whether the context is contradictory (§6.9, §10.5): a contradiction stopped it, or a row of the
        /// e-graph's contradiction function is visible in it. The one read a stopped context answers.
        /// </summary>
        public bool IsContradictory => context.IsContradictory;

        /// <summary>
        /// Scans one table's rows, hidden ones skipped, into <paramref name="sink"/> in batches, until it stops.
        /// </summary>
        static void ScanAll<TVisitor>(Table table, HiddenRows hidden, ref FunctionRowSink<TVisitor> sink)
            where TVisitor : struct, IFunctionRowVisitor
        {
            var all = table.All();
            var position = 0;
            while (position >= 0 && sink.Stopped == false)
                position = hidden.IsNone
                    ? table.ScanGenericBounded(all.AsRef(), position, ScanBatchSize, ReadOnlySpan<Constraint>.Empty, ref sink)
                    : table.ScanGenericBounded(all.AsRef(), position, ScanBatchSize, ReadOnlySpan<Constraint>.Empty, hidden, ref sink);

            all.Dispose();
        }

    }

}
