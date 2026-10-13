using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A deliberately simple model of egglog's action interpreter, for differential tests of
    /// <see cref="ExecutionState.RunInstrs(ReadOnlySpan{Instr}, Bindings)"/>: one array of values per variable, one
    /// flag per lane, and a loop over the lanes of each instruction in turn, with no masks, no bindings layout, no
    /// gathering and no specialization. Lookups go through <see cref="Table.TryGetRowColumn"/>, staging through plain
    /// buffers of the database, and predictions through a dictionary keyed by text.
    /// </summary>
    /// <remarks>
    /// It follows egglog's semantics (core-relations/src/action/mod.rs:598-882) except in one place, as the engine
    /// does: a row staged by <c>LookupOrInsertDefault</c> sets the changed flag, which egglog leaves unset there
    /// (mod.rs:707).
    /// </remarks>
    sealed class ReferenceActionInterpreter
    {

        const uint Stale = Relations.Value.StaleRep;

        readonly Database db;
        readonly ExecutionState callState;
        readonly Dictionary<string, uint[]> predicted = new();
        readonly Dictionary<TableId, MutationBuffer> buffers = new();

        /// <summary>
        /// Initializes a model running against <paramref name="db"/>.
        /// </summary>
        public ReferenceActionInterpreter(Database db)
        {
            this.db = db;
            callState = db.CreateExecutionState();
        }

        /// <summary>
        /// Gets whether anything was staged.
        /// </summary>
        public bool Changed { get; private set; }

        /// <summary>
        /// Gets the number of rows predicted.
        /// </summary>
        public int PredictedCount => predicted.Count;

        /// <summary>
        /// Runs <paramref name="program"/> over <paramref name="matches"/> lanes whose join variables hold
        /// <paramref name="columns"/>; the variables the program defines are added to <paramref name="columns"/>.
        /// </summary>
        /// <returns>The number of lanes live at the end.</returns>
        public int Run(Instr[] program, int matches, Dictionary<Variable, uint[]> columns)
        {
            if (columns.Count == 0)
                matches = 1;

            var live = Enumerable.Repeat(true, matches).ToArray();
            foreach (var instr in program)
            {
                if (live.Any(x => x) == false)
                    return 0;

                Step(instr, live, columns);
            }

            return live.Count(x => x);
        }

        /// <summary>
        /// Flushes the staged writes and merges the database.
        /// </summary>
        public void Commit()
        {
            foreach (var buffer in buffers.Values)
                buffer.Flush();

            db.MergeAll();
        }

        void Step(Instr instr, bool[] live, Dictionary<Variable, uint[]> cols)
        {
            var n = live.Length;
            switch (instr)
            {
                case LookupInstr i:
                {
                    var dst = Fresh(n);
                    var table = db.GetTable(i.Table);
                    for (var lane = 0; lane < n; lane++)
                    {
                        if (live[lane] == false)
                            continue;

                        if (table.TryGetRowColumn(Values(i.Args, cols, lane), i.Column, out var v))
                            dst[lane] = v;
                        else
                            live[lane] = false;
                    }

                    cols[i.Destination] = dst;
                    break;
                }
                case LookupWithDefaultInstr i:
                {
                    var dst = Fresh(n);
                    var table = db.GetTable(i.Table);
                    for (var lane = 0; lane < n; lane++)
                        if (live[lane])
                            dst[lane] = table.TryGetRowColumn(Values(i.Args, cols, lane), i.Column, out var v) ? v : Read(i.Default, cols, lane);

                    cols[i.Destination] = dst;
                    break;
                }
                case LookupOrInsertDefaultInstr i:
                {
                    var dst = Fresh(n);
                    var table = db.GetTable(i.Table);
                    for (var lane = 0; lane < n; lane++)
                    {
                        if (live[lane] == false)
                            continue;

                        var key = Values(i.Args, cols, lane);
                        if (table.TryGetRowColumn(key, i.Column, out var v))
                        {
                            dst[lane] = v;
                            continue;
                        }

                        var name = i.Table.Rep + ":" + string.Join(",", key);
                        if (predicted.TryGetValue(name, out var row) == false)
                        {
                            row = new uint[key.Length + i.Defaults.Length];
                            key.CopyTo(row, 0);
                            for (var d = 0; d < i.Defaults.Length; d++)
                            {
                                var w = i.Defaults[d];
                                row[key.Length + d] = w.Kind switch
                                {
                                    WriteValKind.Entry => Read(w.Entry, cols, lane),
                                    WriteValKind.IncrementCounter => checked((uint)db.IncrementCounter(w.Counter)),
                                    _ => row[w.Column],
                                };
                            }

                            Buffer(i.Table).StageInsert(row);
                            Changed = true;
                            predicted[name] = row;
                        }

                        dst[lane] = row[i.Column.Rep];
                    }

                    cols[i.Destination] = dst;
                    break;
                }
                case LookupWithFallbackInstr i:
                {
                    var dst = Fresh(n);
                    var table = db.GetTable(i.Table);
                    var missed = new bool[n];
                    for (var lane = 0; lane < n; lane++)
                    {
                        if (live[lane] == false)
                            continue;

                        if (table.TryGetRowColumn(Values(i.Key, cols, lane), i.Column, out var v))
                            dst[lane] = v;
                        else
                            missed[lane] = true;
                    }

                    cols[i.Destination] = dst;
                    Fallback(db.GetExternalFunction(i.Function), i.FunctionArgs, missed, live, dst, cols);
                    break;
                }
                case InsertInstr i:
                    for (var lane = 0; lane < n; lane++)
                        if (live[lane])
                            Stage(i.Table, Values(i.Values, cols, lane), false);
                    break;
                case InsertIfEqInstr i:
                    for (var lane = 0; lane < n; lane++)
                        if (live[lane] && Read(i.Left, cols, lane) == Read(i.Right, cols, lane))
                            Stage(i.Table, Values(i.Values, cols, lane), false);
                    break;
                case RemoveInstr i:
                    for (var lane = 0; lane < n; lane++)
                        if (live[lane])
                            Stage(i.Table, Values(i.Args, cols, lane), true);
                    break;
                case ExternalInstr i:
                {
                    var dst = Fresh(n);
                    var f = db.GetExternalFunction(i.Function);
                    for (var lane = 0; lane < n; lane++)
                    {
                        if (live[lane] == false)
                            continue;

                        if (f.TryInvoke(callState, Values(i.Args, cols, lane), out var v))
                            dst[lane] = v;
                        else
                            live[lane] = false;
                    }

                    cols[i.Destination] = dst;
                    break;
                }
                case ExternalWithFallbackInstr i:
                {
                    var dst = Fresh(n);
                    var f = db.GetExternalFunction(i.Function);
                    var failed = new bool[n];
                    for (var lane = 0; lane < n; lane++)
                    {
                        if (live[lane] == false)
                            continue;

                        if (f.TryInvoke(callState, Values(i.Args, cols, lane), out var v))
                            dst[lane] = v;
                        else
                            failed[lane] = true;
                    }

                    cols[i.Destination] = dst;
                    Fallback(db.GetExternalFunction(i.Fallback), i.FallbackArgs, failed, live, dst, cols);
                    break;
                }
                case AssertEqInstr i:
                    for (var lane = 0; lane < n; lane++)
                        live[lane] &= Read(i.Left, cols, lane) == Read(i.Right, cols, lane);
                    break;
                case AssertNeInstr i:
                    for (var lane = 0; lane < n; lane++)
                        live[lane] &= Read(i.Left, cols, lane) != Read(i.Right, cols, lane);
                    break;
                case AssertAnyNeInstr i:
                    for (var lane = 0; lane < n; lane++)
                    {
                        if (live[lane] == false)
                            continue;

                        var ops = Values(i.Ops, cols, lane);
                        var any = false;
                        for (var p = 0; p < Math.Min(i.Divider, ops.Length - i.Divider); p++)
                            any |= ops[p] != ops[i.Divider + p];

                        live[lane] = any;
                    }

                    break;
                case ReadCounterInstr i:
                    cols[i.Destination] = Enumerable.Repeat(checked((uint)db.ReadCounter(i.Counter)), n).ToArray();
                    break;
                default:
                    throw new InvalidOperationException("Unknown instruction " + instr);
            }
        }

        /// <summary>
        /// Calls <paramref name="f"/> on the lanes marked in <paramref name="pending"/>, in lane order, writing results
        /// into <paramref name="dst"/> and dropping lanes where it fails.
        /// </summary>
        void Fallback(ExternalFunction f, QueryEntry[] args, bool[] pending, bool[] live, uint[] dst, Dictionary<Variable, uint[]> cols)
        {
            for (var lane = 0; lane < pending.Length; lane++)
            {
                if (pending[lane] == false)
                    continue;

                if (f.TryInvoke(callState, Values(args, cols, lane), out var v))
                    dst[lane] = v;
                else
                    live[lane] = false;
            }
        }

        void Stage(TableId table, uint[] cells, bool remove)
        {
            if (remove)
                Buffer(table).StageRemove(cells);
            else
                Buffer(table).StageInsert(cells);

            Changed = true;
        }

        MutationBuffer Buffer(TableId table)
        {
            if (buffers.TryGetValue(table, out var buffer) == false)
                buffers[table] = buffer = db.NewBuffer(table);

            return buffer;
        }

        static uint[] Fresh(int n) => Enumerable.Repeat(Stale, n).ToArray();

        static uint Read(QueryEntry entry, Dictionary<Variable, uint[]> cols, int lane) => entry.IsVariable ? cols[entry.Variable][lane] : entry.Constant.Rep;

        static uint[] Values(QueryEntry[] entries, Dictionary<Variable, uint[]> cols, int lane) => entries.Select(e => Read(e, cols, lane)).ToArray();

    }

}
