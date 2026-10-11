using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.ActionTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Differential tests of the vectorized interpreter against <see cref="ReferenceActionInterpreter"/>: random programs
    /// of every instruction kind, over random batches of up to 128 matches (and batches whose join binds nothing), run
    /// through one execution state batch after batch, must agree on the surviving lane count, every column they define,
    /// the order and arguments of external calls, the counters, the changed flag, the predictions and the tables after
    /// merging.
    /// </summary>
    public class ActionPropertyTests
    {

        static ActionPropertyWorld Build(Random rng)
        {
            var w = new ActionPropertyWorld();
            var db = new Database(ParallelHeuristics.Serial);
            w.Db = db;
            w.T0 = db.AddTable(Plain(1, 2), name: "t0");
            w.T1 = db.AddTable(Plain(2, 3), name: "t1");
            w.T2 = db.AddTable(Plain(4, 5), name: "t2");
            w.T3 = db.AddTable(Plain(1, 3), name: "t3");
            w.T4 = db.AddTable(Plain(0, 1), name: "t4");
            w.U = db.AddTable(new DisplacedTable(), name: "uf");
            w.Ids = db.AddCounter();
            w.Aux = db.AddCounter();
            for (var i = 0; i < 100; i++)
                db.IncrementCounter(w.Ids);

            var log = w.Log;
            var aux = w.Aux;
            w.F0 = db.AddExternalFunction(Function((_, a) => (a[0] * 7 + a[1]) % 4 == 0 ? null : (a[0] + a[1]) % 11, log, "f0"));
            w.F1 = db.AddExternalFunction(Function((_, a) => a[0] % 5 == 0 ? null : a[0] * 3 % 13, log, "f1"));
            w.F2 = db.AddExternalFunction(Function((s, a) => (uint)s.IncrementCounter(aux), log, "f2"));
            w.F3 = db.AddExternalFunction(Function((_, a) => a.Aggregate(0u, (x, y) => x + y) % 6 == 0 ? null : a.Aggregate(0u, (x, y) => x + y) % 17, log, "f3"));

            for (var i = 0; i < 6; i++)
                Fill(db, w.T0, [(uint)rng.Next(8), (uint)rng.Next(8)]);
            for (var i = 0; i < 12; i++)
                Fill(db, w.T1, [(uint)rng.Next(8), (uint)rng.Next(8), (uint)rng.Next(8)]);
            for (var i = 0; i < 30; i++)
                Fill(db, w.T2, [(uint)rng.Next(3), (uint)rng.Next(3), (uint)rng.Next(3), (uint)rng.Next(3), (uint)rng.Next(8)]);
            for (var i = 0; i < 4; i++)
                Fill(db, w.T3, [(uint)rng.Next(8), (uint)rng.Next(8), (uint)rng.Next(8)]);
            if (rng.Next(2) == 0)
                Fill(db, w.T4, [(uint)rng.Next(8)]);

            var buffer = db.NewBuffer(w.U);
            for (uint ts = 0; ts < 4; ts++)
                buffer.StageInsert([(uint)rng.Next(8), (uint)rng.Next(8), ts]);
            buffer.Flush();
            db.MergeAll();
            return w;
        }

        static Instr RandomInstr(Random rng, ActionPropertyWorld w, List<Variable> defined, ref int next)
        {
            QueryEntry E() => defined.Count > 0 && rng.Next(10) < 7 ? defined[rng.Next(defined.Count)] : K((uint)rng.Next(8));
            QueryEntry[] Es(int n) => Enumerable.Range(0, n).Select(_ => E()).ToArray();
            var dst = new Variable(next);
            Instr instr;
            switch (rng.Next(24))
            {
                case 0:
                    instr = new LookupInstr(w.T0, Es(1), C(rng.Next(2)), dst);
                    break;
                case 1:
                    instr = new LookupInstr(w.T1, Es(2), C(rng.Next(3)), dst);
                    break;
                case 2:
                    instr = new LookupInstr(w.T2, Es(4), C(rng.Next(5)), dst);
                    break;
                case 3:
                    instr = new LookupInstr(w.U, Es(1), C(rng.Next(3)), dst);
                    break;
                case 4:
                {
                    var (table, keys) = rng.Next(4) switch { 0 => (w.T0, 1), 1 => (w.T1, 2), 2 => (w.T2, 4), _ => (w.U, 1) };
                    instr = new LookupWithDefaultInstr(table, Es(keys), C(keys), dst, E());
                    break;
                }
                case 5:
                    instr = new LookupOrInsertDefaultInstr(w.T0, Es(1), [w.Ids], C(1), dst);
                    break;
                case 6:
                {
                    var defaults = new WriteVal[2];
                    for (var d = 0; d < 2; d++)
                        defaults[d] = rng.Next(3) switch { 0 => w.Ids, 1 => E(), _ => WriteVal.CurrentValue(rng.Next(1 + d)) };

                    instr = new LookupOrInsertDefaultInstr(w.T3, Es(1), defaults, C(rng.Next(3)), dst);
                    break;
                }
                case 7:
                    instr = new LookupOrInsertDefaultInstr(w.T2, Es(4), [w.Ids], C(rng.Next(5)), dst);
                    break;
                case 8:
                    instr = new LookupWithFallbackInstr(w.T0, Es(1), w.F1, Es(1), C(1), dst);
                    break;
                case 9:
                    instr = rng.Next(3) switch
                    {
                        0 => new InsertInstr(w.T0, Es(2)),
                        1 => new InsertInstr(w.T1, Es(3)),
                        _ => new InsertInstr(w.T2, Es(5)),
                    };
                    break;
                case 10:
                    instr = new InsertIfEqInstr(w.T0, E(), E(), Es(2));
                    break;
                case 11:
                    instr = rng.Next(3) switch
                    {
                        0 => new RemoveInstr(w.T0, Es(1)),
                        1 => new RemoveInstr(w.T1, Es(2)),
                        _ => new RemoveInstr(w.T2, Es(4)),
                    };
                    break;
                case 12:
                    instr = new ExternalInstr(w.F0, Es(2), dst);
                    break;
                case 13:
                    instr = new ExternalInstr(w.F1, Es(1), dst);
                    break;
                case 14:
                    instr = new ExternalInstr(w.F2, [], dst);
                    break;
                case 15:
                    instr = new ExternalInstr(w.F3, Es(5), dst);
                    break;
                case 16:
                    instr = new ExternalWithFallbackInstr(w.F0, Es(2), w.F1, Es(1), dst);
                    break;
                case 17:
                    instr = new AssertEqInstr(E(), E());
                    break;
                case 18:
                    instr = new AssertNeInstr(E(), E());
                    break;
                case 19:
                {
                    var half = rng.Next(4);
                    instr = AssertAnyNeInstr.Create(Es(half), Es(half));
                    break;
                }
                case 20:
                    instr = new ReadCounterInstr(rng.Next(2) == 0 ? w.Ids : w.Aux, dst);
                    break;
                case 21:
                    instr = rng.Next(2) == 0 ? new InsertInstr(w.T4, Es(1)) : new LookupInstr(w.T4, [], C(0), dst);
                    break;
                case 22:
                    instr = new ExternalWithFallbackInstr(w.F3, Es(5), w.F2, [], dst);
                    break;
                default:
                    instr = new LookupWithFallbackInstr(w.T1, Es(2), w.F0, Es(2), C(2), dst);
                    break;
            }

            if (DefinesDestination(instr))
            {
                defined.Add(dst);
                next++;
            }

            return instr;
        }

        static bool DefinesDestination(Instr instr) => instr.Kind switch
        {
            InstrKind.Insert or InstrKind.InsertIfEq or InstrKind.Remove or InstrKind.AssertEq or InstrKind.AssertNe or InstrKind.AssertAnyNe => false,
            _ => true,
        };

        static Variable? Destination(Instr instr) => instr switch
        {
            LookupInstr i => i.Destination,
            LookupWithDefaultInstr i => i.Destination,
            LookupOrInsertDefaultInstr i => i.Destination,
            LookupWithFallbackInstr i => i.Destination,
            ExternalInstr i => i.Destination,
            ExternalWithFallbackInstr i => i.Destination,
            ReadCounterInstr i => i.Destination,
            _ => null,
        };

        [Theory]
        [MemberData(nameof(Seeds))]
        public void MatchesTheReferenceInterpreter(int seed)
        {
            var rng = new Random(seed);
            var engine = Build(rng);
            var model = engine.Db.Clone();
            var reference = new ReferenceActionInterpreter(model);
            var state = engine.Db.CreateExecutionState();
            var bindings = new Bindings();

            var joinVars = rng.Next(7) == 0 ? 0 : 1 + rng.Next(4);
            var defined = Enumerable.Range(0, joinVars).Select(i => new Variable(i)).ToList();
            var next = joinVars;
            var program = Enumerable.Range(0, 1 + rng.Next(10)).Select(_ => RandomInstr(rng, engine, defined, ref next)).ToArray();
            var used = Enumerable.Range(0, joinVars).Select(i => new Variable(i)).ToArray();

            var batches = 1 + rng.Next(3);
            for (var batch = 0; batch < batches; batch++)
            {
                var matches = rng.Next(4) switch { 0 => 128, 1 => 1 + rng.Next(64), _ => 1 + rng.Next(128) };
                var columns = new Dictionary<Variable, uint[]>();
                foreach (var v in used)
                    columns[v] = Enumerable.Range(0, matches).Select(_ => (uint)rng.Next(8)).ToArray();

                bindings.Clear();
                var map = new DenseIdMap<Variable, Value>();
                for (var lane = 0; lane < matches; lane++)
                {
                    foreach (var v in used)
                        map.Insert(v, new Value(columns[v][lane]));

                    bindings.Push(map, used);
                }

                engine.Log.Clear();
                var got = state.RunInstrs(program, bindings);
                var engineLog = engine.Log.ToList();
                engine.Log.Clear();
                var expected = reference.Run(program, matches, columns);
                var message = $"seed {seed}, batch {batch}, program:\n  {string.Join("\n  ", program.Select(i => i.ToString()))}";

                Assert.True(expected == got, $"{message}\nlive lanes: expected {expected}, got {got}");
                Assert.True(engineLog.Count == engine.Log.Count, $"{message}\ncall count {engineLog.Count} vs {engine.Log.Count}");
                for (var c = 0; c < engineLog.Count; c++)
                {
                    Assert.Equal(engine.Log[c].Name, engineLog[c].Name);
                    Assert.Equal(engine.Log[c].Args, engineLog[c].Args);
                }

                foreach (var instr in program)
                {
                    if (Destination(instr) is not { } v || columns.ContainsKey(v) == false)
                        continue;

                    Assert.True(bindings.Contains(v), $"{message}\n{v} not bound");
                    Assert.True(columns[v].SequenceEqual(bindings[v].ToArray()),
                        $"{message}\n{v}: expected [{string.Join(", ", columns[v])}] got [{string.Join(", ", bindings[v].ToArray())}]");
                }

                Assert.True(reference.Changed == state.Changed, $"{message}\nchanged: expected {reference.Changed}");
                Assert.Equal(reference.PredictedCount, state.PredictedCount);
                Assert.Equal(model.ReadCounter(engine.Ids), engine.Db.ReadCounter(engine.Ids));
                Assert.Equal(model.ReadCounter(engine.Aux), engine.Db.ReadCounter(engine.Aux));
            }

            state.Flush();
            engine.Db.MergeAll();
            reference.Commit();
            foreach (var table in new[] { engine.T0, engine.T1, engine.T2, engine.T3, engine.T4 })
                Assert.Equal(DatabaseTestUtil.Dump(model, table), DatabaseTestUtil.Dump(engine.Db, table));
        }

        public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 400).Select(i => new object[] { i });

    }

}
