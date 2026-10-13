using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Rules in concurrent children (colored-contexts spec §10.9, §17 M10.3): sixteen children of a frozen root run on
    /// eight threads through <see cref="ConcurrentChildren"/>, each staging host writes (rows, terms and unions over root
    /// and child-local ids) and saturating a rule set that derives relations, mints constructor terms, unions classes and
    /// raises a lattice. Each child must end equal, modulo the renaming of the ids it minted, to a serial replay of the
    /// same program in a fresh child of the same root; its <see cref="EGraphContext.CheckInvariants"/> must pass; and the
    /// frozen root must be unchanged bit for bit: every row of every table in physical order with its timestamp, the
    /// union-find's rows and parent array, and the root's version.
    /// </summary>
    /// <remarks>
    /// Renaming. A child's ids come from its id blocks, so the same program mints different ids in different children.
    /// Root ids are shared and kept; every other class (each holds a constructor term, since the programs mint ids only
    /// through constructors) is named by its smallest term, by length then ordinal, over the child's constructor rows. A
    /// congruence-closed view gives distinct classes distinct smallest terms, so the names are a renaming.
    /// </remarks>
    public class ConcurrentChildRulesTests
    {

        const int Children = 16;
        const int Threads = 8;
        const int RootIds = 10;
        const int MaxIterations = 64;

        /// <summary>
        /// Serial children (the default under a frozen parent, §10.9) lease one thread each and run eight at a time;
        /// children given two threads through <see cref="ContextOptions.Parallelism"/> lease two and run four at a time.
        /// </summary>
        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 1)]
        [InlineData(3, 1)]
        [InlineData(4, 1)]
        [InlineData(5, 2)]
        [InlineData(6, 2)]
        public void SixteenChildrenOnEightThreadsEqualSerialReplays(int seed, int childThreads)
        {
            var options = childThreads == 1 ? null : new ContextOptions { Parallelism = new ParallelHeuristics(childThreads) };
            var g = new ContextGraph(new ParallelHeuristics(Threads));
            var rules = Rules(g);
            var random = new Random(seed);
            var ids = Enumerable.Range(0, RootIds).Select(_ => g.Root.FreshId()).ToArray();
            for (var i = 0; i < RootIds; i++)
                g.Assert(g.Root, g.Edge, ids[i], ids[random.Next(RootIds)]);

            g.Root.StageRow(g.Low, [ids[0], g.Int(random.Next(100))]);
            g.Root.AddTerm(g.Add, [ids[1], ids[2]]);
            g.Root.FlushUpdates();

            // One root iteration, so that the children inherit watermarks (§10.2) and still have work of their own.
            g.Root.RunRules(rules);
            g.Root.Freeze();
            Assert.All(rules, r => Assert.True(g.EGraph.GetRuleInfo(r).AllowInheritance));
            var rootIdBound = g.EGraph.Database.ReadCounter(g.EGraph.IdCounter);
            var before = RootSnapshot(g);

            var programs = Enumerable.Range(0, Children).Select(i => Program(g, ids, new Random(seed * 1000 + i))).ToArray();
            var children = Enumerable.Range(0, Children).Select(_ => g.Root.CreateChild(options)).ToArray();
            Assert.All(children, c => Assert.Equal(childThreads, c.Parallelism.DegreeOfParallelism));
            var transcripts = new string[Children];
            var arrived = 0;
            var degree = Threads / childThreads;
            ConcurrentChildren.Run(children, (child, i) =>
            {
                // Best effort to overlap the first bodies of every worker (the thread pool may supply fewer in time).
                Interlocked.Increment(ref arrived);
                SpinWait.SpinUntil(() => Volatile.Read(ref arrived) >= degree, TimeSpan.FromSeconds(1));
                var log = Replay(child, programs[i], rules);
                child.CheckInvariants();
                transcripts[i] = log + Normalize(g, child, (uint)rootIdBound);
            });

            Assert.Equal(before, RootSnapshot(g));
            for (var i = 0; i < Children; i++)
            {
                using var replay = g.Root.CreateChild(options);
                var log = Replay(replay, programs[i], rules);
                replay.CheckInvariants();
                Assert.Equal(log + Normalize(g, replay, (uint)rootIdBound), transcripts[i]);
                Assert.Equal(before, RootSnapshot(g));
            }

            foreach (var child in children)
            {
                child.CheckInvariants();
                child.Dispose();
            }

            Assert.Equal(before, RootSnapshot(g));
            Assert.Equal(Threads, g.EGraph.ParallelismBudget.Available);
        }

        /// <summary>
        /// The rule set, every rule query-determined (constructors, relations, a lattice; no table reads in actions):
        /// <c>edge → path</c>, transitivity, <c>path(x, y) → add(x, y)</c>, commutativity of <c>add</c>, two-cycles collapse,
        /// <c>low</c> flows along edges, and <c>low(x) = v → mark(num(v))</c>.
        /// </summary>
        static EGraphRuleId[] Rules(ContextGraph g)
        {
            var list = new List<EGraphRuleId> { g.EdgeToPath(), g.Transitive() };

            var rb = g.EGraph.NewRule("path-add", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Path, [x, y, rb.NewVar(g.UnitType)], false);
            rb.Lookup(g.Add, [x, y], static () => "add");
            list.Add(rb.Build());

            rb = g.EGraph.NewRule("add-comm", true);
            x = rb.NewVar(ColumnTy.Id);
            y = rb.NewVar(ColumnTy.Id);
            var t = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Add, [x, y, t], false);
            rb.Set(g.Add, [y, x, t]);
            list.Add(rb.Build());

            rb = g.EGraph.NewRule("two-cycle", true);
            x = rb.NewVar(ColumnTy.Id);
            y = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Edge, [x, y, rb.NewVar(g.UnitType)], false);
            rb.QueryTable(g.Edge, [y, x, rb.NewVar(g.UnitType)], false);
            rb.Union(x, y);
            list.Add(rb.Build());

            rb = g.EGraph.NewRule("low-flow", true);
            x = rb.NewVar(ColumnTy.Id);
            y = rb.NewVar(ColumnTy.Id);
            var v = rb.NewVar(g.I64);
            rb.QueryTable(g.Edge, [x, y, rb.NewVar(g.UnitType)], false);
            rb.QueryTable(g.Low, [x, v], false);
            rb.Set(g.Low, [y, v]);
            list.Add(rb.Build());

            rb = g.EGraph.NewRule("low-mark", true);
            x = rb.NewVar(ColumnTy.Id);
            v = rb.NewVar(g.I64);
            rb.QueryTable(g.Low, [x, v], false);
            var n = rb.Lookup(g.Num, [v], static () => "num");
            rb.Set(g.Mark, [n, RuleEntry.Const(g.Unit, g.UnitType)]);
            list.Add(rb.Build());

            return list.ToArray();
        }

        /// <summary>
        /// A random child program in two rounds: each stages edges over root ids, a union of root ids, a term over root
        /// ids with an edge from it, a union of a minted term with a root id (second round), and a <c>low</c> value; then
        /// flushes and saturates. Each step receives the ids the program minted so far, so a replay mints its own.
        /// </summary>
        static List<Action<EGraphContext, List<Value>>> Program(ContextGraph g, Value[] ids, Random random)
        {
            var steps = new List<Action<EGraphContext, List<Value>>>();
            for (var round = 0; round < 2; round++)
            {
                for (var e = 0; e < 3; e++)
                {
                    var (a, b) = (ids[random.Next(RootIds)], ids[random.Next(RootIds)]);
                    steps.Add((c, _) => g.Assert(c, g.Edge, a, b));
                }

                var (u, w) = (ids[random.Next(RootIds)], ids[random.Next(RootIds)]);
                if (random.Next(3) > 0)
                    steps.Add((c, _) => c.StageUnion(u, w));

                var (k0, k1, target) = (ids[random.Next(RootIds)], ids[random.Next(RootIds)], ids[random.Next(RootIds)]);
                steps.Add((c, minted) =>
                {
                    var term = c.StageTerm(g.Add, [k0, k1]);
                    minted.Add(term);
                    g.Assert(c, g.Edge, term, target);
                });

                if (round == 1)
                {
                    var (which, other) = (random.Next(2), ids[random.Next(RootIds)]);
                    steps.Add((c, minted) => c.StageUnion(minted[which], other));
                }

                var (low, value) = (ids[random.Next(RootIds)], random.Next(100));
                steps.Add((c, _) => c.StageRow(g.Low, [low, g.Int(value)]));
                steps.Add(null!);
            }

            return steps;
        }

        /// <summary>
        /// Runs a program in <paramref name="child"/>: a <c>null</c> step flushes and saturates. Returns a log of each
        /// saturation's iteration count, which a serial replay must reproduce (decision 4: serial runs are deterministic).
        /// </summary>
        static string Replay(EGraphContext child, List<Action<EGraphContext, List<Value>>> program, EGraphRuleId[] rules)
        {
            var minted = new List<Value>();
            var log = new StringBuilder();
            foreach (var step in program)
            {
                if (step is null)
                {
                    child.FlushUpdates();
                    var report = child.Saturate(rules, MaxIterations);
                    Assert.False(report.HitLimit);
                    log.Append("saturated in ").Append(report.Iterations).Append('\n');
                }
                else
                {
                    step(child, minted);
                }
            }

            return log.ToString();
        }

        /// <summary>
        /// Writes every function's rows as <paramref name="child"/> sees them, sorted, with root ids kept and every other
        /// class named by its smallest constructor term.
        /// </summary>
        static string Normalize(ContextGraph g, EGraphContext child, uint rootIdBound)
        {
            var names = new Dictionary<uint, string>();
            string? Name(uint id) => id < rootIdBound ? "r" + id : names.GetValueOrDefault(id);
            static bool Better(string candidate, string? current) => current is null || candidate.Length < current.Length || (candidate.Length == current.Length && string.CompareOrdinal(candidate, current) < 0);

            var nums = Rows(child, g.Num);
            var adds = Rows(child, g.Add);
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var row in nums)
                    if (row[1] >= rootIdBound && "num(" + row[0] + ")" is var candidate && Better(candidate, names.GetValueOrDefault(row[1])))
                    {
                        names[row[1]] = candidate;
                        changed = true;
                    }

                foreach (var row in adds)
                    if (row[2] >= rootIdBound && Name(row[0]) is { } a && Name(row[1]) is { } b && "add(" + a + "," + b + ")" is var candidate && Better(candidate, names.GetValueOrDefault(row[2])))
                    {
                        names[row[2]] = candidate;
                        changed = true;
                    }
            }

            Assert.Equal(names.Count, names.Values.Distinct().Count());
            var text = new StringBuilder();
            void Write(string label, FunctionId function, bool[] idColumns)
            {
                var lines = Rows(child, function).Select(row => string.Join(" ", row.Select((cell, i) => idColumns[i] ? Name(cell) ?? throw new InvalidOperationException($"Id {cell} of {label} has no name.") : cell.ToString()))).ToList();
                lines.Sort(StringComparer.Ordinal);
                Assert.Equal(lines.Count, child.TableSize(function));
                text.Append(label).Append(": ").Append(string.Join("; ", lines)).Append('\n');
            }

            Write("edge", g.Edge, [true, true, false]);
            Write("path", g.Path, [true, true, false]);
            Write("mark", g.Mark, [true, false]);
            Write("num", g.Num, [false, true]);
            Write("add", g.Add, [true, true, true]);
            Write("low", g.Low, [true, false]);
            return text.ToString();
        }

        static List<uint[]> Rows(EGraphContext context, FunctionId function)
        {
            var rows = new List<uint[]>();
            context.ForEach(function, row => rows.Add(row.Values.ToArray().Select(v => v.Rep).ToArray()));
            return rows;
        }

        /// <summary>
        /// Every physical row of every function table of the root (timestamps and subsumption cells included) in table
        /// order, the union-find's rows with their timestamps, its parent array and the root's version.
        /// </summary>
        static string RootSnapshot(ContextGraph g)
        {
            var egraph = g.EGraph;
            var db = egraph.Database;
            var text = new StringBuilder();
            for (var f = 0; f < egraph.FunctionCount; f++)
            {
                var table = db.GetTable(egraph.GetTable(new FunctionId(f)));
                text.Append(f).Append(": ").Append(string.Join(" ", TableTestUtil.LiveRows(table).Select(TableTestUtil.Show))).Append('\n');
            }

            var displaced = egraph.Store.RootUnionFind.UnionFind;
            text.Append("uf: ").Append(string.Join(" ", Enumerable.Range(0, displaced.Count).Select(r => displaced.GetChild(r) + "->" + displaced.GetCanonical(r) + "@" + displaced.GetTimestamp(r)))).Append('\n');
            text.Append("parents: ").Append(string.Join(" ", displaced.UnionFind.Parents.ToArray())).Append('\n');
            text.Append("version: ").Append(g.Root.Version);
            return text.ToString();
        }

    }

}
