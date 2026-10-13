using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The lift against its specification (colored-contexts spec §11.2, §17 M10.4, I33): over random parents (a writable
    /// root, or a writable child of a frozen root) and random cases (unions over parent and local ids, relation rows,
    /// lattice values, constructor terms, rule runs, vacuous cases), what the parent holds after the lift over its
    /// original ids is exactly E, the equalities, facts and joined values that held in every live case before it:
    /// soundness (⊆ E) and completeness (⊇ E). Terms are checked for completeness over every parent id after the lift.
    /// 400 seeds.
    /// </summary>
    public class LifterPropertyTests
    {

        /// <summary>
        /// The seeds, in batches of 50.
        /// </summary>
        public static TheoryData<int> Batches => new() { 0, 1, 2, 3, 4, 5, 6, 7 };

        [Theory]
        [MemberData(nameof(Batches))]
        public void TheLiftIsSoundAndCompleteForE(int batch)
        {
            for (var seed = batch * 50; seed < (batch + 1) * 50; seed++)
            {
                try
                {
                    RunSeed(seed);
                }
                catch (Exception e) when (e is not SeedFailure)
                {
                    throw new SeedFailure(seed, e);
                }
            }
        }

        /// <summary>
        /// One seed: builds the parent and cases, records E, lifts, and checks the parent against E.
        /// </summary>
        static void RunSeed(int seed)
        {
            var random = new Random(seed);
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var copy = g.EdgeToPath();
            var transitive = g.Transitive();
            var n = 5 + random.Next(5);
            var ids = Enumerable.Range(0, n).Select(_ => g.Root.FreshId()).ToArray();

            // The root's own facts.
            Populate(g, g.Root, ids, random, unions: random.Next(2), terms: false);
            g.Root.FlushUpdates();

            using var frozenScope = seed % 2 == 1 ? Freeze(g) : null;
            var parent = frozenScope ?? g.Root;
            if (frozenScope is not null)
            {
                Populate(g, parent, ids, random, unions: random.Next(2), terms: false);
                parent.FlushUpdates();
            }

            using var set = new CaseSet(parent);
            var caseCount = 1 + random.Next(4);
            for (var c = 0; c < caseCount; c++)
            {
                var @case = set.AddCase();
                var locals = Enumerable.Range(0, random.Next(3)).Select(_ => @case.FreshId()).ToArray();
                Populate(g, @case, ids.Concat(locals).ToArray(), random, unions: random.Next(5), terms: true);
                if (random.Next(7) == 0)
                    @case.StageRow(g.Bottom, [g.Unit]);

                @case.EnsureClean();
                if (@case.IsStopped == false && random.Next(3) == 0)
                    @case.Saturate([copy, transitive], 8);
            }

            foreach (var @case in set.Cases)
                if (@case.IsStopped == false)
                    @case.EnsureClean();

            var live = set.Cases.Where(c => c.IsContradictory == false).ToArray();
            var expected = live.Length == 0 ? null : Snapshot(g, parent, live, ids);
            var lowBefore = ids.Select(x => parent.LookupId(g.Low, [x])).ToArray();

            var report = set.Lift(Spec(g), new LiftOptions { CandidateLimit = 4096 });
            Assert.False(report.HitRoundLimit, "the lift hit its round limit");
            Assert.Equal(0, report.Skipped);
            Assert.Equal(live.Length, report.LiveCases);

            if (live.Length == 0)
            {
                Assert.True(report.ParentContradicted);
                Assert.True(parent.IsContradictory);
                return;
            }

            parent.CheckInvariants();
            foreach (var @case in live)
                @case.CheckInvariants();

            var actual = Snapshot(g, parent, [parent], ids);
            Assert.Equal(expected!.Equalities, actual.Equalities);
            Assert.Equal(expected.Edges, actual.Edges);
            Assert.Equal(expected.Paths, actual.Paths);
            Assert.Equal(expected.Marks, actual.Marks);
            Assert.Equal(expected.Nums, actual.Nums);
            Assert.Equal(expected.NumEqualities, actual.NumEqualities);

            // Lattice values: where every live case held one, the parent holds their join; elsewhere what it held.
            for (var i = 0; i < ids.Length; i++)
            {
                var lifted = parent.LookupId(g.Low, [ids[i]]);
                if (expected.Lows[i] is { } join)
                    Assert.Equal(g.Int(join), lifted);
                else
                    Assert.Equal(lowBefore[i], lifted);
            }

            CheckTermCompleteness(g, parent, live, ids);
        }

        /// <summary>
        /// Freezes the root and returns a writable child of it to lift into.
        /// </summary>
        static EGraphContext Freeze(ContextGraph g)
        {
            g.Root.Freeze();
            return g.Root.CreateChild();
        }

        /// <summary>
        /// Stages random unions, relation rows, lattice values and (in cases) constructor terms in
        /// <paramref name="context"/> over <paramref name="pool"/>.
        /// </summary>
        static void Populate(ContextGraph g, EGraphContext context, Value[] pool, Random random, int unions, bool terms)
        {
            Value Pick() => pool[random.Next(pool.Length)];
            for (var i = 0; i < unions; i++)
                context.StageUnion(Pick(), Pick());

            for (var i = random.Next(5); i > 0; i--)
                g.Assert(context, g.Edge, Pick(), Pick());

            for (var i = random.Next(3); i > 0; i--)
                g.Assert(context, g.Mark, Pick());

            for (var i = random.Next(3); i > 0; i--)
                context.StageRow(g.Low, [Pick(), g.Int(random.Next(10))]);

            if (terms == false)
                return;

            var made = new List<Value>();
            for (var i = random.Next(3); i > 0; i--)
            {
                var term = context.StageTerm(g.Num, [g.Int(random.Next(4))]);
                made.Add(term);
                if (random.Next(2) == 0)
                    context.StageUnion(term, Pick());
            }

            for (var i = random.Next(2); i > 0 && made.Count > 0; i--)
            {
                var term = context.StageTerm(g.Add, [made[random.Next(made.Count)], random.Next(2) == 0 ? Pick() : made[random.Next(made.Count)]]);
                if (random.Next(2) == 0)
                    context.StageUnion(term, Pick());
            }
        }

        /// <summary>
        /// What holds in every context of <paramref name="contexts"/> over the parent's original ids.
        /// </summary>
        static LiftFacts Snapshot(ContextGraph g, EGraphContext parent, IReadOnlyList<EGraphContext> contexts, Value[] ids)
        {
            var facts = new LiftFacts();
            var n = ids.Length;
            for (var a = 0; a < n; a++)
            {
                for (var b = a + 1; b < n; b++)
                    if (contexts.All(k => k.Find(ids[a]) == k.Find(ids[b])))
                        facts.Equalities.Add((a, b));

                if (contexts.All(k => k.ContainsKey(g.Mark, [ids[a]])))
                    facts.Marks.Add(a);

                for (var b = 0; b < n; b++)
                {
                    if (contexts.All(k => k.ContainsKey(g.Edge, [ids[a], ids[b]])))
                        facts.Edges.Add((a, b));

                    if (contexts.All(k => k.ContainsKey(g.Path, [ids[a], ids[b]])))
                        facts.Paths.Add((a, b));
                }

                var lows = contexts.Select(k => k.LookupId(g.Low, [ids[a]])).ToArray();
                facts.Lows.Add(lows.All(v => v is not null) ? lows.Max(v => g.EGraph.BaseValues.Unwrap<Int64Codec, long>(v!.Value)) : null);
            }

            for (var i = 0; i < 4; i++)
            {
                var outputs = contexts.Select(k => k.LookupId(g.Num, [g.Int(i)])).ToArray();
                if (outputs.Any(o => o is null))
                    continue;

                facts.Nums.Add(i);
                for (var x = 0; x < n; x++)
                    if (contexts.Select((k, j) => k.Find(outputs[j]!.Value) == k.Find(ids[x])).All(b => b))
                        facts.NumEqualities.Add((i, x));
            }

            _ = parent;
            return facts;
        }

        /// <summary>
        /// Completeness over every parent id after the lift, terms included: equal in every live case implies equal in the
        /// parent; an <c>add</c> term every live case holds over parent ids is in the parent.
        /// </summary>
        static void CheckTermCompleteness(ContextGraph g, EGraphContext parent, IReadOnlyList<EGraphContext> live, Value[] ids)
        {
            var pool = new List<Value>(ids);
            for (var i = 0; i < 4; i++)
                if (parent.LookupId(g.Num, [g.Int(i)]) is { } num)
                    pool.Add(num);

            parent.ForEach(g.Add, row => pool.Add(row.Values[2]));
            for (var a = 0; a < pool.Count; a++)
            {
                for (var b = 0; b < pool.Count; b++)
                {
                    if (b > a && live.All(k => k.Find(pool[a]) == k.Find(pool[b])))
                        Assert.True(parent.Find(pool[a]) == parent.Find(pool[b]), $"ids {pool[a].Rep} and {pool[b].Rep} are equal in every live case but not in the parent");

                    if (live.All(k => k.ContainsKey(g.Add, [pool[a], pool[b]])))
                        Assert.True(parent.ContainsKey(g.Add, [pool[a], pool[b]]), $"add({pool[a].Rep}, {pool[b].Rep}) holds in every live case but not in the parent");
                }
            }
        }

        /// <summary>
        /// The spec the property lifts with: every table of the context graph.
        /// </summary>
        static LiftSpec Spec(ContextGraph g) => new(
            new LiftTable(g.Edge, LiftTableKind.Relation),
            new LiftTable(g.Path, LiftTableKind.Relation),
            new LiftTable(g.Mark, LiftTableKind.Relation),
            new LiftTable(g.Num, LiftTableKind.Constructor),
            new LiftTable(g.Add, LiftTableKind.Constructor),
            new LiftTable(g.Low, LiftTableKind.Function, (a, b) =>
            {
                var values = g.EGraph.BaseValues;
                return values.Get<Int64Codec, long>(Math.Max(values.Unwrap<Int64Codec, long>(new Value(a)), values.Unwrap<Int64Codec, long>(new Value(b)))).Rep;
            }));

    }

}
