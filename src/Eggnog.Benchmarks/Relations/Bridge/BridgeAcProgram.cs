using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

using static Eggnog.Relations.RuleTerm;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The program of egglog-bridge's <c>examples/ac.rs</c> (the bridge form of <c>examples/make-ac-bench.py</c>'s
    /// egglog program): commutativity and associativity of <c>add</c> saturate <c>(0 + ... + 12)</c> associated to the
    /// left and <c>(12 + ... + 0)</c> associated to the right into one e-class, on an e-graph with the default
    /// parallelism or a given one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor is the part of the Rust <c>main</c> between <c>let start = web_time::Instant::now()</c> and the
    /// saturation loop, in the same order: <c>EGraph::default()</c> (here
    /// <see cref="Eggnog.Relations.EGraph.EGraph()"/>, which uses every processor as rayon's global pool does), the
    /// <c>i64</c> base type, the constructors <c>num(i64) -&gt; id</c> and <c>add(id, id) -&gt; id</c>
    /// (<see cref="DefaultVal.FreshId"/>, <see cref="MergeFn.UnionId"/>, no subsumption), the two rules, a run on the
    /// empty database that must change nothing, the <c>i64</c> type registered again (a no-op),
    /// the <see cref="N"/> numbers loaded with <see cref="EGraph.AddValues"/> under fresh ids, and both sums loaded with
    /// one more <see cref="EGraph.AddValues"/>. <see cref="Saturate"/> is the loop, which runs until an iteration changes
    /// nothing, and <see cref="Verify"/> the assertion that follows it.
    /// </para>
    /// <para>
    /// The loop saturates after <see cref="ExpectedIterations"/> iterations, the last of which changes nothing, and the
    /// sums are then equal. The saturated e-graph holds one class per subset of at least two of the <see cref="N"/>
    /// numbers and one <c>add</c> row per ordered split of each into two non-empty parts, <c>3^13 - 2^14 + 1</c> =
    /// 1,577,940 rows (<see cref="AcSaturationWorkload.SaturatedRows"/>); egglog does not print that count, but it
    /// follows from the program, so <see cref="Verify"/> checks it too.
    /// </para>
    /// <para>
    /// <c>ac.exe</c> prints one iteration more (0 to 10). Its iteration 9 re-derives the saturated e-graph (about 5.2
    /// million matches, no new row, no new union), but the more than 20,000 rows it stages into <c>add</c> go through
    /// egglog's <c>parallel_insert</c>, which reports a change for every staged row before deciding whether the merge
    /// changes anything (core-relations/src/table/mod.rs:871), so the loop runs iteration 10, which finds no new row
    /// and takes microseconds. The engine's parallel insert (<see cref="SortedWritesTable"/>) reports a change only
    /// where a merge changes a row, so here iteration 9 reports none and is the last. With one rayon thread egglog
    /// would take its serial insert, which reports real changes only, and stop after 10 iterations too.
    /// </para>
    /// </remarks>
    internal sealed class BridgeAcProgram
    {

        /// <summary>
        /// The number of leaves: <c>ac.rs</c>'s <c>const N: usize = 13</c>.
        /// </summary>
        public const int N = 13;

        /// <summary>
        /// The number of iterations the loop runs, the last of which changes nothing (iterations 0 to 9). <c>ac.exe</c>
        /// runs one more, which its parallel insert's spurious change report causes (see the remarks).
        /// </summary>
        public const int ExpectedIterations = 10;

        readonly EGraph egraph;
        readonly FunctionId numTable;
        readonly FunctionId addTable;
        readonly EGraphRuleId addComm;
        readonly EGraphRuleId addAssoc;
        readonly Value leftRoot;
        readonly Value rightRoot;

        /// <summary>
        /// Builds the e-graph on every processor (<c>EGraph::default()</c>) and its rules, checks that the rules change
        /// nothing on the empty database, and loads the two sums, as <c>ac.rs</c> does before its saturation loop. Part
        /// of the time <c>ac.rs</c> measures.
        /// </summary>
        /// <exception cref="InvalidOperationException">Running the rules on the empty database changed it.</exception>
        public BridgeAcProgram()
            : this(ParallelHeuristics.Default)
        {

        }

        /// <summary>
        /// Builds the program as <see cref="BridgeAcProgram()"/> does, on an e-graph whose operations use
        /// <paramref name="parallelism"/>: the counterpart of running <c>ac.exe</c> with <c>RAYON_NUM_THREADS</c> set.
        /// </summary>
        /// <param name="parallelism">When the e-graph's operations go parallel, and on how many threads.</param>
        /// <exception cref="InvalidOperationException">Running the rules on the empty database changed it.</exception>
        public BridgeAcProgram(ParallelHeuristics parallelism)
        {
            egraph = new EGraph(parallelism);
            var intBase = egraph.BaseValues.RegisterType<Int64Codec, long>();
            numTable = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Base(intBase), ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = "num",
                CanSubsume = false,
            });
            addTable = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = "add",
                CanSubsume = false,
            });

            // ((-> (add_table x y) id)) => ((set (add_table y x) id))
            addComm = ExpressionBuilder.DefineRule(egraph,
                [Call(addTable, "x", "y").Is("id")],
                [RuleCommand.Set(Call(addTable, "y", "x"), "id")]);

            // ((-> (add_table x (add_table y z)) id)) => ((set (add_table (add_table x y) z) id))
            addAssoc = ExpressionBuilder.DefineRule(egraph,
                [Call(addTable, "x", Call(addTable, "y", "z")).Is("id")],
                [RuleCommand.Set(Call(addTable, Call(addTable, "x", "y"), "z"), "id")]);

            // Running these rules on an empty database should change nothing.
            if (egraph.RunRules(addComm, addAssoc).Changed)
                throw new InvalidOperationException("Running the AC rules on the empty database changed it.");

            // Fill the database: 0 .. N, each under a fresh id (ac.rs registers the i64 type a second time first).
            egraph.BaseValues.RegisterType<Int64Codec, long>();
            var ids = new List<Value>(N);
            var numRows = new List<(FunctionId, Value[])>(N);
            for (var i = 0; i < N; i++)
            {
                var id = egraph.FreshId();
                var value = egraph.BaseValues.Get<Int64Codec, long>(i);
                ids.Add(id);
                numRows.Add((numTable, [value, id]));
            }

            egraph.AddValues(numRows);

            // (0 + ... + N) left-associated, and (N + ... + 0) right-associated; with the rules saturated they are equal.
            var toAdd = new List<(FunctionId, Value[])>(2 * (N - 1));
            var prev = ids[0];
            for (var i = 1; i < N; i++)
            {
                var id = egraph.FreshId();
                toAdd.Add((addTable, [ids[i], prev, id]));
                prev = id;
            }

            leftRoot = toAdd[^1].Item2[2];
            prev = ids[^1];
            for (var i = 0; i < N - 1; i++)
            {
                var id = egraph.FreshId();
                toAdd.Add((addTable, [prev, ids[i], id]));
                prev = id;
            }

            rightRoot = toAdd[^1].Item2[2];
            egraph.AddValues(toAdd);
        }

        /// <summary>
        /// Gets the e-graph.
        /// </summary>
        public EGraph EGraph => egraph;

        /// <summary>
        /// Runs both rules until an iteration changes nothing: the saturation loop of <c>ac.rs</c>. With
        /// <paramref name="log"/>, writes <c>ac.rs</c>'s line after each iteration
        /// (<c>Finished iteration {i} after {:?}</c>).
        /// </summary>
        /// <param name="log">Where to write the iteration lines; <c>null</c> for none.</param>
        /// <returns>The iterations run, the last one included.</returns>
        public int Saturate(TextWriter? log)
        {
            for (var i = 0; ; i++)
            {
                var start = Stopwatch.GetTimestamp();
                var keepGoing = egraph.RunRules(addComm, addAssoc).Changed;
                if (log != null)
                    log.WriteLine($"Finished iteration {i} after {RustDuration.Format(RustDuration.ElapsedNanoseconds(start))}");

                if (keepGoing == false)
                    return i + 1;
            }
        }

        /// <summary>
        /// Checks the result: the two sums have the same canonical id (<c>ac.rs</c>'s <c>assert_eq!</c>, inside its timed
        /// region), the loop ran <see cref="ExpectedIterations"/> iterations (one fewer than <c>ac.exe</c>, see the type's
        /// remarks), and the tables hold the <see cref="N"/> numbers and the saturated <c>add</c> rows. O(find) plus
        /// O(1).
        /// </summary>
        /// <param name="iterations">The iterations <see cref="Saturate"/> reported.</param>
        /// <exception cref="InvalidOperationException">The result is not the saturated e-graph.</exception>
        public void Verify(int iterations)
        {
            var canonLeft = egraph.GetCanonRepr(leftRoot, ColumnTy.Id);
            var canonRight = egraph.GetCanonRepr(rightRoot, ColumnTy.Id);
            var errors = new StringBuilder();
            if (canonLeft != canonRight)
                errors.Append($" the sums are in different classes ({canonLeft.Rep} and {canonRight.Rep});");

            if (iterations != ExpectedIterations)
                errors.Append($" saturated after {iterations} iterations, not {ExpectedIterations};");

            var nums = egraph.TableSize(numTable);
            if (nums != N)
                errors.Append($" num has {nums} rows, not {N};");

            var adds = egraph.TableSize(addTable);
            var saturatedAdds = AcSaturationWorkload.SaturatedRows(N);
            if (adds != saturatedAdds)
                errors.Append($" add has {adds} rows, the saturated e-graph {saturatedAdds};");

            if (errors.Length > 0)
                throw new InvalidOperationException("The ac program's result is wrong:" + errors.ToString().TrimEnd(';'));
        }

    }

}
