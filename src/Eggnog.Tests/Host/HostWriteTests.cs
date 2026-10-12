using System;
using System.Collections.Generic;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// Host write batches (D30, D32, §6.6.1, §6.6.2): atomic, canonical, and silent when nothing changes.
    /// </summary>
    public sealed class HostWriteTests
    {

        [Fact]
        public void ARejectedFactWritesNothing()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            t.Root.Assume(f.Of("k").Is(1L));
            var version = t.Root.Version;

            Assert.Throws<ArgumentException>(() => t.Root.Assume(t.Num.Of(1L), ((Term<long>)1L).IsNot(2L)));
            Assert.Throws<FactConflictException>(() => t.Root.Assume(t.Num.Of(2L), t.Tag.Of(t.A.Of()), f.Of("k").Is(5L)));

            Assert.False(t.Root.Check(t.Num.Of(1L)));
            Assert.False(t.Root.Check(t.Num.Of(2L)));
            Assert.False(t.Root.Check(Fact.Exists(t.A.Of())));
            Assert.Equal(version, t.Root.Version);
        }

        [Fact]
        public void MakeReturnsCanonicalClasses()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Union(a, b);

            var again = t.Root.Make(t.B.Of());
            Assert.Equal(t.Root.Find(b).Id, again.Id);
            Assert.Equal(a.Id, again.Id);
            var wrapped = t.Root.Make(t.Wrap.Of(b));
            Assert.True(t.Root.AreEqual(wrapped, t.Wrap.Of(a)));
            Assert.Equal(t.Root.Find(wrapped).Id, wrapped.Id);
        }

        [Fact]
        public void ABatchMintsEachNewTermOnce()
        {
            var t = new NodeTheory();
            var first = default(Node);
            var second = default(Node);
            t.Root.Write(w =>
            {
                first = w.Make(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(1L)));
                second = w.Make(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(1L)));
            });

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(1, t.Graph.Engine.TableSize(t.Lit.Function));
            Assert.Equal(1, t.Graph.Engine.TableSize(t.Add.Function));
            Assert.True(t.Root.AreEqual(first, t.Add.Of(t.Lit.Of(1L), t.Lit.Of(1L))));
        }

        [Fact]
        public void WriteMakesSeveralCallsOneBatchWithOneFlush()
        {
            var t = new NodeTheory();
            var clock = t.Graph.Engine.NextTimestamp;
            t.Root.Assume(t.Num.Of(1L));
            t.Root.Assume(t.Num.Of(2L));
            Assert.Equal(clock + 2, t.Graph.Engine.NextTimestamp);

            clock = t.Graph.Engine.NextTimestamp;
            var version = t.Root.Version;
            t.Root.Write(w =>
            {
                w.Assume(t.Num.Of(3L));
                var lit = w.Make(t.Lit.Of(3L));
                w.Assume(t.Tag.Of(lit));
                Assert.Same(t.Root, w.Scope);
            });

            Assert.Equal(clock + 1, t.Graph.Engine.NextTimestamp);
            Assert.NotEqual(version, t.Root.Version);
            Assert.True(t.Root.Check(t.Num.Of(3L), t.Tag.Of(t.Lit.Of(3L))));
        }

        [Fact]
        public void AThrowInsideWriteWritesNothing()
        {
            var t = new NodeTheory();
            var version = t.Root.Version;
            ScopeWriter? kept = null;
            Assert.Throws<InvalidOperationException>(() => t.Root.Write(w =>
            {
                kept = w;
                w.Assume(t.Num.Of(1L));
                w.Make(t.A.Of());
                throw new InvalidOperationException("abandon");
            }));

            Assert.False(t.Root.Check(t.Num.Of(1L)));
            Assert.False(t.Root.Check(Fact.Exists(t.A.Of())));
            Assert.Equal(version, t.Root.Version);
            Assert.Throws<ObjectDisposedException>(() => kept!.Assume(t.Num.Of(2L)));
        }

        [Fact]
        public void TheScopeRefusesItsOwnCallsWhileABatchIsOpen()
        {
            var t = new NodeTheory();
            t.Root.Write(w =>
            {
                Assert.Throws<InvalidOperationException>(() => t.Root.Assume(t.Num.Of(1L)));
                Assert.Throws<InvalidOperationException>(() => t.Root.Check(t.Num.Of(1L)));
                w.Assume(t.Num.Of(2L));
            });
            Assert.True(t.Root.Check(t.Num.Of(2L)));
            Assert.False(t.Root.Check(t.Num.Of(1L)));
        }

        [Fact]
        public void AConflictWithTheStoredValueIsRefused()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            t.Root.Assume(f.Of("k").Is(1L));

            var e = Assert.Throws<FactConflictException>(() => t.Root.Assume(f.Of("k").Is(2L)));

            Assert.Same(f, e.Table);
            Assert.Equal(new object[] { "k" }, e.Key);
            Assert.Equal(1L, e.StoredValue);
            Assert.Equal(2L, e.NewValue);
            Assert.IsAssignableFrom<InvalidOperationException>(e);
            Assert.Equal(1L, t.Root.Get(f.Of("k")));
            t.Root.Assume(f.Of("k").Is(1L));
        }

        [Fact]
        public void AConflictWithinABatchIsRefused()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");

            var e = Assert.Throws<FactConflictException>(() => t.Root.Assume(f.Of("j").Is(1L), f.Of("j").Is(1L), f.Of("j").Is(2L)));

            Assert.Equal(new object[] { "j" }, e.Key);
            Assert.Equal(1L, e.StoredValue);
            Assert.Equal(2L, e.NewValue);
            Assert.False(t.Root.TryGet(f.Of("j"), out _));
        }

        [Fact]
        public void AConflictReportsClassKeys()
        {
            var t = new NodeTheory();
            var size = t.Graph.DefineFunction<Node, long>("size");
            var a = t.Root.Make(t.A.Of());
            t.Root.Assume(size.Of(a).Is(1L));
            var e = Assert.Throws<FactConflictException>(() => t.Root.Assume(size.Of(t.A.Of()).Is(2L)));
            Assert.Equal(a, Assert.IsType<Node>(e.Key[0]));
        }

        [Fact]
        public void AUnionThatMergesConflictingKeysThroughCongruenceIsAConflict()
        {
            var t = new NodeTheory();
            var size = t.Graph.DefineFunction<Node, long>("size");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(size.Of(t.Wrap.Of(a)).Is(1L), size.Of(t.Wrap.Of(b)).Is(2L));
            var version = t.Root.Version;

            // a = b makes (Wrap a) = (Wrap b), which gives size two values for one key.
            var e = Assert.Throws<FactConflictException>(() => t.Root.Union(a, b));

            Assert.Same(size, e.Table);
            Assert.Equal(new HashSet<object> { 1L, 2L }, new HashSet<object> { e.StoredValue, e.NewValue });
            Assert.False(t.Root.IsFaulted);
            Assert.False(t.Root.AreEqual(a, b));
            Assert.Equal(version, t.Root.Version);
            Assert.Throws<FactConflictException>(() => t.Root.Assume(((Term<Node>)a).Is(b)));
            Assert.Throws<FactConflictException>(() => t.Root.Assume(t.B.Of().Is(a)));
            Assert.False(t.Root.AreEqual(a, b));
        }

        [Fact]
        public void AUnionThatMergesKeysHoldingOneValueIsAccepted()
        {
            var t = new NodeTheory();
            var size = t.Graph.DefineFunction<Node, long>("size");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(size.Of(t.Wrap.Of(a)).Is(1L), size.Of(t.Wrap.Of(b)).Is(1L), size.Of(a).Is(3L));

            Assert.True(t.Root.Union(a, b));

            Assert.True(t.Root.AreEqual(t.Root.Make(t.Wrap.Of(a)), t.Wrap.Of(b)));
            Assert.Equal(1L, t.Root.Get(size.Of(t.Wrap.Of(b))));
            Assert.Equal(3L, t.Root.Get(size.Of(b)));
        }

        [Fact]
        public void AWriteAfterAUnionOfItsBatchIsCheckedAgainstTheMergedKey()
        {
            var t = new NodeTheory();
            var size = t.Graph.DefineFunction<Node, long>("size");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(size.Of(a).Is(1L));

            Assert.Throws<FactConflictException>(() => t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(size.Of(b).Is(2L));
            }));
            Assert.False(t.Root.AreEqual(a, b));
            Assert.False(t.Root.TryGet(size.Of(b), out _));

            // The same value is no conflict: the batch commits.
            t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(size.Of(b).Is(1L));
            });
            Assert.True(t.Root.AreEqual(a, b));
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public void ARejectedCallRejectsItsWholeBatch()
        {
            var t = new NodeTheory();
            var size = t.Graph.DefineFunction<Node, long>("size");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(size.Of(a).Is(1L), size.Of(b).Is(2L));
            var version = t.Root.Version;
            var made = default(Node);

            t.Root.Write(w =>
            {
                made = w.Make(t.Wrap.Of(a));
                w.Assume(t.Num.Of(1L));
                Assert.Throws<FactConflictException>(() => w.Union(a, b));
                w.Assume(t.Num.Of(2L));
            });

            Assert.Equal(version, t.Root.Version);
            Assert.False(t.Root.Check(t.Num.Of(1L)));
            Assert.False(t.Root.Check(t.Num.Of(2L)));
            Assert.False(t.Root.Check(Fact.Exists(t.Wrap.Of(a))));
            Assert.Contains("did not commit", Assert.Throws<ArgumentException>(() => t.Root.Find(made)).Message);
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public void AWriterUsedOnAnotherThreadIsRefusedAndRejectsTheBatch()
        {
            var t = new NodeTheory();
            Exception? refused = null;
            t.Root.Write(w =>
            {
                w.Assume(t.Num.Of(1L));
                var thread = new System.Threading.Thread(() => refused = Record.Exception(() => w.Assume(t.Num.Of(2L))));
                thread.Start();
                thread.Join();
            });

            Assert.Contains("in use on another thread", Assert.IsType<InvalidOperationException>(refused).Message);
            Assert.False(t.Root.Check(t.Num.Of(1L)));
            Assert.False(t.Root.Check(t.Num.Of(2L)));
        }

        [Fact]
        public void UnionReturnsWhetherTheClassesWereDistinct()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            Assert.True(t.Root.Union(a, b));
            Assert.False(t.Root.Union(b, a));
            Assert.False(t.Root.Union(a, a));
            Assert.True(t.Root.AreEqual(a, b));
        }

        [Fact]
        public void AClassThatLostAUnionLandsCanonicalInARow()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Union(a, b);
            Assert.Equal(a.Id, t.Root.Find(b).Id);

            t.Root.Assume(t.Tag.Of(b), t.Lit.Of(5L).Is(b));

            var cells = new List<uint>();
            t.Graph.Engine.ForEach(t.Tag.Function, row => cells.Add(row.Values[0].Rep));
            Assert.Equal(new[] { a.Id.Cell }, cells);
            Assert.True(t.Root.Check(t.Tag.Of(a)));
            Assert.Equal(a.Id, t.Root.Get(t.Lit.Of(5L)).Id);
        }

        [Fact]
        public void AConstructorOutputWriteCreatesOrUnions()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(t.Lit.Of(1L).Is(a));
            Assert.Equal(a.Id, t.Root.Get(t.Lit.Of(1L)).Id);
            Assert.Equal(1, t.Graph.Engine.TableSize(t.Lit.Function));

            t.Root.Assume(t.Lit.Of(1L).Is(b));
            Assert.True(t.Root.AreEqual(a, b));

            var c = default(Node);
            t.Root.Write(w =>
            {
                w.Assume(t.Lit.Of(2L).Is(a));
                c = w.Make(t.Lit.Of(2L));
            });
            Assert.True(t.Root.AreEqual(c, a));
        }

        [Fact]
        public void FactsThatHoldAlreadyChangeNothing()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            var best = t.Graph.DefineFunction<string, long>("best", ValueLattice.Max<long>());
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Union(a, b);
            t.Root.Assume(t.Num.Of(1L), f.Of("k").Is(1L), best.Of("k").Is(5L), t.Lit.Of(1L).Is(a));
            var version = t.Root.Version;
            var clock = t.Graph.Engine.NextTimestamp;
            var extractor = t.Root.CreateExtractor();

            t.Root.Assume(t.Num.Of(1L), f.Of("k").Is(1L), best.Of("k").Is(3L), t.Lit.Of(1L).Is(b), t.A.Of().Is(t.B.Of()));
            t.Root.Make(t.Lit.Of(1L));
            Assert.False(t.Root.Union(a, b));
            t.Root.Write(w => w.Assume(t.Num.Of(1L)));

            Assert.Equal(version, t.Root.Version);
            Assert.Equal(clock, t.Graph.Engine.NextTimestamp);
            Assert.Equal("(A)", extractor.Extract(a).ToString());
            Assert.Equal(5L, t.Root.Get(best.Of("k")));
        }

        [Fact]
        public void AChangingWriteMovesTheVersion()
        {
            var t = new NodeTheory();
            var before = t.Root.Version;
            t.Root.Assume(t.Num.Of(1L));
            var after = t.Root.Version;
            Assert.NotEqual(before, after);
            t.Root.Make(t.A.Of());
            Assert.NotEqual(after, t.Root.Version);
        }

        [Fact]
        public void InvalidWritesAreRefused()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            var g = t.Graph.DefineFunction<string, long>("g");
            var byLength = t.Graph.DefineConstructor<long, Node>("ByLength");

            Assert.Equal("value equality is not a write: 1 = 2", Assert.Throws<ArgumentException>(() => t.Root.Assume(((Term<long>)1L).Is(2L))).Message.Split(" (Parameter")[0]);
            Assert.Throws<ArgumentException>(() => t.Root.Assume(f.Of("k").Is(g.Of("k"))));
            Assert.Throws<ArgumentException>(() => t.Root.Assume(Fact.Exists(t.A.Of())));
            Assert.Throws<ArgumentException>(() => t.Root.Assume(Fact.True(true)));
            Assert.Contains("host terms do not read; Get 'f' first", Assert.Throws<ArgumentException>(() => t.Root.Make(byLength.Of(f.Of("k")))).Message);
            Assert.Throws<ArgumentException>(() => t.Root.Make(byLength.Of(Primitives.Add.Of(1L, 2L))));
            var other = new NodeTheory();
            Assert.Throws<ArgumentException>(() => t.Root.Assume(other.Num.Of(1L)));
            Assert.False(t.Root.IsFaulted);
        }

    }

}
