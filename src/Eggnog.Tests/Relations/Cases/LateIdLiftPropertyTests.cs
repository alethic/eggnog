using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The lift against its specification (colored-contexts spec §11.2, I33) when the parent mints ids after its cases
    /// reserved their id blocks (late parent ids, L7, §4.2), and when the parent is a child of a writable root. A late id
    /// is larger than every local id of the cases, so a case that unions it with a local id keeps the local id as the
    /// class's canonical id; <see cref="LifterPropertyTests"/> never builds that, since its parent ids all precede the
    /// cases. What the parent holds after the lift over its ids, the late ones included, must be exactly what held in every
    /// live case before it, and nothing the parent holds may name a case's local id. 200 seeds.
    /// </summary>
    public class LateIdLiftPropertyTests
    {

        /// <summary>
        /// The seeds, in batches of 50.
        /// </summary>
        public static TheoryData<int> Batches => new() { 0, 1, 2, 3 };

        [Theory]
        [MemberData(nameof(Batches))]
        public void TheLiftIsSoundAndCompleteOverLateIds(int batch)
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
        /// One seed: the parent is the root, a writable child of a frozen root, or a writable child of a writable root.
        /// The cases mint their locals first, then the parent mints its late ids, then the cases are populated over all
        /// of them; the lift is checked against what held in every live case.
        /// </summary>
        static void RunSeed(int seed)
        {
            var random = new Random(seed);
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var copy = g.EdgeToPath();
            var n = 3 + random.Next(4);
            var early = Enumerable.Range(0, n).Select(_ => g.Root.FreshId()).ToArray();
            Populate(g, g.Root, early, random, unions: random.Next(2), terms: false);
            g.Root.FlushUpdates();

            var shape = seed % 3;
            if (shape == 1)
                g.Root.Freeze();

            using var scope = shape == 0 ? null : g.Root.CreateChild();
            var parent = scope ?? g.Root;
            using var set = new CaseSet(parent);
            var caseCount = 1 + random.Next(3);
            var locals = new List<Value[]>();
            for (var c = 0; c < caseCount; c++)
            {
                var @case = set.AddCase();
                locals.Add(Enumerable.Range(0, 1 + random.Next(3)).Select(_ => @case.FreshId()).ToArray());
            }

            // Late ids: minted by the parent after every case reserved its block.
            var late = Enumerable.Range(0, 1 + random.Next(3)).Select(_ => parent.FreshId()).ToArray();
            Assert.All(late, id => Assert.True(id.Rep > locals.SelectMany(l => l).Max(v => v.Rep)));
            var ids = early.Concat(late).ToArray();

            for (var c = 0; c < caseCount; c++)
            {
                var @case = set.Cases[c];
                var pool = ids.Concat(locals[c]).ToArray();

                // Union each local with a late id first, so the local is canonical for the late id's class.
                foreach (var local in locals[c])
                    if (random.Next(2) == 0)
                        @case.StageUnion(local, late[random.Next(late.Length)]);

                Populate(g, @case, pool, random, unions: random.Next(4), terms: true);
                if (random.Next(8) == 0)
                    @case.StageRow(g.Bottom, [g.Unit]);

                @case.EnsureClean();
                if (@case.IsStopped == false && random.Next(3) == 0)
                    @case.Saturate([copy], 4);
            }

            foreach (var @case in set.Cases)
                if (@case.IsStopped == false)
                    @case.EnsureClean();

            var live = set.Cases.Where(k => k.IsContradictory == false).ToArray();
            var expected = live.Length == 0 ? null : Snapshot(g, live, ids);
            var report = set.Lift(Spec(g), new LiftOptions { CandidateLimit = 4096 });
            Assert.False(report.HitRoundLimit, "the lift hit its round limit");
            Assert.Equal(0, report.Skipped);
            if (live.Length == 0)
            {
                Assert.True(parent.IsContradictory);
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

            // Nothing the parent holds names a case's local id.
            var foreign = new HashSet<uint>(locals.SelectMany(l => l).Select(v => v.Rep));

            // Id columns: every key of edge, path and mark; the output of num; every column of add.
            foreach (var (function, from, to) in new[] { (g.Edge, 0, 2), (g.Path, 0, 2), (g.Mark, 0, 1), (g.Num, 1, 2), (g.Add, 0, 3) })
                parent.ForEach(function, row =>
                {
                    for (var i = from; i < to; i++)
                        Assert.False(foreign.Contains(row.Values[i].Rep), $"the parent's {function} row names local id {row.Values[i].Rep} of a case");
                });

            // Terms over every parent id: an add term every live case holds is in the parent.
            var pool2 = new List<Value>(ids);
            for (var i = 0; i < 4; i++)
                if (parent.LookupId(g.Num, [g.Int(i)]) is { } num)
                    pool2.Add(num);

            foreach (var a in pool2)
                foreach (var b in pool2)
                    if (live.All(k => k.ContainsKey(g.Add, [a, b])))
                        Assert.True(parent.ContainsKey(g.Add, [a, b]), $"add({a.Rep}, {b.Rep}) holds in every live case but not in the parent");
        }

        /// <summary>
        /// Stages random unions, relation rows and (in cases) constructor terms over <paramref name="pool"/>.
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

            for (var i = random.Next(3); i > 0 && made.Count > 0; i--)
            {
                var term = context.StageTerm(g.Add, [made[random.Next(made.Count)], random.Next(2) == 0 ? Pick() : made[random.Next(made.Count)]]);
                if (random.Next(2) == 0)
                    context.StageUnion(term, Pick());
            }
        }

        /// <summary>
        /// What holds in every context of <paramref name="contexts"/> over <paramref name="ids"/>.
        /// </summary>
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

        /// <summary>
        /// The relations and constructors of the context graph.
        /// </summary>
        static LiftSpec Spec(ContextGraph g) => new(
            new LiftTable(g.Edge, LiftTableKind.Relation),
            new LiftTable(g.Path, LiftTableKind.Relation),
            new LiftTable(g.Mark, LiftTableKind.Relation),
            new LiftTable(g.Num, LiftTableKind.Constructor),
            new LiftTable(g.Add, LiftTableKind.Constructor));

    }

}
