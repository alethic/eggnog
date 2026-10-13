using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Id constants of rules in contexts: every store-backed context, the root included, runs a rule's id constants
    /// (query, action and primitive operands) in their canonical form in that context, and a watermark set under other
    /// canonical forms is not used, so a constant that lost a union forces a full run.
    /// </summary>
    public class IdConstantContextTests
    {

        /// <summary>
        /// Mints <paramref name="count"/> root ids, in increasing order.
        /// </summary>
        static Value[] Ids(ContextGraph g, int count) => Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();

        /// <summary>
        /// <c>edge(x, #c) → mark(x)</c>: a child in which <c>c</c> lost a union to <c>b</c> matches the root's row
        /// <c>edge(a, b)</c>; the root and a sibling, where <c>c</c> is canonical, do not.
        /// </summary>
        [Fact]
        public void AQueryConstantRunsCanonicallyInAChild()
        {
            var g = new ContextGraph();
            var (a, b, c) = Ids(g, 3) is var ids ? (ids[0], ids[1], ids[2]) : default;
            var rb = g.EGraph.NewRule("edge-to-c", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Edge, [x, RuleEntry.Const(c, ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Set(g.Mark, [x, RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();

            g.Assert(g.Root, g.Edge, a, b);
            g.Root.FlushUpdates();
            g.Root.RunRules(rule);
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            using var sibling = g.Root.CreateChild();
            child.StageUnion(b, c);
            child.FlushUpdates();
            child.RunRules(rule);
            sibling.RunRules(rule);
            Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(child, g.Mark));
            Assert.Empty(ContextGraph.Keys(sibling, g.Mark));
            Assert.Empty(ContextGraph.Keys(g.Root, g.Mark));
            child.CheckInvariants();
            sibling.CheckInvariants();
        }

        /// <summary>
        /// <c>mark(x) → path(x, #c)</c> and <c>mark(x) → union(x, #c)</c>: in a child where <c>c</c> lost to <c>b</c>,
        /// the action constants are written as <c>b</c>; the root writes them as <c>c</c>.
        /// </summary>
        [Fact]
        public void ActionConstantsAreWrittenCanonicallyInAChild()
        {
            var g = new ContextGraph();
            var ids = Ids(g, 4);
            var (a, b, c, d) = (ids[0], ids[1], ids[2], ids[3]);
            var rb = g.EGraph.NewRule("mark-path-c", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Mark, [x, rb.NewVar(g.UnitType)], false);
            rb.Set(g.Path, [x, RuleEntry.Const(c, ColumnTy.Id), RuleEntry.Const(g.Unit, g.UnitType)]);
            var pathRule = rb.Build();

            var ub = g.EGraph.NewRule("mark-union-c", true);
            var y = ub.NewVar(ColumnTy.Id);
            ub.QueryTable(g.Mark, [y, ub.NewVar(g.UnitType)], false);
            ub.Union(y, RuleEntry.Const(c, ColumnTy.Id));
            var unionRule = ub.Build();

            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            child.StageUnion(b, c);
            g.Assert(child, g.Mark, d);
            child.FlushUpdates();
            child.RunRules(pathRule);
            Assert.Equal([$"{d.Rep} {b.Rep}"], ContextGraph.Keys(child, g.Path));
            child.RunRules(unionRule);
            Assert.Equal(b, child.Find(d));
            Assert.Equal(b, child.Find(c));
            Assert.Equal(a, child.Find(a));
            child.CheckInvariants();

            using var sibling = g.Root.CreateChild();
            g.Assert(sibling, g.Mark, d);
            sibling.FlushUpdates();
            sibling.RunRules(pathRule, unionRule);
            Assert.Equal([$"{c.Rep} {c.Rep}"], ContextGraph.Keys(sibling, g.Path));
            Assert.Equal(c, sibling.Find(d));
            sibling.CheckInvariants();
        }

        /// <summary>
        /// <c>edge(x, y), y != #c → mark(x)</c>, the constant an operand of a primitive (flagged as an id, since a
        /// primitive's operand has no table column to say so): over the frozen root's <c>edge(a, b)</c>, a child in
        /// which <c>b</c> and <c>c</c> are one class filters the match out, whichever of them is canonical, while a
        /// sibling derives <c>mark(a)</c>; a root that unions them filters it out too (the root canonicalizes its
        /// constants as a child does).
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void APrimitiveConstantRunsCanonically(bool constantWins)
        {
            for (var atRoot = 0; atRoot < 2; atRoot++)
            {
                var g = new ContextGraph();
                var ids = Ids(g, 3);
                var a = ids[0];
                var (b, c) = constantWins ? (ids[2], ids[1]) : (ids[1], ids[2]);
                var ne = BridgeTestUtil.Register(g.EGraph, (state, args) => args[0] != args[1] ? args[0] : null);
                var rb = g.EGraph.NewRule("edge-not-c", true);
                var x = rb.NewVar(ColumnTy.Id);
                var y = rb.NewVar(ColumnTy.Id);
                rb.QueryTable(g.Edge, [x, y, rb.NewVar(g.UnitType)], false);
                rb.QueryPrim(ne, [y, RuleEntry.Const(c, ColumnTy.Id), rb.NewVar(ColumnTy.Id)], ColumnTy.Id);
                rb.Set(g.Mark, [x, RuleEntry.Const(g.Unit, g.UnitType)]);
                var rule = rb.Build();

                g.Assert(g.Root, g.Edge, a, b);
                g.Root.FlushUpdates();
                if (atRoot == 1)
                {
                    g.Root.StageUnion(b, c);
                    g.Root.FlushUpdates();
                    g.Root.RunRules(rule);
                    Assert.Empty(ContextGraph.Keys(g.Root, g.Mark));
                    continue;
                }

                g.Root.Freeze();
                using var child = g.Root.CreateChild();
                using var sibling = g.Root.CreateChild();
                child.StageUnion(b, c);
                child.FlushUpdates();
                child.RunRules(rule);
                sibling.RunRules(rule);
                Assert.Empty(ContextGraph.Keys(child, g.Mark));
                Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(sibling, g.Mark));
                child.CheckInvariants();
                sibling.CheckInvariants();
            }
        }

        /// <summary>
        /// A grandchild C inherits a rule's watermark from the root G through P; when the rule's constant lost a union
        /// in P after G ran, the canonical forms of the rule's constants differ from those G's run stored, the seed is
        /// refused, and C's full run matches the old row of the constant's new class.
        /// </summary>
        [Fact]
        public void AnInheritedWatermarkIsRefusedWhenAConstantLostAUnionOnThePath()
        {
            var g = new ContextGraph();
            var ids = Ids(g, 3);
            var (a, b, c) = (ids[0], ids[1], ids[2]);
            var rb = g.EGraph.NewRule("mark-c", true);
            rb.QueryTable(g.Mark, [RuleEntry.Const(c, ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Set(g.Path, [RuleEntry.Const(c, ColumnTy.Id), RuleEntry.Const(a, ColumnTy.Id), RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();

            g.Assert(g.Root, g.Mark, b);
            g.Root.FlushUpdates();
            g.Root.RunRules(rule);
            Assert.Empty(ContextGraph.Keys(g.Root, g.Path));
            g.Root.Freeze();

            using var p = g.Root.CreateChild();
            p.StageUnion(b, c);
            p.FlushUpdates();
            p.Freeze();
            using var child = p.CreateChild();
            var report = child.RunRules(rule);
            Assert.Equal(1, report.RuleSetReport.NumMatches("mark-c"));
            Assert.Equal([$"{b.Rep} {a.Rep}"], ContextGraph.Keys(child, g.Path));

            // With the same canonical constants, the seed holds: a sibling of P that never unioned matches nothing new.
            using var q = g.Root.CreateChild();
            Assert.Equal(0, q.RunRules(rule).RuleSetReport.NumMatches("mark-c"));
            child.CheckInvariants();
            q.CheckInvariants();
        }

        /// <summary>
        /// A rule defined after a child minted a local id, with that id as a constant: the root, which never saw the
        /// id, matches nothing; the child matches its own row; after the child unions the local id into a root id, the
        /// constant runs as the root id and matches the root's row too.
        /// </summary>
        [Fact]
        public void ALocalIdConstantRunsInItsChild()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, b);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var local = child.FreshId();
            g.Assert(child, g.Edge, b, local);
            child.FlushUpdates();

            var rb = g.EGraph.NewRule("to-local", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Edge, [x, RuleEntry.Const(local, ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Set(g.Mark, [x, RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();

            child.RunRules(rule);
            Assert.Equal([$"{b.Rep}"], ContextGraph.Keys(child, g.Mark));
            child.StageUnion(local, b);
            child.FlushUpdates();
            child.RunRules(rule);
            Assert.Equal([$"{a.Rep}", $"{b.Rep}"], ContextGraph.Keys(child, g.Mark));
            child.CheckInvariants();

            using var sibling = g.Root.CreateChild();
            sibling.RunRules(rule);
            Assert.Empty(ContextGraph.Keys(sibling, g.Mark));
        }

        /// <summary>
        /// Siblings in which a constant lost unions to different ids each run it in their own canonical form, from one
        /// cached plan (its constant variants, one per vector of canonical constants), and neither disturbs the other
        /// or the root.
        /// </summary>
        [Fact]
        public void SiblingsRunTheirOwnCanonicalForms()
        {
            var g = new ContextGraph();
            var ids = Ids(g, 4);
            var (a, b, c, d) = (ids[0], ids[1], ids[2], ids[3]);
            var rb = g.EGraph.NewRule("edge-to-d", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Edge, [x, RuleEntry.Const(d, ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Set(g.Path, [x, RuleEntry.Const(d, ColumnTy.Id), RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();

            g.Assert(g.Root, g.Edge, a, b);
            g.Assert(g.Root, g.Edge, b, c);
            g.Root.FlushUpdates();
            g.Root.RunRules(rule);
            g.Root.Freeze();

            using var left = g.Root.CreateChild();
            using var right = g.Root.CreateChild();
            left.StageUnion(d, b);
            left.FlushUpdates();
            right.StageUnion(d, c);
            right.FlushUpdates();
            for (var i = 0; i < 2; i++)
            {
                left.RunRules(rule);
                right.RunRules(rule);
            }

            Assert.Equal([$"{a.Rep} {b.Rep}"], ContextGraph.Keys(left, g.Path));
            Assert.Equal([$"{b.Rep} {c.Rep}"], ContextGraph.Keys(right, g.Path));
            Assert.Empty(ContextGraph.Keys(g.Root, g.Path));
            left.CheckInvariants();
            right.CheckInvariants();
        }

        /// <summary>
        /// Random family A programs whose rules are rich in constants: the differential's checks, with every rule
        /// carrying id constants from the pool, in contexts that union pool ids.
        /// </summary>
        [Fact]
        public void ConstantRichProgramsAgreeWithTheReference()
        {
            var stats = new ColorRunStats();
            for (var seed = 0; seed < 60; seed++)
            {
                var program = ColorProgram.Generate(50_000 + seed, new ColorFamily(false, seed % 2 == 1), ColorDifferential.Steps);
                var random = new Random(seed);
                foreach (var rule in program.Rules)
                {
                    // Turn one occurrence of an id variable that the body binds elsewhere too into a pool constant.
                    var occurrences = rule.Atoms.SelectMany(a => a.Entries).Where(e => e >= 0).GroupBy(e => e).ToDictionary(g => g.Key, g => g.Count());
                    var atom = rule.Atoms[random.Next(rule.Atoms.Count)];
                    var columns = Enumerable.Range(0, atom.Entries.Length).Where(col => ColorSchema.IdColumns[atom.Table][col] && atom.Entries[col] >= 0 && occurrences[atom.Entries[col]] > 1).ToList();
                    if (columns.Count > 0)
                        atom.Entries[columns[random.Next(columns.Count)]] = ColorRule.Constant((ulong)random.Next(ColorProgram.PoolSize));
                }

                stats.Add(ColorDifferential.RunOrShrink(program, new ColorRunOptions()));
            }

            Assert.True(stats.ChildIterations >= 100, stats.ToString());
        }

    }

}
