using System;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Lattices: the laws <see cref="ValueLattice{T}.Verify"/> checks, the built-ins, and merges that fail, at the root
    /// and in children.
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

        /// <summary>
        /// A lattice that throws during a rule's merge in a child is recorded in the child's runtime, so the child's
        /// exception carries it, as at the root; the child is faulted, and the root is not.
        /// </summary>
        /// <param name="frozenRoot">Whether the root is frozen before the child is made.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ALatticeThatThrowsDuringAChildsMergeIsReportedThere(bool frozenRoot)
        {
            var graph = new EGraph();
            var boom = new InvalidOperationException("boom");
            var f = graph.DefineFunction<string, long>("f", new ValueLattice<long>("picky", (a, b) => a == b ? a : throw boom));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("write two", r => r.Then(f.Of("k").Is(2L)));
            if (frozenRoot)
                graph.Root.Freeze();

            var child = graph.Root.CreateChild();
            child.Assume(f.Of("k").Is(1L));

            var e = Assert.Throws<RuleEvaluationException>(() => child.Run(rules));

            Assert.Null(e.RuleName);
            Assert.Same(boom, e.InnerException);
            Assert.Contains("picky", e.Message);
            Assert.True(child.IsFaulted);
            Assert.False(graph.Root.IsFaulted);
        }

        /// <summary>
        /// A lattice that throws during a rule's merge in a child's run gives a <see cref="RuleEvaluationException"/>
        /// whose inner exception is the delegate's: the failure is recorded on the child's runtime, and the child is
        /// faulted.
        /// </summary>
        [Fact]
        public void ALatticeThatThrowsInAChildsRunKeepsItsCause()
        {
            var graph = new EGraph();
            var boom = new InvalidOperationException("boom");
            var f = graph.DefineFunction<string, long>("f", new ValueLattice<long>("picky", (a, b) => a == b ? a : throw boom));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("write two", r => r.Then(f.Of("k").Is(2L)));
            graph.Root.Assume(f.Of("k").Is(1L));
            graph.Root.Freeze();
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<RuleEvaluationException>(() => child.Run(rules));

            Assert.Same(boom, e.InnerException);
            Assert.Contains("picky", e.Message);
            Assert.True(child.IsFaulted);
            Assert.False(graph.Root.IsFaulted);
        }

        /// <summary>
        /// The same for a host write into a child: a lattice that throws while the write is checked gives the
        /// delegate's exception inside a <see cref="RuleEvaluationException"/>, and writes nothing.
        /// </summary>
        [Fact]
        public void ALatticeThatThrowsInAChildsHostWriteKeepsItsCause()
        {
            var graph = new EGraph();
            var boom = new InvalidOperationException("boom");
            var f = graph.DefineFunction<string, long>("f", new ValueLattice<long>("picky", (a, b) => a == b ? a : throw boom));
            graph.Root.Assume(f.Of("k").Is(1L));
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<RuleEvaluationException>(() => child.Assume(f.Of("k").Is(2L)));

            Assert.Same(boom, e.InnerException);
            Assert.Contains("picky", e.Message);
            Assert.Equal(1L, child.Get(f.Of("k")));
        }

    }

}
