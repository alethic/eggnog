using System;
using System.Globalization;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Lattices: the laws <see cref="ValueLattice{T}.Verify"/> checks, the built-ins, merges that fail, at the root and
    /// in children, and a user lattice over a registered struct with a case join (<see cref="NumericBoundTheory"/>), the
    /// contract a consumer's numeric bounds rely on.
    /// </summary>
    public sealed class LatticeTests
    {

        static readonly Set<string>[] Sets =
        {
            Set<string>.Empty,
            Set.Of("a"),
            Set.Of("b"),
            Set.Of("a", "b"),
            Set.Of("a", "b", "c"),
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
            Assert.Equal(typeof(Set<string>), ValueLattice.Union<string>().ValueType);
            Assert.NotNull(ValueLattice.Max<long>().CaseJoin);
        }

        [Fact]
        public void VerifyRejectsAMismatchedCaseJoin()
        {
            var wrong = new ValueLattice<Set<string>>("wrong", (a, b) => a.Intersect(b), (a, b) => a.Intersect(b));
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

            // Rock, paper, scissors: each pair has a winner, so the merge is commutative and idempotent, but
            // Merge(Merge(0, 1), 2) is 2 and Merge(0, Merge(1, 2)) is 0.
            var beats = new ValueLattice<long>("beats", (a, b) => a == b || (a - b + 3) % 3 == 1 ? a : b);
            Assert.Contains("associativity", Assert.Throws<InvalidOperationException>(() => beats.Verify(new long[] { 0, 1, 2 })).Message);
        }

        [Fact]
        public void HostWritesMergeByTheLattice()
        {
            var graph = new EGraph();
            var lo = graph.DefineFunction<string, long>("lo", ValueLattice.Max<long>());
            var hi = graph.DefineFunction<string, long>("hi", ValueLattice.Min<long>());
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            graph.Root.Assume(lo.Of("n").Is(5L), hi.Of("n").Is(5L), dom.Of("k").Is(Set.Of("a", "b")));
            graph.Root.Assume(lo.Of("n").Is(3L), lo.Of("n").Is(9L), hi.Of("n").Is(3L), dom.Of("k").Is(Set.Of("b", "c")));
            Assert.Equal(9L, graph.Root.Get(lo.Of("n")));
            Assert.Equal(3L, graph.Root.Get(hi.Of("n")));
            Assert.Equal(Set.Of("b"), graph.Root.Get(dom.Of("k")));
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

        /// <summary>
        /// Bounds that cover every case <see cref="NumericBoundTheory"/>'s lattices decide: ties of value with different
        /// <see cref="NumericBound.Open"/>, values that differ only in scale, and the extremes.
        /// </summary>
        static readonly NumericBound[] Bounds =
        {
            new(1.0m, false),
            new(1.0m, true),
            new(1.00m, false),
            new(-2.5m, true),
            new(0m, false),
            new(7m, true),
            new(7m, false),
            new(decimal.MaxValue, false),
            new(decimal.MinValue, true),
        };

        /// <summary>
        /// <see cref="ValueLattice{T}.Verify"/> accepts both bound lattices, case joins included, on samples with ties of
        /// value and of scale, and rejects a lower-bound merge whose tie rule keeps its first argument: on a tie of value
        /// with different <see cref="NumericBound.Open"/> it is not commutative.
        /// </summary>
        [Fact]
        public void VerifyAcceptsTheBoundLatticesAndRejectsATieRuleThatKeepsItsFirstArgument()
        {
            NumericBoundTheory.LowerLattice().Verify(Bounds);
            NumericBoundTheory.UpperLattice().Verify(Bounds);
            Assert.NotNull(NumericBoundTheory.LowerLattice().CaseJoin);
            Assert.NotNull(NumericBoundTheory.UpperLattice().CaseJoin);

            var firstOnTie = new ValueLattice<NumericBound>("first-on-tie", (a, b) => a.Value >= b.Value ? a : b, NumericBoundTheory.LowerLattice().CaseJoin);
            var e = Assert.Throws<InvalidOperationException>(() => firstOnTie.Verify(Bounds));
            Assert.Contains("commutativity", e.Message);
            Assert.Contains("first-on-tie", e.Message);
        }

        /// <summary>
        /// Rules write bounds through the lattices at the root: <c>lo</c> keeps the greater bound and <c>hi</c> the lesser,
        /// and on a tie of value the open bound wins in both.
        /// </summary>
        [Fact]
        public void RulesMergeBoundsAtTheRootAndAnOpenBoundWinsATie()
        {
            var t = new NumericBoundTheory();
            var root = t.Graph.Root;
            root.Assume(t.Ge.Of("x", 1m), t.Gt.Of("x", 3m), t.Ge.Of("x", 3m), t.Le.Of("x", 10m), t.Lt.Of("x", 10m), t.Le.Of("x", 12m));
            root.Assume(t.Ge.Of("y", 5m), t.Le.Of("y", 5m));

            var report = root.Run(Schedule.Saturate(t.Bounds));

            Assert.True(report.Saturated);
            Assert.Equal(new NumericBound(3m, true), root.Get(t.Lo.Of("x")));
            Assert.Equal(new NumericBound(10m, true), root.Get(t.Hi.Of("x")));
            Assert.Equal(new NumericBound(5m, false), root.Get(t.Lo.Of("y")));
            Assert.Equal(new NumericBound(5m, false), root.Get(t.Hi.Of("y")));
            Assert.Equal(2L, root.CountRows(t.Lo));
            Assert.Equal("(lo \"x\") = (open 3)", root.Rows(t.Lo).Single(row => (string)row.Keys[0] == "x").ToString());
        }

        /// <summary>
        /// <c>1.0m</c> and <c>1.00m</c> are equal, so bounds over them are one value: a write of the second merges into
        /// the first and changes nothing, and a read returns the form the graph interned first, for every row that holds
        /// the value, whether a host write or a rule gave it. Another graph that sees the other form first returns that
        /// one.
        /// </summary>
        [Fact]
        public void BoundsThatDifferOnlyInScaleAreOneValueInTheFormInternedFirst()
        {
            var t = new NumericBoundTheory();
            var root = t.Graph.Root;
            root.Assume(t.Lo.Of("x").Is(new NumericBound(1.0m, false)));
            var version = root.Version;

            root.Assume(t.Lo.Of("x").Is(new NumericBound(1.00m, false)));
            Assert.Equal(version, root.Version);
            Assert.Equal(1L, root.CountRows(t.Lo));
            Assert.Equal(1, root.Get(t.Lo.Of("x")).Value.Scale);

            // Another row, and a rule's output: the same value, read in the first form.
            root.Assume(t.Lo.Of("y").Is(new NumericBound(1.00m, false)), t.Ge.Of("z", 1.000m));
            root.Run(Schedule.Saturate(t.Bounds));
            Assert.Equal("1.0", root.Get(t.Lo.Of("y")).Value.ToString(CultureInfo.InvariantCulture));
            Assert.Equal("1.0", root.Get(t.Lo.Of("z")).Value.ToString(CultureInfo.InvariantCulture));

            var other = new NumericBoundTheory();
            other.Graph.Root.Assume(other.Lo.Of("x").Is(new NumericBound(1.00m, false)), other.Lo.Of("y").Is(new NumericBound(1.0m, false)));
            Assert.Equal(2, other.Graph.Root.Get(other.Lo.Of("y")).Value.Scale);
        }

        /// <summary>
        /// Host writes merge through the bound lattices as rules' writes do, and a looser bound than the one held
        /// writes nothing.
        /// </summary>
        [Fact]
        public void HostWritesMergeBoundsByTheLattices()
        {
            var t = new NumericBoundTheory();
            var root = t.Graph.Root;
            root.Assume(t.Lo.Of("x").Is(new NumericBound(2m, false)), t.Hi.Of("x").Is(new NumericBound(12m, true)));
            root.Assume(t.Lo.Of("x").Is(new NumericBound(5m, false)), t.Lo.Of("x").Is(new NumericBound(3m, true)), t.Hi.Of("x").Is(new NumericBound(9m, false)));
            root.Assume(t.Lo.Of("x").Is(new NumericBound(5m, true)), t.Hi.Of("x").Is(new NumericBound(9m, true)));
            var version = root.Version;

            root.Assume(t.Lo.Of("x").Is(new NumericBound(5m, false)), t.Hi.Of("x").Is(new NumericBound(9.5m, true)));

            Assert.Equal(new NumericBound(5m, true), root.Get(t.Lo.Of("x")));
            Assert.Equal(new NumericBound(9m, true), root.Get(t.Hi.Of("x")));
            Assert.Equal(version, root.Version);
        }

        /// <summary>
        /// A lift joins each bound by its lattice's case join, the looser of the cases' bounds (a closed bound winning a
        /// tie of value), both from a host-driven <see cref="CaseSet"/> and from a split step's cases.
        /// </summary>
        [Fact]
        public void LiftsJoinCasesToTheLooserBound()
        {
            var t = new NumericBoundTheory();
            var root = t.Graph.Root;
            using (var cases = root.OpenCases())
            {
                cases.Add().Assume(t.Lo.Of("x").Is(new NumericBound(3m, true)), t.Hi.Of("x").Is(new NumericBound(10m, false)));
                cases.Add().Assume(t.Lo.Of("x").Is(new NumericBound(3m, false)), t.Hi.Of("x").Is(new NumericBound(12m, true)));
                var lifted = cases.Lift();
                Assert.Equal(2, lifted.Values);
                Assert.Equal(2, lifted.LiveCases);
            }

            Assert.Equal(new NumericBound(3m, false), root.Get(t.Lo.Of("x")));
            Assert.Equal(new NumericBound(12m, true), root.Get(t.Hi.Of("x")));

            // A split on kind: each case proves its own bounds of y from comparisons, and the lift keeps the looser.
            var s = new NumericBoundTheory();
            var dom = s.Graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            var discriminator = s.Graph.DefineRelation<string>("discriminator");
            var split = s.Graph.DefineDomainSplit("kind", dom, [discriminator]);
            var branches = s.Graph.DefineRuleset("branches");
            branches.DefineRule("kind a", r => { r.When(dom.Of("kind").Is(Set.Of("a"))); r.Then(s.Ge.Of("y", 1m), s.Lt.Of("y", 5m)); });
            branches.DefineRule("kind b", r => { r.When(dom.Of("kind").Is(Set.Of("b"))); r.Then(s.Gt.Of("y", 1m), s.Le.Of("y", 8m)); });
            var scope = s.Graph.Root;
            scope.Assume(discriminator.Of("kind"), dom.Of("kind").Is(Set.Of("a", "b")));

            var report = scope.Run(Schedule.Split(split, Schedule.Saturate(branches, s.Bounds)));

            Assert.Equal(2, report.CasesOpened);
            Assert.Equal(new NumericBound(1m, false), scope.Get(s.Lo.Of("y")));
            Assert.Equal(new NumericBound(8m, false), scope.Get(s.Hi.Of("y")));
            Assert.False(scope.Check(s.Ge.Of("y", 1m)));                   // only the bounds are common to both cases
        }

        /// <summary>
        /// Children of a frozen scope merge bounds independently and concurrently: each child narrows the frozen scope's
        /// bounds by its own comparisons, an open bound winning the tie at the frozen scope's value, and the frozen
        /// scope keeps its own.
        /// </summary>
        [Fact]
        public void ChildrenOfAFrozenScopeMergeBoundsIndependentlyAndConcurrently()
        {
            const int children = 64;
            var t = new NumericBoundTheory();
            var root = t.Graph.Root;
            root.Assume(t.Ge.Of("x", 0m), t.Le.Of("x", 1000m));
            root.Run(Schedule.Saturate(t.Bounds));
            root.Freeze();

            var lo = new NumericBound[children];
            var hi = new NumericBound[children];
            var threads = DedicatedThreads.For(8, children, i =>
            {
                using var child = root.CreateChild();
                child.Assume(t.Gt.Of("x", (decimal)i), t.Le.Of("x", 100m + i), t.Lt.Of("x", 1000m));
                child.Run(Schedule.Saturate(t.Bounds));
                lo[i] = child.Get(t.Lo.Of("x"));
                hi[i] = child.Get(t.Hi.Of("x"));
            });

            Assert.Equal(8, threads);
            for (var i = 0; i < children; i++)
            {
                Assert.Equal(new NumericBound(i, true), lo[i]);
                Assert.Equal(new NumericBound(100m + i, false), hi[i]);
            }

            Assert.Equal(new NumericBound(0m, false), root.Get(t.Lo.Of("x")));
            Assert.Equal(new NumericBound(1000m, false), root.Get(t.Hi.Of("x")));
            Assert.Empty(root.Children);
        }

        /// <summary>
        /// A lattice over the registered struct that throws during a rule's merge in a child is reported there, with the
        /// delegate's exception as the cause, as for a lattice over <c>long</c>: the child is faulted, and its frozen
        /// parent is not.
        /// </summary>
        [Fact]
        public void ABoundLatticeThatThrowsInAChildsRunKeepsItsCause()
        {
            var graph = new EGraph();
            graph.Values.Register<NumericBound>("NumericBound", format: NumericBoundTheory.Format);
            var boom = new InvalidOperationException("boom");
            var f = graph.DefineFunction<string, NumericBound>("f", new ValueLattice<NumericBound>("picky-bound", (a, b) => a == b ? a : throw boom));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("write an open bound", r => r.Then(f.Of("k").Is(new NumericBound(2m, true))));
            graph.Root.Assume(f.Of("k").Is(new NumericBound(2m, false)));
            graph.Root.Freeze();
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<RuleEvaluationException>(() => child.Run(rules));

            Assert.Same(boom, e.InnerException);
            Assert.Contains("picky-bound", e.Message);
            Assert.True(child.IsFaulted);
            Assert.False(graph.Root.IsFaulted);
            Assert.Equal(new NumericBound(2m, false), graph.Root.Get(f.Of("k")));
        }

        /// <summary>
        /// Every distinct value a scope encodes is interned in the graph's pools, which never shrink: a host write's
        /// constants and each rule output. These lattices' merges and case joins return one of their arguments, so
        /// their results add nothing. A child's values stay interned once it is disposed, and a later child that
        /// writes the same values adds nothing.
        /// </summary>
        [Fact]
        public void EveryDistinctBoundIsInternedAndOutlivesTheChildThatMadeIt()
        {
            var t = new NumericBoundTheory();
            var root = t.Graph.Root;
            root.Freeze();
            var before = t.Graph.Values.InternedCount;

            using (var child = root.CreateChild())
            {
                // The write's two constants, the string x and the decimal 3.
                child.Assume(t.Gt.Of("x", 3m));
                Assert.Equal(before + 2, t.Graph.Values.InternedCount);

                // The rule's output, (open 3).
                child.Run(Schedule.Saturate(t.Bounds));
                Assert.Equal(before + 3, t.Graph.Values.InternedCount);

                // The constants are interned already; the rule's output (closed 3) is new, and merges into (open 3).
                child.Assume(t.Ge.Of("x", 3m));
                child.Run(Schedule.Saturate(t.Bounds));
                Assert.Equal(new NumericBound(3m, true), child.Get(t.Lo.Of("x")));
                Assert.Equal(before + 4, t.Graph.Values.InternedCount);
            }

            Assert.Equal(before + 4, t.Graph.Values.InternedCount);
            using (var again = root.CreateChild())
            {
                again.Assume(t.Gt.Of("x", 3m), t.Ge.Of("x", 3m));
                again.Run(Schedule.Saturate(t.Bounds));
                Assert.Equal(new NumericBound(3m, true), again.Get(t.Lo.Of("x")));
            }

            Assert.Equal(before + 4, t.Graph.Values.InternedCount);
        }

    }

}
