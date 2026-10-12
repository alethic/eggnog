using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial soundness review of M10.2 (layered query execution): rules run in real contexts of a store, compared
    /// with a nested-loop join over the rows each context's view shows, with the context's canonical form of every Id
    /// constant (X19).
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class M102SoundnessReviewTests
    {

        /// <summary>
        /// I29 / X21: headers evaluated through a child's view, then a write that changes the view's structure (a new own
        /// layer hiding the root's row), then Build: the rule set records the generation at Build, so the run accepts
        /// headers taken from the old views. Expected: either the run is refused (as for a generation change after Build),
        /// or it derives what a fresh rule set derives.
        /// </summary>
        [Fact]
        public void HeadersEvaluatedBeforeAViewChangeAreNotSilentlyStale()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            Insert(root, f, a, 1, 0);
            root.Freeze();
            using var child = root.CreateChild();

            var log = new List<uint[]>();
            var recorder = root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var rsb = child.Database.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var v = q.NewVariable("v");

            // A fast constraint on the time column: a header, evaluated now, over the child's current view (root only).
            q.AddAtom(f, [x, v, QueryEntry.Wildcard()], [Constraint.GeConst(C(2), new Value(0))]);
            var rb = q.Build();
            rb.CallExternal(recorder, [x, v]);
            rb.Build("f");

            // The child writes f(a)=5: a new own layer, and the root's row hidden (KeyHeld): the view generation moves.
            var before = child.Tables!.ViewGeneration;
            Insert(child, f, a, 5, 0);
            Assert.NotEqual(before, child.Tables!.ViewGeneration);

            using var rs = rsb.Build();
            RuleSetReport? report = null;
            var refused = Record.Exception(() => report = child.Database.RunRuleSet(rs));
            if (refused is not null)
            {
                Assert.IsType<InvalidOperationException>(refused);
                return;
            }

            // Not refused: the run must see f(a)=5, the only row the child's view shows.
            Assert.Equal([$"{a},5"], ExecutionTestUtil.Distinct(log));
        }

        /// <summary>
        /// I29 again: a rule set built in a child, then a write that compacts the child's own layer (enough of its own rows
        /// deleted), then the run. Compaction moves row ids without changing the view's structure, so the generation check
        /// passes and the header subsets, which name row ids, now name other rows. Expected: refused, or exactly the matches
        /// of the current view.
        /// </summary>
        [Fact]
        public void AnOwnLayerCompactedAfterBuildIsNotReadThroughStaleHeaders()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 40).Select(_ => root.NewId()).ToArray();
            root.Freeze();
            using var child = root.CreateChild();
            child.Write(state =>
            {
                for (var i = 0; i < ids.Length; i++)
                    state.StageInsert(f, [ids[i], (uint)(i % 2), state.Timestamp]);

                return 0;
            });

            var log = new List<uint[]>();
            var recorder = root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var rsb = child.Database.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var v = q.NewVariable("v");
            q.AddAtom(f, [x, v, QueryEntry.Wildcard()], [Constraint.EqConst(C(1), new Value(1))]);
            var rb = q.Build();
            rb.CallExternal(recorder, [x, v]);
            rb.Build("odd");
            using var rs = rsb.Build();

            var generation = child.Tables!.ViewGeneration;
            child.Write(state =>
            {
                for (var i = 0; i < 29; i++)
                    state.StageRemove(f, [ids[i]]);

                return 0;
            });

            var layer = child.Tables!.TryGetLayer(f)!.Own;
            var expected = ExecutionTestUtil.Distinct(ScanVisible(child.GetView(f)).Select(r => r.Cells).Where(c => c[1] == 1).Select(c => new[] { c[0], c[1] }));
            var refused = Record.Exception(() => child.Database.RunRuleSet(rs));
            if (refused is not null)
                return;

            Assert.True(expected.SetEquals(ExecutionTestUtil.Distinct(log)),
                $"generation {generation} -> {child.Tables!.ViewGeneration}, own rows {layer.Rows.Count}; expected {string.Join(" ", expected)}, got {string.Join(" ", ExecutionTestUtil.Distinct(log))}");
        }

        /// <summary>
        /// I19 / I3: a child's rule run stamps every row it writes with the run's timestamp (X38). A timestamp below the
        /// child's creation (or its last epoch) must be refused: otherwise the child holds rows stamped below
        /// <see cref="Context.CreatedAt"/>, which I19, I21 and the inherited-watermark proof (I31 case 2) exclude.
        /// </summary>
        [Fact]
        public void AChildRunRefusesATimestampBelowItsCreation()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var g = store.AddTable(Relation(2), name: "g");
            var root = store.Root;
            var a = root.NewId();
            Insert(root, f, a, 1, 0);
            Insert(root, f, a, 2, 0);
            root.Freeze();
            using var child = root.CreateChild();
            Assert.True(child.CreatedAt > 0);

            var rsb = child.Database.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var v = q.NewVariable("v");
            q.AddAtom(f, [x, v, QueryEntry.Wildcard()]);
            var rb = q.Build();
            var ts = rb.ReadCounter(store.ClockCounter);
            rb.Insert(g, [x, x, ts]);
            rb.Build("f to g");
            using var rs = rsb.Build();

            var refused = Record.Exception(() => child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, 0u));
            if (refused is null)
            {
                child.Rebuild();
                var violation = Record.Exception(() => child.CheckInvariants());
                Assert.Fail($"A child's rule run at timestamp 0 (created at {child.CreatedAt}) was accepted; invariants: {violation?.Message ?? "none reported"}");
            }
        }

        /// <summary>
        /// X19 for action constants in a child: the key of an action's <c>Lookup</c> is an Id constant that lost a union in
        /// the child; the child's view holds only the canonical key, so only the canonical form finds it.
        /// </summary>
        [Fact]
        public void AChildCanonicalizesAnActionLookupKey()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var g = store.AddTable(Relation(1), name: "g");
            var root = store.Root;
            var x = root.NewId();
            var c = root.NewId();
            var k = root.NewId();
            Insert(root, f, c, 7, 0);
            Insert(root, g, x, 0);
            root.Freeze();
            using var child = root.CreateChild();
            Assert.True(child.Union(c, k));
            child.Rebuild();
            Assert.Equal(c, child.Find(k));

            var log = new List<uint[]>();
            var recorder = root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            LayeredRuleUtil.Run(child, rsb =>
            {
                var q = rsb.NewRule();
                var v = q.NewVariable("v");
                q.AddAtom(g, [v, QueryEntry.Wildcard()]);
                var rb = q.Build();
                var looked = rb.Lookup(f, [QueryEntry.Const(new Value(k))], C(1));
                rb.CallExternal(recorder, [v, looked]);
                rb.Build("lookup");
            });

            Assert.Equal([$"{x},7"], ExecutionTestUtil.Distinct(log));
        }

        /// <summary>
        /// Concurrent children of a frozen root (their own thread domains, C9) run one shared cached plan whose Id
        /// constant each child canonicalizes differently (X19): variants are made concurrently, the root's catalogs are
        /// built lazily from several threads, and each child must find exactly its own matches.
        /// </summary>
        [Fact]
        public void ConcurrentChildrenRunOneCachedPlanWithTheirOwnConstants()
        {
            for (var round = 0; round < 5; round++)
            {
                var store = new Store(ParallelHeuristics.Serial);
                var r = store.AddTable(Relation(2), name: "r");
                var root = store.Root;
                var ids = Enumerable.Range(0, 400).Select(_ => root.NewId()).ToArray();
                var random = new Random(round);
                root.Write(state =>
                {
                    for (var i = 0; i < 4000; i++)
                        state.StageInsert(r, [ids[random.Next(ids.Length)], ids[random.Next(ids.Length)], state.Timestamp]);

                    return 0;
                });

                var log = new List<uint[]>();
                var recorder = root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
                var written = ids[^1];
                var rsb0 = root.Database.NewRuleSet();
                var q0 = rsb0.NewRule();
                var v0 = q0.NewVariable("v");
                q0.AddAtom(r, [v0, new Value(written), QueryEntry.Wildcard()]);
                var rb0 = q0.Build();
                rb0.CallExternal(recorder, [v0, QueryEntry.IdConst(new Value(written))]);
                var rule0 = rb0.Build("r");
                CachedPlan cached;
                using (var rs0 = rsb0.Build())
                    cached = rs0.BuildCachedPlan(rule0);

                root.Freeze();
                const int Children = 12;
                var children = Enumerable.Range(0, Children).Select(_ => root.CreateChild()).ToArray();
                var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
                System.Threading.Tasks.Parallel.For(0, Children, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                {
                    try
                    {
                        var child = children[i];
                        child.Union(written, ids[i * 7]);
                        child.Rebuild();
                        LayeredRuleUtil.Run(child, rsb => rsb.AddRuleFromCachedPlan(cached));
                    }
                    catch (Exception e)
                    {
                        errors.Add(e);
                    }
                });

                Assert.Empty(errors);
                for (var i = 0; i < Children; i++)
                {
                    var child = children[i];
                    var k = child.Find(written);
                    Assert.Equal(ids[i * 7], k);
                    var expected = new SortedSet<string>(
                        ScanVisible(child.GetView(r)).Select(x => x.Cells).Where(row => row[1] == k).Select(row => $"{row[0]},{k}"),
                        StringComparer.Ordinal);
                    var got = new SortedSet<string>(log.Where(e => e[1] == k).Select(e => $"{e[0]},{e[1]}"), StringComparer.Ordinal);
                    Assert.True(expected.SetEquals(got), $"round {round} child {i}: expected {expected.Count}, got {got.Count}");
                    child.CheckInvariants();
                }

                foreach (var child in children)
                    child.Dispose();
            }
        }

        /// <summary>
        /// Random chains of real contexts (frozen and writable ancestors, unions, local ids, deletes where allowed, parent
        /// writes after a child exists), then one random conjunctive rule run in the deepest context: its distinct matches
        /// must be a nested-loop join's over the rows the context's view shows, with Id constants in their canonical form
        /// there (X19), serially and on the parallel executor.
        /// </summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void RandomContextChainsMatchANestedLoopOverTheView(bool parallel, bool large)
        {
            var failures = new List<string>();
            var withMatches = 0;
            var withHides = 0;
            for (var seed = 0; seed < (large ? 300 : 800) && failures.Count < 5; seed++)
            {
                var failure = RunSeed(seed, parallel, large, out var matched, out var hid);
                if (failure is not null)
                    failures.Add(failure);

                withMatches += matched ? 1 : 0;
                withHides += hid ? 1 : 0;
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine + "----" + Environment.NewLine, failures));
            Assert.True(withMatches > 50, $"only {withMatches} seeds had matches");
            Assert.True(withHides > 50, $"only {withHides} seeds hid rows");
        }

        /// <summary>
        /// One seed of <see cref="RandomContextChainsMatchANestedLoopOverTheView"/>.
        /// </summary>
        /// <returns>A description of the mismatch, or <c>null</c>.</returns>
        static string? RunSeed(int seed, bool parallel, bool large, out bool matched, out bool hid)
        {
            matched = false;
            hid = false;
            var rng = new Random(seed * 7919 + (large ? 1 : 0));
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var r = store.AddTable(Relation(2), name: "r");
            var g = store.AddTable(Relation(1), name: "g");
            var root = store.Root;
            var ids = new List<uint>();
            for (var i = 0; i < (large ? 80 : 10); i++)
                ids.Add(root.NewId());

            var log = new List<uint[]>();
            var recorder = root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var trace = new List<string>();
            var depth = 1 + rng.Next(3);
            var current = root;
            var chain = new List<Context> { root };
            for (var level = 0; ; level++)
            {
                var allowRemove = level == 0 || chain[level - 1].IsFrozen;
                RandomOps(rng, current, ids, f, r, g, rng.Next(1, 14), allowRemove, trace, level, large);
                current.Rebuild();
                if (level == depth)
                    break;

                if ((level == 0 || chain[level - 1].IsFrozen) && rng.Next(2) == 0)
                {
                    current.Freeze();
                    trace.Add($"L{level} freeze");
                }

                var options = parallel ? new ContextOptions { Parallelism = new ParallelHeuristics(4) } : null;
                var child = current.CreateChild(options);
                if (parallel)
                    child.Database.ParallelRuleSetsOverride = true;

                if (current.IsFrozen == false && rng.Next(2) == 0)
                {
                    RandomOps(rng, current, ids, f, r, g, rng.Next(1, 6), false, trace, level, large);
                    current.Rebuild();
                }

                chain.Add(child);
                current = child;
                trace.Add($"L{level + 1} created");
                if (rng.Next(2) == 0)
                    ids.Add(current.NewId());
            }

            var leaf = current;
            leaf.Rebuild();
            hid = leaf.Tables!.Statistics.RowsHidden > 0 || chain.Any(c => c.Tables is { } t && t.Statistics.RowsHidden > 0);
            var tables = new[] { f, r, g };
            var visible = tables.ToDictionary(t => t, t => VisibleRows(leaf, t));
            var atoms = RandomQuery(rng, store, tables, ids);
            var strategy = (PlanStrategy)rng.Next(3);
            var noDecomposition = rng.Next(2) == 0;
            var bound = atoms.SelectMany(a => a.Entries).Where(e => e.IsVariable && e.IsWildcard == false).Select(e => e.Variable).Distinct().OrderBy(v => v.Index).ToArray();
            var expected = Reference(leaf, store, visible, atoms, bound);
            log.Clear();
            try
            {
                LayeredRuleUtil.Run(leaf, rsb =>
                {
                    var q = rsb.NewRule();
                    q.Strategy = strategy;
                    q.NoDecomposition = noDecomposition;
                    for (var i = 0; i < 5; i++)
                        q.NewVariable("v" + i);

                    foreach (var atom in atoms)
                        q.AddAtom(atom.Table, atom.Entries, atom.Constraints);

                    var rb = q.Build();
                    rb.CallExternal(recorder, bound.Select(v => QueryEntry.Var(v)).ToArray());
                    rb.Build("rule");
                });
            }
            catch (Exception e)
            {
                return $"seed {seed} parallel {parallel}: threw {e}\n{string.Join("\n", trace)}";
            }

            var got = ExecutionTestUtil.Distinct(log);
            matched = expected.Count > 0;
            if (expected.SetEquals(got))
            {
                // The semi-naive split of the same rule from its cached plan at a random boundary W: variant i takes atom
                // i's rows at or above W and atoms before it below W, plus every atom below W: together they find every match
                // (I31 Lemma 1).
                CachedPlan cached;
                var rsb0 = leaf.Database.NewRuleSet();
                var q0 = rsb0.NewRule();
                q0.Strategy = strategy;
                q0.NoDecomposition = noDecomposition;
                for (var i = 0; i < 5; i++)
                    q0.NewVariable("v" + i);

                foreach (var atom in atoms)
                    q0.AddAtom(atom.Table, atom.Entries, atom.Constraints);

                var rb0 = q0.Build();
                rb0.CallExternal(recorder, bound.Select(v => QueryEntry.Var(v)).ToArray());
                var rule0 = rb0.Build("rule");
                using (var rs0 = rsb0.Build())
                    cached = rs0.BuildCachedPlan(rule0);

                var w = (uint)rng.Next(1, (int)store.ReadClock() + 1);
                log.Clear();
                try
                {
                    LayeredRuleUtil.Run(leaf, rsb =>
                    {
                        for (var i = 0; i < atoms.Count; i++)
                        {
                            var extras = new List<(AtomId, Constraint)>();
                            for (var j = 0; j < i; j++)
                                extras.Add((new AtomId(j), Constraint.LtConst(C(store.Schema[atoms[j].Table]!.SortColumn), new Value(w))));

                            extras.Add((new AtomId(i), Constraint.GeConst(C(store.Schema[atoms[i].Table]!.SortColumn), new Value(w))));
                            rsb.AddRuleFromCachedPlan(cached, extras.ToArray());
                        }

                        // And every atom old: the matches a semi-naive run at W would leave to earlier runs.
                        var old = new List<(AtomId, Constraint)>();
                        for (var j = 0; j < atoms.Count; j++)
                            old.Add((new AtomId(j), Constraint.LtConst(C(store.Schema[atoms[j].Table]!.SortColumn), new Value(w))));

                        rsb.AddRuleFromCachedPlan(cached, old.ToArray());
                    });
                }
                catch (Exception e)
                {
                    return $"seed {seed} parallel {parallel} (semi-naive at {w}): threw {e}\n{string.Join("\n", trace)}";
                }

                var split = ExecutionTestUtil.Distinct(log);
                if (expected.SetEquals(split))
                    return null;

                return $"seed {seed} parallel {parallel} semi-naive split at {w}, {strategy} noDecomposition {noDecomposition}\nquery {string.Join("; ", atoms)}\n" +
                       $"expected {string.Join(" ", expected)}\ngot      {string.Join(" ", split)}\n" + string.Join("\n", trace);
            }

            return $"seed {seed} parallel {parallel} depth {depth} {strategy} noDecomposition {noDecomposition}\nquery {string.Join("; ", atoms)}\n" +
                   $"expected {string.Join(" ", expected)}\ngot      {string.Join(" ", got)}\n" +
                   string.Join("\n", tables.Select(t => $"{store.Schema.GetName(t)}: {string.Join(" ", visible[t].Select(row => "[" + string.Join(",", row) + "]"))}")) +
                   "\n" + string.Join("\n", trace);
        }

        /// <summary>
        /// Applies random writes, unions and (where allowed) deletes to <paramref name="context"/>, one batch each.
        /// </summary>
        static void RandomOps(Random rng, Context context, List<uint> ids, TableId f, TableId r, TableId g, int count, bool allowRemove, List<string> trace, int level, bool large)
        {
            if (large)
            {
                // One bulk batch first, so layers cross the executor's size thresholds.
                var bulk = rng.Next(20, 400);
                context.Write(state =>
                {
                    for (var i = 0; i < bulk; i++)
                    {
                        var a = ids[rng.Next(ids.Count)];
                        var b = ids[rng.Next(ids.Count)];
                        switch (rng.Next(3))
                        {
                            case 0:
                                state.StageInsert(f, [a, (uint)rng.Next(4), state.Timestamp]);
                                break;
                            case 1:
                                state.StageInsert(r, [a, b, state.Timestamp]);
                                break;
                            default:
                                state.StageInsert(g, [a, state.Timestamp]);
                                break;
                        }
                    }

                    return 0;
                });

                trace.Add($"L{level} bulk {bulk}");
            }

            for (var i = 0; i < count; i++)
            {
                var a = ids[rng.Next(ids.Count)];
                var b = ids[rng.Next(ids.Count)];
                switch (rng.Next(allowRemove ? 7 : 6))
                {
                    case 0:
                    case 1:
                        var value = (uint)rng.Next(4);
                        Insert(context, f, a, value, 0);
                        trace.Add($"L{level} f({a})={value}");
                        break;
                    case 2:
                    case 3:
                        Insert(context, r, a, b, 0);
                        trace.Add($"L{level} r({a},{b})");
                        break;
                    case 4:
                        Insert(context, g, a, 0);
                        trace.Add($"L{level} g({a})");
                        break;
                    case 5:
                        context.Union(a, b);
                        context.Rebuild();
                        trace.Add($"L{level} union({a},{b})");
                        break;
                    default:
                        if (rng.Next(2) == 0)
                        {
                            Remove(context, f, a);
                            trace.Add($"L{level} remove f({a})");
                        }
                        else
                        {
                            Remove(context, r, a, b);
                            trace.Add($"L{level} remove r({a},{b})");
                        }

                        break;
                }
            }
        }

        /// <summary>
        /// The rows <paramref name="context"/> sees of <paramref name="table"/>.
        /// </summary>
        static List<uint[]> VisibleRows(Context context, TableId table)
        {
            if (context.IsRoot)
                return TableTestUtil.LiveRows(context.Store.Schema[table]!.Root);

            return ScanVisible(context.GetView(table)).Select(x => x.Cells).ToList();
        }

        /// <summary>
        /// A random query of one to three atoms over <c>f(id, num, ts)</c>, <c>r(id, id, ts)</c> and <c>g(id, ts)</c>:
        /// variables, Id constants from the pool (possibly not canonical in the running context), numeric constants on
        /// <c>f</c>'s value, wildcards on time columns, and sometimes a time range (a header).
        /// </summary>
        static List<RandomAtom> RandomQuery(Random rng, Store store, TableId[] tables, List<uint> ids)
        {
            var atoms = new List<RandomAtom>();
            for (var n = 1 + rng.Next(3); n > 0; n--)
            {
                var table = tables[rng.Next(tables.Length)];
                var schema = store.Schema[table]!;
                var arity = schema.Arity;
                var entries = new QueryEntry[arity];
                for (var c = 0; c < arity; c++)
                {
                    if (c == schema.SortColumn)
                    {
                        entries[c] = QueryEntry.Wildcard();
                        continue;
                    }

                    var isId = schema.RebuildColumns.ToArray().Contains(new ColumnId(c));
                    var roll = rng.Next(10);
                    entries[c] = roll < 7
                        ? QueryEntry.Var(new Variable(rng.Next(5)))
                        : roll < 9
                            ? QueryEntry.Const(new Value(isId ? ids[rng.Next(ids.Count)] : (uint)rng.Next(4)))
                            : QueryEntry.Wildcard();
                }

                var constraints = new List<Constraint>();
                if (rng.Next(4) == 0)
                    constraints.Add(Constraint.GeConst(C(schema.SortColumn), new Value((uint)rng.Next(1, 12))));

                atoms.Add(new RandomAtom(table, entries, constraints.ToArray()));
            }

            return atoms;
        }

        /// <summary>
        /// The nested-loop join over <paramref name="visible"/>, with Id constants canonical in <paramref name="context"/>.
        /// </summary>
        static SortedSet<string> Reference(Context context, Store store, Dictionary<TableId, List<uint[]>> visible, List<RandomAtom> atoms, Variable[] bound)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            var binding = new Dictionary<int, uint>();
            Recurse(0);
            return result;

            void Recurse(int depth)
            {
                if (depth == atoms.Count)
                {
                    result.Add(string.Join(",", bound.Select(v => binding[v.Index])));
                    return;
                }

                var atom = atoms[depth];
                var idColumns = store.Schema[atom.Table]!.RebuildColumns.ToArray();
                foreach (var row in visible[atom.Table])
                {
                    if (Constraint.EvaluateAll(atom.Constraints, row) == false)
                        continue;

                    var added = new List<int>();
                    var ok = true;
                    for (var c = 0; c < row.Length && ok; c++)
                    {
                        var e = atom.Entries[c];
                        if (e.IsVariable == false)
                        {
                            var constant = e.Constant.Rep;
                            if (idColumns.Contains(new ColumnId(c)))
                                constant = context.Find(constant);

                            ok = row[c] == constant;
                        }
                        else if (e.IsWildcard == false)
                        {
                            if (binding.TryGetValue(e.Variable.Index, out var v))
                            {
                                ok = v == row[c];
                            }
                            else
                            {
                                binding[e.Variable.Index] = row[c];
                                added.Add(e.Variable.Index);
                            }
                        }
                    }

                    if (ok)
                        Recurse(depth + 1);

                    foreach (var v in added)
                        binding.Remove(v);
                }
            }
        }

    }

}
