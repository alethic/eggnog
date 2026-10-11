using System;
using System.Collections.Generic;
using System.Globalization;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The input, the rule and the reference model of <see cref="ActionLookupBenchmarks"/>: a rule whose join is one
    /// scan and whose actions are dominated by <c>LookupOrInsertDefault</c> and <c>Lookup</c>, writing a function whose
    /// merge calls <see cref="ExecutionState.PredictRow(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, Span{uint})"/>
    /// (<see cref="FunctionMerge"/>), over an <see cref="ActionLookupProgram"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For <see cref="Keys"/> = <c>N</c>: <c>leaf(k) = k</c>, <c>weight(k) = k</c> and <c>best(k) = k</c> for every
    /// <c>k</c> below <c>N</c> (leaf ids are minted in order, so leaf <c>k</c> is id <c>k</c>), and <c>N</c>
    /// <c>driver(a, b)</c> rows: the <c>a</c> are distinct, drawn from [0, 2N) (half of them below <c>N</c>), the
    /// <c>b</c> are uniform in [0, 2N) and repeat. The rule, as egglog's bridge would lower
    /// <c>(rule ((driver a b)) ((set (best (weight (leaf a))) (leaf b))))</c>:
    /// </para>
    /// <code>
    /// driver(a, b, _) =&gt; ts = ReadCounter(clock)
    ///                     x  = LookupOrInsertDefault(leaf, [a], [fresh id, ts])   // hit for a &lt; N, else a new leaf
    ///                     y  = LookupOrInsertDefault(leaf, [b], [fresh id, ts])   // hit for b &lt; N, else predicted (repeats hit the prediction)
    ///                     w  = Lookup(weight, [x])                                // hit for a &lt; N; the lane drops otherwise
    ///                     Insert(best, [w, y, ts])                                 // collides with best(a): the merge calls combine(old, new)
    /// </code>
    /// <para>
    /// The reference model does not depend on the order lanes, batches or merges run in: the <c>a</c> are distinct, so
    /// each <c>best</c> key receives at most one row, whose merge with <c>best(a) = a</c> creates
    /// <c>combine(a, y)</c> and stores its id unless <c>b == a</c>; <c>leaf</c> gains one row per distinct key of at
    /// least <c>N</c> (parallel states that predict the same key twice are unioned, and the table keeps the smaller
    /// id). <c>y</c> is <c>leaf(b)</c> or, for a new leaf, an id the union-find makes equal to it, so the model names
    /// <c>y</c> by its class.
    /// </para>
    /// </remarks>
    internal sealed class ActionLookupWorkload
    {

        /// <summary>
        /// The name of the rule, for the run report.
        /// </summary>
        public const string RuleName = "lookups";

        readonly uint[] lefts;
        readonly uint[] rights;
        readonly uint[] newLeafKeys;

        /// <summary>
        /// Generates the input for <paramref name="keys"/> keys and driver rows from <paramref name="seed"/>, and the
        /// reference model.
        /// </summary>
        /// <param name="keys">The number of keys <c>N</c>; at least one.</param>
        /// <param name="seed">The random seed.</param>
        public ActionLookupWorkload(int keys, int seed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(keys, 1);

            Keys = keys;
            var random = new Random(seed);
            var permutation = new uint[2 * keys];
            for (var i = 0; i < permutation.Length; i++)
                permutation[i] = (uint)i;

            random.Shuffle(permutation);
            lefts = permutation.AsSpan(0, keys).ToArray();
            rights = new uint[keys];
            for (var i = 0; i < keys; i++)
                rights[i] = (uint)random.Next(2 * keys);

            var newLeaves = new HashSet<uint>();
            var survivors = 0;
            var combines = 0;
            for (var i = 0; i < keys; i++)
            {
                if (lefts[i] >= keys)
                {
                    newLeaves.Add(lefts[i]);
                }
                else
                {
                    survivors++;
                    if (rights[i] != lefts[i])
                        combines++;
                }

                if (rights[i] >= keys)
                    newLeaves.Add(rights[i]);
            }

            ExpectedMatches = survivors;
            ExpectedLeaves = keys + newLeaves.Count;
            ExpectedCombines = combines;
            newLeafKeys = [.. newLeaves];
            Array.Sort(newLeafKeys);
        }

        /// <summary>
        /// Gets the number of keys <c>N</c>, which is also the number of <c>driver</c> rows: the join's matches, each
        /// of which runs the actions.
        /// </summary>
        public int Keys { get; }

        /// <summary>
        /// Gets the number of matches the run reports: the lanes that reach the end of the actions, which are those
        /// whose <c>Lookup</c> on <c>weight</c> hits (<c>a &lt; N</c>).
        /// </summary>
        public int ExpectedMatches { get; }

        /// <summary>
        /// Gets the number of live <c>leaf</c> rows after the run.
        /// </summary>
        public int ExpectedLeaves { get; }

        /// <summary>
        /// Gets the number of live <c>combine</c> rows after the run: one per <c>best</c> merge.
        /// </summary>
        public int ExpectedCombines { get; }

        /// <summary>
        /// Loads the input into <paramref name="program"/> (stamped with the clock, then the clock advances), and builds
        /// the rule set the run executes.
        /// </summary>
        /// <param name="program">An empty database.</param>
        /// <returns>The rule set; the caller disposes it.</returns>
        public RuleSet Prepare(ActionLookupProgram program)
        {
            ArgumentNullException.ThrowIfNull(program);

            var db = program.Db;
            var ts = program.Timestamp;
            var leaf = db.NewBuffer(program.Leaf);
            var weight = db.NewBuffer(program.Weight);
            var best = db.NewBuffer(program.Best);
            for (var k = 0u; k < Keys; k++)
            {
                var id = checked((uint)db.IncrementCounter(program.Ids));
                leaf.StageInsert([k, id, ts]);
                weight.StageInsert([id, k, ts]);
                best.StageInsert([k, id, ts]);
            }

            leaf.Flush();
            weight.Flush();
            best.Flush();
            var driver = db.NewBuffer(program.Driver);
            for (var i = 0; i < Keys; i++)
                driver.StageInsert([lefts[i], rights[i], ts]);

            driver.Flush();
            db.MergeAll(ts);
            db.IncrementCounter(program.Clock);

            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            q.Strategy = PlanStrategy.MinCover;
            var a = q.NewVariable("a");
            var b = q.NewVariable("b");
            var t = q.NewVariable("t");
            q.AddAtom(program.Driver, [a, b, t]);
            var rb = q.Build();
            var now = rb.ReadCounter(program.Clock);
            var x = rb.LookupOrInsert(program.Leaf, [a], [WriteVal.FromCounter(program.Ids), now], new ColumnId(1));
            var y = rb.LookupOrInsert(program.Leaf, [b], [WriteVal.FromCounter(program.Ids), now], new ColumnId(1));
            var w = rb.Lookup(program.Weight, [x], new ColumnId(1));
            rb.Insert(program.Best, [w, y, now]);
            rb.Build(RuleName);
            return rsb.Build();
        }

        /// <summary>
        /// Runs <paramref name="ruleSet"/> at the clock's timestamp, merge included.
        /// </summary>
        /// <param name="program">The database <see cref="Prepare"/> filled.</param>
        /// <param name="ruleSet">The rule set <see cref="Prepare"/> built.</param>
        /// <returns>The run's report.</returns>
        public static RuleSetReport Run(ActionLookupProgram program, RuleSet ruleSet)
        {
            ArgumentNullException.ThrowIfNull(program);

            return program.Db.RunRuleSet(ruleSet, ReportLevel.TimeOnly, program.Timestamp);
        }

        /// <summary>
        /// Checks the run against the reference model: <see cref="ExpectedMatches"/> matches, a change, no recorded
        /// failure, the expected live row count of every table, and the contents the merges must produce.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The contents checked, all independent of the order lanes, batches and merges run in:
        /// </para>
        /// <list type="bullet">
        /// <item>the loaded rows the run only reads are unchanged: <c>leaf(k) = k</c> and <c>weight(k) = k</c> for every
        /// <c>k &lt; N</c>;</item>
        /// <item>every new leaf key holds a fresh id (at least <c>N</c>) that the union-find keeps canonical (the least of
        /// the ids the run predicted for the key);</item>
        /// <item>no two <c>combine</c> rows share their first key, and every output is a fresh id of its own;</item>
        /// <item>for each <c>driver(a, b)</c> with <c>a &lt; N</c> and <c>b != a</c>: a <c>combine</c> row has the first
        /// key <c>a</c> (the id of leaf <c>a</c>), its second key is in the class of <c>leaf(b)</c>
        /// (<see cref="DisplacedTable.FindNaive"/>), and <c>best(a)</c> holds its output: what
        /// <see cref="FunctionMerge"/> predicted, stored by the table's merge;</item>
        /// <item>every other <c>best(k)</c>, <c>k &lt; N</c>, still holds <c>k</c>, and <c>best</c> has no other key.</item>
        /// </list>
        /// <para>
        /// O(<c>N</c>) lookups plus one scan of <c>combine</c>, outside the measurement.
        /// </para>
        /// </remarks>
        /// <param name="program">The database after the run.</param>
        /// <param name="report">The run's report.</param>
        /// <exception cref="InvalidOperationException">The run disagrees with the reference model.</exception>
        public void Verify(ActionLookupProgram program, RuleSetReport report)
        {
            ArgumentNullException.ThrowIfNull(program);
            ArgumentNullException.ThrowIfNull(report);

            var db = program.Db;
            var matches = report.NumMatches(RuleName);
            var leaf = (SortedWritesTable)db.GetTable(program.Leaf);
            var combine = (SortedWritesTable)db.GetTable(program.Combine);
            var best = (SortedWritesTable)db.GetTable(program.Best);
            var weight = (SortedWritesTable)db.GetTable(program.Weight);
            var uf = (DisplacedTable)db.GetTable(program.Uf);
            if (db.Failure is { } failure)
                throw new InvalidOperationException("The run reported a failure: " + failure);

            var agrees = report.Changed
                && matches == ExpectedMatches
                && leaf.Count == ExpectedLeaves
                && combine.Count == ExpectedCombines
                && best.Count == Keys
                && weight.Count == Keys;
            if (agrees == false)
                throw new InvalidOperationException(
                    $"The run matched {matches} times (changed: {report.Changed}) and left {leaf.Count} leaf, {combine.Count} combine, "
                    + $"{best.Count} best and {weight.Count} weight rows; the reference model predicts {ExpectedMatches} matches, "
                    + $"{ExpectedLeaves} leaf, {ExpectedCombines} combine, {Keys} best and {Keys} weight rows.");

            var n = (uint)Keys;
            for (var k = 0u; k < n; k++)
            {
                if (leaf.TryGetColumn(k, 1, out var id) == false || id != k)
                    throw Mismatch($"leaf({k}) is {Describe(leaf, k)}; it was loaded as {k} and only read.");

                if (weight.TryGetColumn(k, 1, out var w) == false || w != k)
                    throw Mismatch($"weight({k}) is {Describe(weight, k)}; it was loaded as {k} and only read.");
            }

            foreach (var key in newLeafKeys)
            {
                if (leaf.TryGetColumn(key, 1, out var id) == false || id < n || uf.FindNaive(id) != id)
                    throw Mismatch($"the new leaf({key}) is {Describe(leaf, key)}; it must hold a fresh, canonical id.");
            }

            // The combine rows by first key: each driver row with a below N merges into best(a) once, so the first keys
            // are distinct, and the outputs are ids the merges minted.
            var combines = new Dictionary<uint, (uint Second, uint Output)>(ExpectedCombines);
            var outputs = new HashSet<uint>(ExpectedCombines);
            foreach (var row in RowCollector.LiveRows(combine))
            {
                if (combines.TryAdd(row[0], (row[1], row[2])) == false)
                    throw Mismatch($"two combine rows have the first key {row[0]}.");

                if (row[2] < n || outputs.Add(row[2]) == false)
                    throw Mismatch($"combine({row[0]}, {row[1]}) holds {row[2]}, which is not a fresh id of its own.");
            }

            var expectedBest = new uint[Keys];
            for (var k = 0u; k < n; k++)
                expectedBest[k] = k;

            for (var i = 0; i < Keys; i++)
            {
                var a = lefts[i];
                var b = rights[i];
                if (a >= n || b == a)
                    continue;

                if (combines.TryGetValue(a, out var merged) == false)
                    throw Mismatch($"driver({a}, {b}): no combine row has the first key {a}, which the merge into best({a}) creates.");

                // leaf(b) was checked above: b itself below N, a fresh canonical id otherwise.
                leaf.TryGetColumn(b, 1, out var leafB);
                if (uf.FindNaive(merged.Second) != uf.FindNaive(leafB))
                    throw Mismatch($"driver({a}, {b}): combine({a}, {merged.Second}) has a second key outside the class of leaf({b}) = {leafB}.");

                expectedBest[a] = merged.Output;
            }

            for (var k = 0u; k < n; k++)
            {
                if (best.TryGetColumn(k, 1, out var value) == false || value != expectedBest[k])
                    throw Mismatch(
                        $"best({k}) is {Describe(best, k)}; the reference model predicts {expectedBest[k]}"
                        + (expectedBest[k] == k ? ", which no merge changes." : ", the output of the combine row its merge created."));
            }
        }

        /// <summary>
        /// Describes the value column of <paramref name="table"/> at <paramref name="key"/> for a failure message.
        /// </summary>
        /// <param name="table">A table with one key column and its value in column 1.</param>
        /// <param name="key">The key.</param>
        /// <returns>The value, or "absent".</returns>
        static string Describe(SortedWritesTable table, uint key)
        {
            return table.TryGetColumn(key, 1, out var value) ? value.ToString(CultureInfo.InvariantCulture) : "absent";
        }

        /// <summary>
        /// Creates the exception that fails a run whose contents disagree with the reference model.
        /// </summary>
        /// <param name="message">What disagrees.</param>
        /// <returns>The exception.</returns>
        static InvalidOperationException Mismatch(string message) => new("The run disagrees with the reference model: " + message);

    }

}
