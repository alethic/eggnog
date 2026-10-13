using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// I33 for a parent that is itself a case (the phase-B lift of colored-contexts spec §11.3, which
    /// <see cref="LifterPropertyTests"/> does not cover): the parent holds local ids, terms and unions of its own, and its
    /// cases merge over both the root's ids and the parent's local ids. After the lift, what the parent holds over those
    /// ids is exactly what held in every live case before it. 200 seeds.
    /// </summary>
    public class NestedLiftPropertyReviewTests
    {

        public static TheoryData<int> Batches => new() { 0, 1, 2, 3 };

        [Theory]
        [MemberData(nameof(Batches))]
        public void TheLiftIntoACaseIsSoundAndComplete(int batch)
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

        static void RunSeed(int seed)
        {
            var random = new Random(seed);
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var copy = g.EdgeToPath();
            var transitive = g.Transitive();
            var rootIds = Enumerable.Range(0, 4 + random.Next(4)).Select(_ => g.Root.FreshId()).ToArray();
            Populate(g, g.Root, rootIds, random, random.Next(2), terms: false);
            g.Root.FlushUpdates();

            using var outer = new CaseSet(g.Root);
            var parent = outer.AddCase();
            var parentLocals = Enumerable.Range(0, 1 + random.Next(3)).Select(_ => parent.FreshId()).ToArray();
            var ids = rootIds.Concat(parentLocals).ToArray();
            Populate(g, parent, ids, random, random.Next(3), terms: true);
            parent.EnsureClean();
            if (parent.IsStopped)
                return;

            using var set = new CaseSet(parent);
            var caseCount = 1 + random.Next(3);
            for (var c = 0; c < caseCount; c++)
            {
                var @case = set.AddCase();
                var locals = Enumerable.Range(0, random.Next(3)).Select(_ => @case.FreshId()).ToArray();
                Populate(g, @case, ids.Concat(locals).ToArray(), random, random.Next(5), terms: true);
                if (random.Next(8) == 0)
                    @case.StageRow(g.Bottom, [g.Unit]);

                @case.EnsureClean();
                if (@case.IsStopped == false && random.Next(2) == 0)
                    @case.Saturate([copy, transitive], 8);
            }

            foreach (var @case in set.Cases)
                if (@case.IsStopped == false)
                    @case.EnsureClean();

            var live = set.Cases.Where(c => c.IsContradictory == false).ToArray();
            var expected = live.Length == 0 ? null : Snapshot(g, live, ids);
            var report = set.Lift(Spec(g), new LiftOptions { CandidateLimit = 4096 });
            Assert.False(report.HitRoundLimit);
            Assert.Equal(0, report.Skipped);
            if (live.Length == 0)
            {
                Assert.True(parent.IsStopped);
                return;
            }

            parent.CheckInvariants();
            foreach (var @case in live)
                @case.CheckInvariants();

            var actual = Snapshot(g, [parent], ids);
            Assert.Equal(expected!.Equalities, actual.Equalities);
            Assert.Equal(expected.Edges, actual.Edges);
            Assert.Equal(expected.Paths, actual.Paths);
            Assert.Equal(expected.Marks, actual.Marks);
            Assert.Equal(expected.Nums, actual.Nums);
            Assert.Equal(expected.NumEqualities, actual.NumEqualities);
            for (var i = 0; i < ids.Length; i++)
                if (expected.Lows[i] is { } join)
                    Assert.Equal(g.Int(join), parent.LookupId(g.Low, [ids[i]]));
        }

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

        static LiftFacts Snapshot(ContextGraph g, IReadOnlyList<EGraphContext> contexts, Value[] ids)
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

            return facts;
        }

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
