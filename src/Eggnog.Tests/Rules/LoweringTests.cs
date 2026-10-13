using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// The engine rule a program lowers to, read through the bridge's rule (<c>Relations.BridgeQuery</c>): atoms, steps
    /// in placement order, head operations, and the descriptions of the variants of rules, local rules and mirror copy
    /// rules.
    /// </summary>
    public sealed class LoweringTests
    {

        [Fact]
        public void AtomsFollowTheBodyAndRelationAtomsEndWithTheUnitConstant()
        {
            var t = new NodeTheory();
            var pair = t.Graph.DefineRelation<long, long>("pair");
            var f = t.Graph.DefineFunction<long, string>("f");
            var rule = t.Rules.DefineRule("join", r =>
            {
                var (x, y, s) = r.Vars<long, long, string>("x", "y", "s");
                r.When(pair.Of(x, y), s.Is(f.Of(x)), t.Num.Of(y));
                r.Then(t.Num.Of(x));
            });

            var query = QueryOf(t, rule);
            Assert.Equal(new[] { pair.Function, f.Function, t.Num.Function }.Select(t.Graph.Engine.GetTable), query.Atoms.Select(a => a.Table));
            var pairAtom = query.Atoms[0].Entries;
            Assert.True(pairAtom[0].IsVariable);
            Assert.True(pairAtom[1].IsVariable);
            Assert.False(pairAtom[2].IsVariable);
            Assert.Equal(t.Graph.Unit.Unit, pairAtom[2].Constant.Rep);
            Assert.Equal(t.Graph.Unit.EngineType, pairAtom[2].ConstantType);
            var functionAtom = query.Atoms[1].Entries;
            Assert.True(functionAtom[1].IsVariable);
        }

        [Fact]
        public void StepsAreQueryPrimsInPlacementOrder()
        {
            var t = new NodeTheory();
            var rule = t.Rules.DefineRule("steps", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.Where(Primitives.Less.Of(y, 10L));
                r.When(t.Num.Of(x), y.Is(Primitives.Add.Of(x, 1L)));
                r.When(x.IsNot(y));
                r.Then(t.Num.Of(y));
            });

            var program = rule.Compiled.Program;
            Assert.Equal(new[] { ProgramStepKind.Let, ProgramStepKind.Filter, ProgramStepKind.NotEqual }, program.Steps.Select(s => s.Kind));
            var variant = rule.Compiled.Variants.Default!;
            var actions = QueryOf(t, rule).Actions;
            Assert.Equal(Relations.RuleActionKind.QueryPrim, actions[0].Kind);
            Assert.Equal(variant.Owned[0], actions[0].Function);
            Assert.Equal(Relations.RuleActionKind.QueryPrim, actions[1].Kind);
            Assert.Equal(variant.Owned[1], actions[1].Function);
            Assert.Equal(Relations.RuleActionKind.QueryPrim, actions[2].Kind);
            Assert.Equal(t.Graph.NotEqual, actions[2].Function);
            Assert.Equal(Relations.RuleActionKind.Insert, actions[3].Kind);
        }

        [Fact]
        public void HeadOperationsFollowTheBuilderCalls()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f", ValueLattice.Max<long>());
            var rule = t.Rules.DefineRule("head", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
                var m = r.Make("m", t.Lit.Of(x));
                r.Then(t.Tag.Of(m), f.Of(x).Is(x), m.Is(t.A.Of()), t.Wrap.Of(m).Is(t.Lit.Of(x)));
            });

            // m.Is(A()) writes A's output (the union-id merge unions it with m); two constructor terms union.
            Assert.Equal(
                new[]
                {
                    HeadOpKind.Compute, HeadOpKind.Insert, HeadOpKind.GetOrAdd, HeadOpKind.Insert, HeadOpKind.Write, HeadOpKind.Write,
                    HeadOpKind.GetOrAdd, HeadOpKind.Union,
                },
                rule.Compiled.Program.Head.Select(h => h.Kind));
            var engine = t.Graph.Engine;
            var actions = QueryOf(t, rule).Actions;
            Assert.Equal(
                new[]
                {
                    Relations.RuleActionKind.CallExternal,
                    Relations.RuleActionKind.Insert,
                    Relations.RuleActionKind.LookupOrInsert,
                    Relations.RuleActionKind.Insert,
                    Relations.RuleActionKind.Insert,
                    Relations.RuleActionKind.Insert,
                    Relations.RuleActionKind.LookupOrInsert,
                    Relations.RuleActionKind.Union,
                },
                actions.Select(a => a.Kind));
            Assert.Equal(engine.GetTable(t.Num.Function), actions[1].Table);
            Assert.Equal(engine.GetTable(t.Lit.Function), actions[2].Table);
            Assert.Equal(engine.GetTable(t.Tag.Function), actions[3].Table);
            Assert.Equal(engine.GetTable(f.Function), actions[4].Table);
            Assert.Equal(engine.GetTable(t.A.Function), actions[5].Table);
            Assert.Equal(engine.GetTable(t.Wrap.Function), actions[6].Table);
        }

        [Fact]
        public void DescriptionsNameTheRulesetTheRuleAndItsSerial()
        {
            var t = new NodeTheory();
            var rule = t.Rules.DefineRule("named", r => r.Then(t.Num.Of(1L)));
            var variant = rule.Compiled.Variants.Default!;
            Assert.Equal($"rules/named#{rule.Compiled.Serial}", variant.Description);
            Assert.Same(variant, t.Graph.VariantsByDescription[variant.Description]);
            Assert.Equal(variant.Description, t.Graph.Engine.GetRuleInfo(variant.Id).Description);
            Assert.True(variant.SemiNaive);
            Assert.Same(rule, variant.Rule);

            var other = t.Rules.DefineRule("named", r => r.Then(t.Num.Of(2L)));
            Assert.NotEqual(variant.Description, other.Compiled.Variants.Default!.Description);
        }

        [Fact]
        public void TheNaiveTwinIsLoweredOnlyWhenSemiNaiveFalseIsFirstUsed()
        {
            var t = new NodeTheory();
            var rule = t.Rules.DefineRule("twin", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Tag.Of(t.Lit.Of(x)));
            });
            t.Root.Assume(t.Num.Of(1L));

            Assert.Single(rule.Compiled.Variants.All);
            t.Root.Run(t.Rules);
            Assert.Single(rule.Compiled.Variants.All);

            t.Root.Run(t.Rules, new RunOptions { SemiNaive = false });
            var variants = rule.Compiled.Variants.All.ToArray();
            Assert.Equal(2, variants.Length);
            var twin = variants.Single(v => v.SemiNaive == false);
            Assert.Equal($"rules/twin#{rule.Compiled.Serial}/naive", twin.Description);
            Assert.Same(twin, t.Graph.VariantsByDescription[twin.Description]);
        }

        [Fact]
        public void ANaiveRuleLowersOneNaiveVariant()
        {
            var t = new NodeTheory();
            var rule = t.Rules.DefineRule("naive", r =>
            {
                r.Naive();
                r.Then(t.Num.Of(1L));
            });

            Assert.True(rule.IsNaive);
            var variant = rule.Compiled.Variants.Default!;
            Assert.False(variant.SemiNaive);
            Assert.Equal($"rules/naive#{rule.Compiled.Serial}", variant.Description);
            t.Root.Run(t.Rules);
            t.Root.Run(t.Rules, new RunOptions { SemiNaive = false });
            Assert.Single(rule.Compiled.Variants.All);
        }

        /// <summary>
        /// Gets the bridge rule of a rule's default variant.
        /// </summary>
        static Relations.BridgeQuery QueryOf(NodeTheory t, Rule rule) => t.Graph.Engine.GetRuleInfo(rule.Compiled.Variants.Default!.Id).Query;

        /// <summary>
        /// A let whose target feeds a filter declared before it: steps are placed in rounds, so the filter runs once
        /// the let has bound its target.
        /// </summary>
        [Fact]
        public void AFilterOverALetDeclaredAfterItIsPlaced()
        {
            var t = new NodeTheory();
            var hit = t.Graph.DefineRelation<long>("hit");
            t.Root.Assume(t.Num.Of(4L), t.Num.Of(9L));
            t.Rules.DefineRule("plus one above five", r =>
            {
                var (n, m) = r.Vars<long, long>("n", "m");
                r.When(t.Num.Of(n));
                r.Where(Primitives.Greater.Of(m, 5L));
                r.When(m.Is(Primitives.Add.Of(n, 1L)));
                r.Then(hit.Of(m));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(hit.Of(10L)));
            Assert.False(t.Root.Check(hit.Of(5L)));
        }

        /// <summary>
        /// A local rule's variant is described <c>{scope path}:{ruleset}/{name}#{serial}</c> and a mirror copy rule's
        /// <c>$mirror/{mirror name}#{serial}</c>; copy rules are never listed in a run's report.
        /// </summary>
        [Fact]
        public void LocalAndCopyRulesAreDescribedAsSpecified()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var outer = t.Root.CreateChild(t.Outer);
            var local = outer.DefineRule(t.Rules, "local", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            Assert.Equal($"{outer.Path}:rules/local#{local.Compiled.Serial}", local.Compiled.Variants.Default!.Description);

            var mirror = t.Graph.Mirrors.Get(t.Fact, t.Outer);
            var copy = mirror.CopyRule;
            Assert.Equal($"$mirror/fact@outer#{copy.Serial}", copy.Variants.Default!.Description);

            outer.Assume(t.Fact.Of("a"));
            var report = outer.Run(Schedule.Saturate(t.Rules));
            Assert.True(outer.Check(copies.Facts.Of("a"), t.Seen.Of("a")));
            Assert.DoesNotContain(copy.Rule, report.Rules.Keys);
            Assert.DoesNotContain(report.Rules.Keys, r => r.Ruleset.Name == "$mirror");
            Assert.Contains(local, report.Rules.Keys);
        }

    }

}
