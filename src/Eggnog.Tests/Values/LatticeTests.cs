using System;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Lattices (D25): the laws <see cref="ValueLattice{T}.Verify"/> checks, the built-ins, and merges that fail (D31).
    /// </summary>
    public sealed class LatticeTests
    {

        static readonly ValueSet<string>[] Sets =
        {
            ValueSet<string>.Empty,
            ValueSet.Of("a"),
            ValueSet.Of("b"),
            ValueSet.Of("a", "b"),
            ValueSet.Of("a", "b", "c"),
        };

        static readonly long[] Numbers = { -3, 0, 1, 7, long.MaxValue };

        [Fact]
        public void VerifyAcceptsTheBuiltIns()
        {
            ValueLattice.Intersect<string>().Verify(Sets);
            ValueLattice.Union<string>().Verify(Sets);
            ValueLattice.Max<long>().Verify(Numbers);
            ValueLattice.Min<long>().Verify(Numbers);
            Assert.Equal("intersect", ValueLattice.Intersect<string>().Name);
            Assert.Equal(typeof(ValueSet<string>), ValueLattice.Union<string>().ValueType);
            Assert.NotNull(ValueLattice.Max<long>().CaseJoin);
        }

        [Fact]
        public void VerifyRejectsAMismatchedCaseJoin()
        {
            var wrong = new ValueLattice<ValueSet<string>>("wrong", (a, b) => a.Intersect(b), (a, b) => a.Intersect(b));
            var e = Assert.Throws<InvalidOperationException>(() => wrong.Verify(Sets));
            Assert.Contains("absorption", e.Message);
            Assert.Contains("wrong", e.Message);
        }

        [Fact]
        public void VerifyRejectsAMergeThatIsNotALattice()
        {
            var first = new ValueLattice<long>("first", (a, b) => a);
            Assert.Contains("commutativity", Assert.Throws<InvalidOperationException>(() => first.Verify(Numbers)).Message);
            var sum = new ValueLattice<long>("sum", (a, b) => a + b);
            Assert.Contains("idempotence", Assert.Throws<InvalidOperationException>(() => sum.Verify(new long[] { 1, 2 })).Message);
        }

        [Fact]
        public void HostWritesMergeByTheLattice()
        {
            var graph = new EGraph();
            var lo = graph.DefineFunction<string, long>("lo", ValueLattice.Max<long>());
            var hi = graph.DefineFunction<string, long>("hi", ValueLattice.Min<long>());
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            graph.Root.Assume(lo.Of("n").Is(5L), hi.Of("n").Is(5L), dom.Of("k").Is(ValueSet.Of("a", "b")));
            graph.Root.Assume(lo.Of("n").Is(3L), lo.Of("n").Is(9L), hi.Of("n").Is(3L), dom.Of("k").Is(ValueSet.Of("b", "c")));
            Assert.Equal(9L, graph.Root.Get(lo.Of("n")));
            Assert.Equal(3L, graph.Root.Get(hi.Of("n")));
            Assert.Equal(ValueSet.Of("b"), graph.Root.Get(dom.Of("k")));
            Assert.Same(lo.Lattice, ((FunctionHandle)lo).Lattice);
        }

        [Fact]
        public void ALatticeThatThrowsDuringARulesMergeGivesARuleEvaluationExceptionWithoutARuleName()
        {
            var graph = new EGraph();
            var boom = new InvalidOperationException("boom");
            var f = graph.DefineFunction<string, long>("f", new ValueLattice<long>("picky", (a, b) => a == b ? a : throw boom));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("write two", r => r.Then(f.Of("k").Is(2L)));
            graph.Root.Assume(f.Of("k").Is(1L));

            var e = Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules));

            Assert.Null(e.RuleName);
            Assert.Null(e.Bindings);
            Assert.Same(boom, e.InnerException);
            Assert.Contains("picky", e.Message);
            Assert.True(graph.Root.IsFaulted);
            Assert.Same(e, graph.Root.Failure);
        }

        [Fact]
        public void ALatticeThatThrowsWhileAHostWriteIsCheckedWritesNothingAndDoesNotFault()
        {
            var graph = new EGraph();
            var f = graph.DefineFunction<string, long>("f", new ValueLattice<long>("picky", (a, b) => a == b ? a : throw new InvalidOperationException("boom")));
            graph.Root.Assume(f.Of("k").Is(1L));
            var version = graph.Root.Version;

            var e = Assert.Throws<RuleEvaluationException>(() => graph.Root.Assume(f.Of("k").Is(2L)));

            Assert.Null(e.RuleName);
            Assert.False(graph.Root.IsFaulted);
            Assert.Equal(1L, graph.Root.Get(f.Of("k")));
            Assert.Equal(version, graph.Root.Version);
        }

    }

}
