using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Rules in concurrent children: sixteen children of a frozen root run on eight threads through
    /// <see cref="ConcurrentChildren"/>, each staging host writes (rows, terms and unions over root and child-local
    /// ids) and saturating a rule set that derives relations, mints constructor terms, unions classes and raises a
    /// lattice. Each child must end equal, modulo the renaming of the ids it minted, to a serial replay of the same
    /// program in a fresh child of the same root; its <see cref="EGraphContext.CheckInvariants"/> must pass; and the
    /// frozen root must be unchanged bit for bit: every row of every table in physical order with its timestamp, the
    /// union-find's rows and parent array, and the root's version.
    /// </summary>
    /// <remarks>
    /// Renaming. A child's ids come from its id blocks, so the same program mints different ids in different children.
    /// Root ids are shared and kept; every other class (each holds a constructor term, since the programs mint ids only
    /// through constructors) is named by its smallest term, by length then ordinal, over the child's constructor rows.
    /// A congruence-closed view gives distinct classes distinct smallest terms, so the names are a renaming.
    /// </remarks>
    public class ConcurrentChildRulesTests
    {

        const int Children = 16;
        const int Threads = 8;
        const int RootIds = 10;
        const int MaxIterations = 64;

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public ConcurrentChildRulesTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Serial children (the default under a frozen parent) lease one thread each and run eight at a time; children
        /// given two threads through <see cref="ContextOptions.Parallelism"/> lease two and run four at a time.
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
            Run(seed, Children, Threads, childThreads, forceParallel: false, rounds: 2, freezeRaces: 0);
        }

        /// <summary>
        /// The soak (<see cref="ConcurrentSoak"/>, <c>EGGNOG_CONCURRENT_SOAK</c>): each seed draws 2 to 32 children, a
        /// budget of 2 to 8 threads, programs of 1 to 4 rounds, and in half the iterations children of two or four
        /// threads. In half, the children's rule sets are forced onto the parallel executor; when the children also have
        /// two or four threads, about a quarter of the iterations, the tasks of one child's run create its layers
        /// concurrently, while at one thread the executor runs every task on the calling thread. In half, a relation is
        /// defined on another thread while the root freezes, and the children write it, after the same race has run on
        /// up to 400 fresh graphs.
        /// </summary>
        [Fact]
        public void Soak()
        {
            ConcurrentSoak.Run(output, nameof(ConcurrentChildRulesTests), seed =>
            {
                var random = new Random(seed);
                var children = random.Next(2, 33);
                var threads = random.Next(2, 9);
                var childThreads = random.Next(4) switch { 2 => 2, 3 => 4, _ => 1 };
                var forceParallel = random.Next(2) == 0;
                var rounds = random.Next(1, 5);
                var freezeRaces = random.Next(2) == 0 ? 0 : random.Next(1, 401);
                var draws = $"{children} children, {threads} threads, {childThreads} per child, forced parallel {forceParallel}, {rounds} rounds, {freezeRaces} freeze races";
                return (draws, () => Run(seed, children, threads, childThreads, forceParallel, rounds, freezeRaces));
            });
        }

        /// <summary>
        /// Runs <paramref name="children"/> children of a frozen root concurrently within a budget of
        /// <paramref name="threads"/> threads, and compares each with a serial replay and the root with itself.
        /// </summary>
        /// <param name="seed">The seed of the root's facts and of every child's program.</param>
        /// <param name="children">The number of children.</param>
        /// <param name="threads">The e-graph's degree of parallelism: the budget the children share.</param>
        /// <param name="childThreads">Each child's degree of parallelism.</param>
        /// <param name="forceParallel">Whether every child's rule sets, and every replay's, run on the parallel executor
        /// whatever their size.</param>
        /// <param name="rounds">The number of rounds of each child's program.</param>
        /// <param name="freezeRaces">When positive, the root freezes while a relation is defined on another thread
        /// (<see cref="FreezeWhileDefining"/>), and the children write that relation; the same race runs first on this
        /// many less one fresh graphs without rules or rows, where it is cheap. Zero freezes the root alone.</param>
        static void Run(int seed, int children, int threads, int childThreads, bool forceParallel, int rounds, int freezeRaces)
        {
            for (var race = 1; race < freezeRaces; race++)
                FreezeWhileDefining(new ContextGraph());

            var options = childThreads == 1 ? null : new ContextOptions { Parallelism = new ParallelHeuristics(childThreads) };
            var g = new ContextGraph(new ParallelHeuristics(threads));
            var (rules, seen) = Rules(g);
            var random = new Random(seed);
            var ids = Enumerable.Range(0, RootIds).Select(_ => g.Root.FreshId()).ToArray();
            for (var i = 0; i < RootIds; i++)
                g.Assert(g.Root, g.Edge, ids[i], ids[random.Next(RootIds)]);

            g.Root.StageRow(g.Low, [ids[0], g.Int(random.Next(100))]);
            g.Root.AddTerm(g.Add, [ids[1], ids[2]]);
            g.Root.FlushUpdates();

            // One root iteration, so that the children inherit the root's watermarks and still have work of their own.
            g.Root.RunRules(rules);
            var extra = freezeRaces > 0 ? FreezeWhileDefining(g) : FreezeOnly(g);
            Assert.All(rules, r => Assert.True(g.EGraph.GetRuleInfo(r).AllowInheritance));
            var rootIdBound = g.EGraph.Database.ReadCounter(g.EGraph.IdCounter);
            var before = RootSnapshot(g);

            var programs = Enumerable.Range(0, children).Select(i => Program(g, ids, new Random(seed * 1000 + i), rounds, extra)).ToArray();
            var contexts = Enumerable.Range(0, children).Select(_ => g.Root.CreateChild(options)).ToArray();
            Assert.All(contexts, c => Assert.Equal(childThreads, c.Parallelism.DegreeOfParallelism));
            if (forceParallel)
                foreach (var child in contexts)
                    child.Context.Database.ParallelRuleSetsOverride = true;

            var transcripts = new string[children];
            var arrived = 0;
            var degree = Math.Min(children, Math.Max(1, threads / childThreads));
            ConcurrentChildren.Run(contexts, (child, i) =>
            {
                // Best effort to overlap the first bodies of every worker (the thread pool may supply fewer in time).
                Interlocked.Increment(ref arrived);
                SpinWait.SpinUntil(() => Volatile.Read(ref arrived) >= degree, TimeSpan.FromSeconds(1));
                var log = Replay(child, programs[i], rules);
                child.CheckInvariants();
                transcripts[i] = log + Normalize(g, child, (uint)rootIdBound, seen, extra);
            });

            Assert.Equal(before, RootSnapshot(g));
            for (var i = 0; i < children; i++)
            {
                using var replay = g.Root.CreateChild(options);
                if (forceParallel)
                    replay.Context.Database.ParallelRuleSetsOverride = true;

                var log = Replay(replay, programs[i], rules);
                replay.CheckInvariants();
                Assert.Equal(log + Normalize(g, replay, (uint)rootIdBound, seen, extra), transcripts[i]);
                Assert.Equal(before, RootSnapshot(g));
            }

            foreach (var child in contexts)
            {
                child.CheckInvariants();
                child.Dispose();
            }

            Assert.Equal(before, RootSnapshot(g));
            Assert.Equal(threads, g.EGraph.ParallelismBudget.Available);
        }

        /// <summary>
        /// Freezes the root.
        /// </summary>
        /// <returns><c>null</c>: no relation is defined.</returns>
        static FunctionId? FreezeOnly(ContextGraph g)
        {
            g.Root.Freeze();
            return null;
        }

        /// <summary>
        /// Defines the relation <c>extra(id)</c> on a pool thread while this one freezes the root. Either call may be
        /// refused while the other holds the root's domain; the freeze is then repeated, or the relation defined on the
        /// frozen root. However the two calls meet, the relation's root table must end frozen and shared, as every
        /// table of a frozen root is.
        /// </summary>
        /// <returns>The relation.</returns>
        static FunctionId? FreezeWhileDefining(ContextGraph g)
        {
            FunctionId? extra = null;
            using var start = new Barrier(2);
            var definer = Task.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    extra = g.Relation("extra", ColumnTy.Id);
                }
                catch (InvalidOperationException)
                {
                }
            });
            start.SignalAndWait();
            try
            {
                g.Root.Freeze();
            }
            catch (InvalidOperationException)
            {
            }

            definer.Wait();
            if (g.Root.IsFrozen == false)
                g.Root.Freeze();

            extra ??= g.Relation("extra", ColumnTy.Id);
            var schema = g.EGraph.Store.Schema[g.EGraph.GetTable(extra.Value)]!;
            Assert.True(schema.Root.IsShared, "A relation defined while the root froze is not frozen with it.");
            return extra;
        }

        /// <summary>
        /// The rule set, every rule query-determined (constructors, relations, a lattice; no table reads in actions):
        /// <c>edge → path</c>, transitivity, <c>path(x, y) → add(x, y)</c>, commutativity of <c>add</c>, two-cycles
        /// collapse, <c>low</c> flows along edges, and <c>low(x) = v → mark(num(v))</c>; and six rules that write the
        /// relation <c>seen</c> from <c>edge</c>, <c>path</c>, <c>low</c> and <c>add</c>, so that on the parallel executor
        /// the tasks of a child's first iteration create its layer of <c>seen</c> concurrently.
        /// </summary>
        /// <returns>The rules, and the relation <c>seen</c>.</returns>
        static (EGraphRuleId[] Rules, FunctionId Seen) Rules(ContextGraph g)
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

            var seen = g.Relation("seen", ColumnTy.Id);
            foreach (var (source, column) in new[] { (g.Edge, 0), (g.Edge, 1), (g.Path, 0), (g.Path, 1), (g.Low, 0), (g.Add, 2) })
            {
                rb = g.EGraph.NewRule($"seen-{list.Count}", true);
                var columns = new List<RuleEntry>();
                var arity = source == g.Add ? 3 : 2;
                for (var i = 0; i < arity; i++)
                    columns.Add(rb.NewVar(source == g.Low && i == 1 ? g.I64 : ColumnTy.Id));

                if (source != g.Low && source != g.Add)
                    columns.Add(rb.NewVar(g.UnitType));

                rb.QueryTable(source, [.. columns], false);
                rb.Set(seen, [columns[column], RuleEntry.Const(g.Unit, g.UnitType)]);
                list.Add(rb.Build());
            }

            return (list.ToArray(), seen);
        }

        /// <summary>
        /// A random child program in <paramref name="rounds"/> rounds: each stages edges over root ids, a union of root
        /// ids, a term over root ids with an edge from it, a union of a minted term with a root id (from the second
        /// round), a <c>low</c> value, and when <paramref name="extra"/> is given an <c>extra</c> row over a root id or a
        /// minted term; then flushes and saturates. Each step receives the ids the program minted so far, so a replay
        /// mints its own.
        /// </summary>
        static List<Action<EGraphContext, List<Value>>> Program(ContextGraph g, Value[] ids, Random random, int rounds, FunctionId? extra)
        {
            var steps = new List<Action<EGraphContext, List<Value>>>();
            for (var round = 0; round < rounds; round++)
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

                if (round >= 1)
                {
                    var (which, other) = (random.Next(round + 1), ids[random.Next(RootIds)]);
                    steps.Add((c, minted) => c.StageUnion(minted[which], other));
                }

                var (low, value) = (ids[random.Next(RootIds)], random.Next(100));
                steps.Add((c, _) => c.StageRow(g.Low, [low, g.Int(value)]));
                if (extra is { } relation)
                {
                    var (fromRoot, key) = (random.Next(2) == 0, ids[random.Next(RootIds)]);
                    steps.Add((c, minted) => g.Assert(c, relation, fromRoot ? key : minted[^1]));
                }

                steps.Add(null!);
            }

            return steps;
        }

        /// <summary>
        /// Runs a program in <paramref name="child"/>: a <c>null</c> step flushes and saturates. Returns a log of each
        /// saturation's iteration count, which a serial replay must reproduce, since serial runs are deterministic.
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
        /// Writes every function's rows as <paramref name="child"/> sees them, sorted, with root ids kept and every
        /// other class named by its smallest constructor term; <paramref name="extra"/>'s rows too when it is given.
        /// </summary>
        static string Normalize(ContextGraph g, EGraphContext child, uint rootIdBound, FunctionId seen, FunctionId? extra)
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
            Write("seen", seen, [true, false]);
            if (extra is { } relation)
                Write("extra", relation, [true, false]);

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
