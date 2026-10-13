using System;
using System.Collections.Generic;
using System.IO;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The benchmark's BasicMath rule run in parallel at the root at 200k rows: the total bytes the run allocates,
    /// written to a report file (<c>RC_OUT</c>, or <c>rootcost.txt</c> beside the tests) to compare builds of the
    /// engine.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class ParallelRootMathAllocationTests
    {

        [Fact]
        public void Run()
        {
            for (var round = 0; round < 3; round++)
            {
                var g = new ExecutionMathEgraph("Serial");
                var db = g.Db;
                db.ParallelRuleSetsOverride = true;
                var rows = 200_000;
                var values = rows / 2;
                var adds = rows - values;
                var random = new Random(0x5EED);
                var pairs = new HashSet<(int, int)>(adds);
                var left = new int[adds];
                var right = new int[adds];
                for (var i = 0; i < adds; i++)
                {
                    int a, b;
                    do
                    {
                        a = random.Next(values);
                        b = random.Next(values);
                    }
                    while (pairs.Add((a, b)) == false);

                    left[i] = a;
                    right[i] = b;
                }

                var bases = new BaseValues();
                bases.RegisterType<Int64Codec, long>();
                var addInt = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
                {
                    var x = bases.Unwrap<Int64Codec, long>(new Value(args[0]));
                    var y = bases.Unwrap<Int64Codec, long>(new Value(args[1]));
                    result = bases.Get<Int64Codec, long>(x + y).Rep;
                    return true;
                }));

                var ids = new uint[values];
                var numBuffer = db.NewBuffer(g.Num);
                for (var i = 0; i < values; i++)
                {
                    ids[i] = (uint)db.IncrementCounter(g.IdCounter);
                    numBuffer.StageInsert([bases.Get<Int64Codec, long>(i).Rep, ids[i], 0]);
                }

                numBuffer.Flush();
                db.MergeAll();
                var addBuffer = db.NewBuffer(g.Add);
                for (var i = 0; i < adds; i++)
                    addBuffer.StageInsert([ids[left[i]], ids[right[i]], (uint)db.IncrementCounter(g.IdCounter), 0]);

                addBuffer.Flush();
                db.MergeAll();
                var rsb = new RuleSetBuilder(db);
                var q = rsb.NewRule();
                q.Strategy = PlanStrategy.Gj;
                var xv = q.NewVariable("x");
                var yv = q.NewVariable("y");
                var z = q.NewVariable("z");
                var t1 = q.NewVariable("t1");
                var t2 = q.NewVariable("t2");
                var t3 = q.NewVariable("t3");
                var av = q.NewVariable("a");
                var bv = q.NewVariable("b");
                q.AddAtom(g.Add, [xv, yv, z, t1]);
                q.AddAtom(g.Num, [av, xv, t2]);
                q.AddAtom(g.Num, [bv, yv, t3]);
                var rb = q.Build();
                var sum = rb.CallExternal(addInt, [av, bv]);
                rb.Insert(g.Num, [sum, z, new Value(1)]);
                rb.Build("add");
                using var rs = rsb.Build();
                var before = GC.GetTotalAllocatedBytes(true);
                var report = db.ExecuteRuleSet(rs);
                var bytes = GC.GetTotalAllocatedBytes(true) - before;
                File.AppendAllText(Environment.GetEnvironmentVariable("RC_OUT") ?? Path.Combine(AppContext.BaseDirectory, "rootcost.txt"),
                    $"parallel math 200k: {bytes} B, matches {report.NumMatches("add")}{Environment.NewLine}");
            }
        }

    }

}
