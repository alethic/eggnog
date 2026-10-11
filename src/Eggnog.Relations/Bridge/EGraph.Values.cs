using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    public sealed partial class EGraph
    {

        /// <summary>
        /// The number of row positions <see cref="ForEach{TVisitor}"/> scans per batch: egglog's 32 (lib.rs:468), which
        /// bounds the rows passed over after the visitor stops.
        /// </summary>
        const int ScanBatchSize = 32;

        /// <summary>
        /// Loads rows into functions and merges them. egglog's <c>add_values</c> (lib.rs:322-356).
        /// </summary>
        /// <remarks>
        /// Each row holds the function's keys and output, which are not canonicalized. The rows are stamped with the
        /// current timestamp, staged one buffer per table, and merged by <see cref="FlushUpdates"/>, so conflicts go
        /// through the functions' merges and a rebuild follows any union. If a row is malformed nothing is merged.
        /// </remarks>
        /// <param name="values">The rows, each with its function.</param>
        /// <exception cref="ArgumentException">A function is not declared, or a row does not have one value per schema
        /// column.</exception>
        public void AddValues(IEnumerable<(FunctionId Function, Value[] Row)> values)
        {
            ArgumentNullException.ThrowIfNull(values);

            var timestamp = NextTimestamp;
            var buffers = new DenseIdMap<TableId, MutationBuffer>();
            var scratch = new uint[8];
            foreach (var (function, row) in values)
            {
                ArgumentNullException.ThrowIfNull(row, nameof(values));
                var info = GetFunctionInfo(function);
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
                    buffer = db.NewBuffer(info.Table);
                    buffers.Insert(info.Table, buffer);
                }

                buffer.StageInsert(full);
            }

            // Publish the staged rows (egglog drops the buffers) only once every row was accepted.
            foreach (var (_, buffer) in buffers)
                buffer.Flush();

            FlushUpdates();
        }

        /// <summary>
        /// Adds the term <paramref name="function"/>(<paramref name="inputs"/>) and returns its canonical id: a fresh id
        /// for a new term, or the id of the existing one, which the function's merge (the union-id merge, for a
        /// constructor) reconciles with the fresh id. egglog's <c>add_term</c> (lib.rs:358-385).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="inputs">The keys; not canonicalized.</param>
        /// <returns>The canonical id of the term after the merge and any rebuild.</returns>
        /// <exception cref="ArgumentException">The function is not declared, or the number of inputs is not its number of
        /// keys.</exception>
        public Value AddTerm(FunctionId function, ReadOnlySpan<Value> inputs)
        {
            var info = GetFunctionInfo(function);
            var math = info.Math;
            if (inputs.Length != math.KeyCount)
                throw new ArgumentException($"{info.Name} takes {math.KeyCount} keys, not {inputs.Length}.", nameof(inputs));

            var result = FreshId();
            var columns = math.TableColumns;
            Span<uint> row = columns <= 32 ? stackalloc uint[32] : new uint[columns];
            row = row.Slice(0, columns);
            MemoryMarshal.Cast<Value, uint>(inputs).CopyTo(row);
            row[math.OutputColumn] = result.Rep;
            math.WriteTail(row, NextTimestamp);

            var buffer = db.NewBuffer(info.Table);
            buffer.StageInsert(row);
            buffer.Flush();
            FlushUpdates();
            return GetCanonInUf(result);
        }

        /// <summary>
        /// Looks up the output of <paramref name="function"/> for <paramref name="key"/>, which is not canonicalized.
        /// egglog's <c>lookup_id</c> (lib.rs:387-399). One hash probe.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="key">The key.</param>
        /// <returns>The output, or <c>null</c> if the function holds no row for the key.</returns>
        /// <exception cref="ArgumentException">The function is not declared, or the key does not have one value per key
        /// column.</exception>
        public Value? LookupId(FunctionId function, ReadOnlySpan<Value> key)
        {
            var info = GetFunctionInfo(function);
            var math = info.Math;
            if (key.Length != math.KeyCount)
                throw new ArgumentException($"{info.Name} takes {math.KeyCount} keys, not {key.Length}.", nameof(key));

            var table = db.GetTable(info.Table);
            return table.TryGetRowColumn(MemoryMarshal.Cast<Value, uint>(key), new ColumnId(math.OutputColumn), out var value) ? new Value(value) : null;
        }

        /// <summary>
        /// Gets an estimate of the number of rows of <paramref name="function"/>. egglog's <c>approx_table_size</c>, the
        /// database's size estimate (the live row count). O(1).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>The estimate.</returns>
        public int ApproxTableSize(FunctionId function) => db.EstimateSize(GetTable(function));

        /// <summary>
        /// Gets the number of rows of <paramref name="function"/>, subsumed ones included. egglog's <c>table_size</c>.
        /// O(1).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>The row count.</returns>
        public int TableSize(FunctionId function) => db.GetTable(GetTable(function)).Count;

        /// <summary>
        /// Removes every row of <paramref name="function"/>, and every write staged for it. egglog's <c>clear_table</c>
        /// (lib.rs:409-424): the table clears in bulk and moves to a new generation, so cached subsets and indexes are
        /// rebuilt on their next use. Writes staged for other tables are not flushed first.
        /// </summary>
        /// <param name="function">The function.</param>
        public void ClearTable(FunctionId function) => db.ClearTable(GetTable(function));

        /// <summary>
        /// Visits every row of <paramref name="function"/>. egglog's <c>for_each</c> (lib.rs:426-434).
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
        /// Visits the rows of <paramref name="function"/> until <paramref name="predicate"/> returns <c>false</c>.
        /// egglog's <c>for_each_while</c> (lib.rs:436-473).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="predicate">Receives each row; returns <c>false</c> to stop; must not change the e-graph.</param>
        public void ForEachWhile(FunctionId function, FunctionRowPredicate predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            var visitor = new FunctionRowPredicateVisitor(predicate);
            ForEach(function, ref visitor);
        }

        /// <summary>
        /// Visits the rows of <paramref name="function"/>, in table order, until the visitor returns <c>false</c>: the
        /// specialized form of egglog's <c>for_each_while</c> (lib.rs:436-473). Each row is the function's schema columns
        /// and whether it is subsumed.
        /// </summary>
        /// <remarks>
        /// The rows are read in place from the table, in batches of 32 positions; tombstones are skipped. The visitor must
        /// not change the e-graph (egglog's <c>&amp;self</c>). O(table size) plus the visits, without allocation.
        /// </remarks>
        /// <typeparam name="TVisitor">The visitor type; a struct, so the scan is specialized for it.</typeparam>
        /// <param name="function">The function.</param>
        /// <param name="visitor">The visitor; its final state is written back.</param>
        public void ForEach<TVisitor>(FunctionId function, ref TVisitor visitor)
            where TVisitor : struct, IFunctionRowVisitor
        {
            var info = GetFunctionInfo(function);
            var table = db.GetTable(info.Table);
            var all = table.All();
            var sink = new FunctionRowSink<TVisitor>(visitor, info.Math);
            var position = 0;
            while (position >= 0 && sink.Stopped == false)
                position = table.ScanGenericBounded(all.AsRef(), position, ScanBatchSize, ReadOnlySpan<Constraint>.Empty, ref sink);

            all.Dispose();
            visitor = sink.Visitor;
        }

        /// <summary>
        /// Writes every row of every function, timestamps and subsumption cells included, to <paramref name="writer"/>.
        /// egglog's <c>dump_debug_info</c> (lib.rs:475-504), which logs instead. Unlikely to be useful for large tables.
        /// </summary>
        /// <param name="writer">The writer.</param>
        public void DumpDebugInfo(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteLine("=== View Tables ===");
            foreach (var (id, info) in funcs)
            {
                var table = db.GetTable(info.Table);
                var all = table.All();
                using var rows = table.Scan(all.AsRef());
                all.Dispose();
                for (var i = 0; i < rows.Count; i++)
                    writer.WriteLine($"View Table {info.Name} / {id} / {info.Table}: [{string.Join(", ", rows.GetRow(i).ToArray())}]");
            }
        }

    }

}
