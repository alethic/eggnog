using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// egglog's <c>basic_query</c> (core-relations/src/tests.rs:40-138) on the <c>basic_math_egraph</c> fixture
    /// (<see cref="MathEgraph"/>), at benchmark scale: the rule <c>Add(x, y, z), Num(a, x), Num(b, y) =&gt;
    /// Num(a + b, z)</c> over a generated e-graph, run once through <see cref="Database.RunRuleSet"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The input has <see cref="Values"/> <c>num</c> rows, the values <c>0 .. Values-1</c> with fresh ids, and
    /// <see cref="Adds"/> <c>add</c> rows over distinct random pairs of those ids, each with a fresh id; every row is
    /// stamped 0. The rule's three-atom join matches once per <c>add</c> row, and its actions call an external function
    /// adding the two 64-bit base values and insert <c>num(a + b, z, 1)</c>. About half of the sums are values that
    /// already have a row, and every sum that several rows produce collides too, so the merge runs
    /// <see cref="UnionTakeNewMerge"/>, which stages unions into the union-find in the same <see cref="Database.MergeAll"/>.
    /// </para>
    /// <para>
    /// The reference model (<see cref="ExpectedNumRows"/>, <see cref="ExpectedUnions"/>) does not depend on the order the
    /// rows merge in: each sum keeps one row, and each colliding row adds one union between distinct classes, so the
    /// parallel mode must produce the same counts.
    /// </para>
    /// </remarks>
    internal sealed class MathWorkload
    {

        /// <summary>
        /// The name of the rule, for the run report.
        /// </summary>
        public const string RuleName = "add";

        readonly int[] leftValues;
        readonly int[] rightValues;

        /// <summary>
        /// Generates the input for about <paramref name="rows"/> rows in total, from <paramref name="seed"/>.
        /// </summary>
        /// <param name="rows">The total row count: half <c>num</c> rows, half <c>add</c> rows; at least four.</param>
        /// <param name="seed">The random seed.</param>
        public MathWorkload(int rows, int seed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(rows, 4);

            Values = rows / 2;
            Adds = rows - Values;
            leftValues = new int[Adds];
            rightValues = new int[Adds];
            var random = new Random(seed);
            var pairs = new HashSet<(int, int)>(Adds);
            for (var i = 0; i < Adds; i++)
            {
                int a, b;
                do
                {
                    a = random.Next(Values);
                    b = random.Next(Values);
                }
                while (pairs.Add((a, b)) == false);

                leftValues[i] = a;
                rightValues[i] = b;
            }

            // Reference model: each sum s keeps one row; the rows producing s, plus the existing row when s < Values,
            // merge one after another, each staging one union between distinct classes.
            var producers = new Dictionary<int, int>();
            for (var i = 0; i < Adds; i++)
            {
                var s = leftValues[i] + rightValues[i];
                producers[s] = producers.TryGetValue(s, out var k) ? k + 1 : 1;
            }

            var numRows = Values;
            var unions = 0;
            foreach (var (sum, count) in producers)
            {
                if (sum < Values)
                {
                    unions += count;
                }
                else
                {
                    numRows++;
                    unions += count - 1;
                }
            }

            ExpectedNumRows = numRows;
            ExpectedUnions = unions;
        }

        /// <summary>
        /// Gets the number of <c>num</c> rows loaded.
        /// </summary>
        public int Values { get; }

        /// <summary>
        /// Gets the number of <c>add</c> rows loaded, which is the number of matches of the rule.
        /// </summary>
        public int Adds { get; }

        /// <summary>
        /// Gets the number of live <c>num</c> rows after the run.
        /// </summary>
        public int ExpectedNumRows { get; }

        /// <summary>
        /// Gets the number of unions the run's merge leaves in the union-find.
        /// </summary>
        public int ExpectedUnions { get; }

        /// <summary>
        /// Loads the input into <paramref name="graph"/>, registers the external function adding two 64-bit base values,
        /// and builds the rule set: everything but the run, which a rule set must directly follow (its header subsets
        /// are evaluated against the tables as they are at build time).
        /// </summary>
        /// <param name="graph">An empty fixture.</param>
        /// <returns>The rule set; the caller disposes it.</returns>
        public RuleSet Prepare(MathEgraph graph)
        {
            ArgumentNullException.ThrowIfNull(graph);

            var db = graph.Db;
            var bases = new BaseValues();
            bases.RegisterType<Int64Codec, long>();
            var addInt = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                var x = bases.Unwrap<Int64Codec, long>(new Value(args[0]));
                var y = bases.Unwrap<Int64Codec, long>(new Value(args[1]));
                result = bases.Get<Int64Codec, long>(x + y).Rep;
                return true;
            }));

            var ids = new uint[Values];
            var numBuffer = db.NewBuffer(graph.Num);
            for (var i = 0; i < Values; i++)
            {
                ids[i] = (uint)db.IncrementCounter(graph.IdCounter);
                numBuffer.StageInsert([bases.Get<Int64Codec, long>(i).Rep, ids[i], 0]);
            }

            numBuffer.Flush();
            db.MergeAll();

            var addBuffer = db.NewBuffer(graph.Add);
            for (var i = 0; i < Adds; i++)
                addBuffer.StageInsert([ids[leftValues[i]], ids[rightValues[i]], (uint)db.IncrementCounter(graph.IdCounter), 0]);

            addBuffer.Flush();
            db.MergeAll();

            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            q.Strategy = PlanStrategy.Gj;
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            var t1 = q.NewVariable("t1");
            var t2 = q.NewVariable("t2");
            var t3 = q.NewVariable("t3");
            var a = q.NewVariable("a");
            var b = q.NewVariable("b");
            q.AddAtom(graph.Add, [x, y, z, t1]);
            q.AddAtom(graph.Num, [a, x, t2]);
            q.AddAtom(graph.Num, [b, y, t3]);
            var rb = q.Build();
            var sum = rb.CallExternal(addInt, [a, b]);
            rb.Insert(graph.Num, [sum, z, new Value(1)]);
            rb.Build(RuleName);
            return rsb.Build();
        }

        /// <summary>
        /// Checks the run against the reference model: one match per <c>add</c> row, a change, the expected live
        /// <c>num</c> rows and the expected unions.
        /// </summary>
        /// <param name="graph">The fixture after the run.</param>
        /// <param name="report">The run's report.</param>
        /// <exception cref="InvalidOperationException">The run disagrees with the reference model.</exception>
        public void Verify(MathEgraph graph, RuleSetReport report)
        {
            ArgumentNullException.ThrowIfNull(graph);
            ArgumentNullException.ThrowIfNull(report);

            var matches = report.NumMatches(RuleName);
            var numRows = graph.Db.GetTable(graph.Num).Count;
            var unions = graph.UnionFind.Count;
            if (report.Changed == false || matches != Adds || numRows != ExpectedNumRows || unions != ExpectedUnions)
                throw new InvalidOperationException(
                    $"The math run matched {matches} times (changed: {report.Changed}) and left {numRows} num rows and {unions} unions; "
                    + $"the reference model predicts {Adds} matches, {ExpectedNumRows} rows and {ExpectedUnions} unions.");
        }

    }

}
