using System;
using System.Collections.Generic;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// Host write batches: egglog's commands one after the other (a fact that fails keeps the terms it made, but not its
    /// write, and the others stand;
    /// a batch rejected for a call from another thread writes nothing more, and the classes its <c>Make</c> calls
    /// returned are invalid), canonical, checked against functions without a lattice, confined to the thread that holds
    /// the scope's guard, and silent when nothing changes.
    /// </summary>
    public sealed class HostWriteTests
    {

        [Fact]
        public void ARejectedFactWritesNothingAndTheFactsBeforeItStand()
        {
            // egglog's commands: (num 1) stands though the next one fails, and so do (num 2) and (tag (A)).
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            t.Root.Assume(f.Of("k").Is(1L));
            var version = t.Root.Version;

            Assert.Throws<ArgumentException>(() => t.Root.Assume(t.Num.Of(1L), ((Term<long>)1L).IsNot(2L)));
            Assert.Throws<FactConflictException>(() => t.Root.Assume(t.Num.Of(2L), t.Tag.Of(t.A.Of()), f.Of("k").Is(5L)));

            Assert.True(t.Root.Check(t.Num.Of(1L)));
            Assert.True(t.Root.Check(t.Num.Of(2L)));
            Assert.True(t.Root.Check(t.Tag.Of(t.A.Of())));
            Assert.Equal(1L, t.Root.Get(f.Of("k")));
            Assert.NotEqual(version, t.Root.Version);
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
        public void AThrowInsideWritePropagatesOnceTheCallsBeforeItAreWritten()
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

            Assert.True(t.Root.Check(t.Num.Of(1L)));
            Assert.True(t.Root.Check(Fact.Exists(t.A.Of())));
            Assert.NotEqual(version, t.Root.Version);
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
            Assert.Equal(1L, t.Root.Get(f.Of("j")));
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

            // (union a b) stands; (set (size b) 2) then meets size(a) = 1.
            Assert.Throws<FactConflictException>(() => t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(size.Of(b).Is(2L));
            }));
            Assert.True(t.Root.AreEqual(a, b));
            Assert.Equal(1L, t.Root.Get(size.Of(b)));

            // The same value is no conflict.
            t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(size.Of(b).Is(1L));
            });
            Assert.True(t.Root.AreEqual(a, b));
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public void ACallThatFailsWritesNothingAndTheOtherCallsStand()
        {
            var t = new NodeTheory();
            var size = t.Graph.DefineFunction<Node, long>("size");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(size.Of(a).Is(1L), size.Of(b).Is(2L));
            var made = default(Node);

            t.Root.Write(w =>
            {
                made = w.Make(t.Wrap.Of(a));
                w.Assume(t.Num.Of(1L));
                Assert.Throws<FactConflictException>(() => w.Union(a, b));
                w.Assume(t.Num.Of(2L));
            });

            Assert.True(t.Root.Check(t.Num.Of(1L)));
            Assert.True(t.Root.Check(t.Num.Of(2L)));
            Assert.True(t.Root.AreEqual(made, t.Wrap.Of(a)));
            Assert.False(t.Root.AreEqual(a, b));
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public void AFailedFactLeavesEveryFactBeforeItWhereverTheBatchFlushed()
        {
            // egglog keeps (num 1), the union, (set (f 1) (A)), whose write came after the batch flushed the union, and
            // (num 2) when (set (g 1) (g 2)) fails.
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, Node>("f");
            var g = t.Graph.DefineFunction<long, long>("g");

            Assert.Throws<InvalidOperationException>(() => t.Root.Assume(
                t.Num.Of(1L), t.A.Of().Is(t.B.Of()), f.Of(1L).Is(t.A.Of()), t.Num.Of(2L), g.Of(1L).Is(g.Of(2L))));

            Assert.True(t.Root.Check(t.Num.Of(1L), t.Num.Of(2L)));
            Assert.True(t.Root.AreEqual(t.Root.Make(t.A.Of()), t.B.Of()));
            Assert.True(t.Root.AreEqual(t.Root.Get(f.Of(1L)), t.B.Of()));
            Assert.Equal(0, t.Root.CountRows(g));
        }

        [Fact]
        public void AConflictOfClassesFailsTheFactWhoseUnionMadeIt()
        {
            // (set (fo (A)) c) (set (fo (B)) d) (union (A) (B)): egglog's rebuild after the union finds two classes for one
            // key and the command fails; the facts before it stand, and a later union that would make c and d one does
            // not save it.
            var t = new NodeTheory();
            var fo = t.Graph.DefineFunction<Node, Node>("fo");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var c = t.Root.Make(t.Lit.Of(3L));
            var d = t.Root.Make(t.Lit.Of(4L));
            t.Root.Assume(fo.Of(a).Is(c), fo.Of(b).Is(d));

            Assert.Throws<FactConflictException>(() => t.Root.Assume(t.Num.Of(1L), t.A.Of().Is(t.B.Of()), t.Num.Of(2L)));
            Assert.True(t.Root.Check(t.Num.Of(1L)));
            Assert.False(t.Root.AreEqual(a, b));

            Assert.Throws<FactConflictException>(() => t.Root.Assume(t.A.Of().Is(t.B.Of()), t.Lit.Of(3L).Is(t.Lit.Of(4L))));
            Assert.False(t.Root.AreEqual(a, b));
            Assert.False(t.Root.AreEqual(c, d));

            t.Root.Write(w =>
            {
                w.Assume(t.Num.Of(3L));
                Assert.Throws<FactConflictException>(() => w.Union(a, b));
                w.Assume(t.Num.Of(4L));
            });
            Assert.True(t.Root.Check(t.Num.Of(3L), t.Num.Of(4L)));
            Assert.False(t.Root.AreEqual(a, b));
            Assert.Equal(2, t.Root.CountRows(fo));
        }

        [Fact]
        public void AFlushThatFailsLeavesTheBatchNothingMoreToWrite()
        {
            // A write of weight, which the earlier write's merge expression reads, flushes the batch first; the merge's
            // lookup of (weight 5) has no row, so the flush fails and faults the scope, and the batch writes nothing more.
            var t = new NodeTheory();
            var weight = t.Graph.DefineFunction<long, long>("weight");
            var total = t.Graph.DefineFunction<string, long>("total", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, weight.Of(@new))));
            t.Root.Assume(total.Of("k").Is(0L));

            t.Root.Write(w =>
            {
                w.Assume(t.Num.Of(1L));
                w.Assume(total.Of("k").Is(5L));
                Assert.Throws<RuleEvaluationException>(() => w.Assume(weight.Of(5L).Is(1L)));
                w.Assume(t.Num.Of(2L));
                w.Assume(weight.Of(6L).Is(1L));
            });

            Assert.True(t.Root.IsFaulted);
        }

        [Fact]
        public void ARejectedBatchWritesNothingMore()
        {
            var t = new NodeTheory();
            var version = t.Root.Version;
            var made = default(Node);

            t.Root.Write(w =>
            {
                made = w.Make(t.Wrap.Of(t.A.Of()));
                w.Assume(t.Num.Of(1L));
                BatchRejection.FromAnotherThread(w, t.Num.Of(3L));
                w.Assume(t.Num.Of(2L));
            });

            Assert.Equal(version, t.Root.Version);
            Assert.False(t.Root.Check(t.Num.Of(1L)));
            Assert.False(t.Root.Check(t.Num.Of(2L)));
            Assert.False(t.Root.Check(Fact.Exists(t.A.Of())));
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
            Assert.Contains("(g \"k\") has no value", Assert.Throws<InvalidOperationException>(() => t.Root.Assume(f.Of("k").Is(g.Of("k")))).Message);
            t.Root.Assume(Fact.Exists(t.A.Of()));
            Assert.True(t.Root.Check(Fact.Exists(t.A.Of())));
            Assert.Throws<ArgumentException>(() => t.Root.Assume(Fact.True(true)));
            Assert.Contains("(f \"k\") has no value", Assert.Throws<InvalidOperationException>(() => t.Root.Make(byLength.Of(f.Of("k")))).Message);
            var other = new NodeTheory();
            Assert.Throws<ArgumentException>(() => t.Root.Assume(other.Num.Of(1L)));
            Assert.False(t.Root.IsFaulted);
        }

        /// <summary>
        /// A call that fails and whose exception the callback catches writes nothing of its own failing fact: the facts
        /// before it, in that call and earlier ones, stand, as egglog's commands do.
        /// </summary>
        [Fact]
        public void AFailedCallTheCallbackCatchesLeavesTheFactsBeforeIt()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            t.Root.Assume(f.Of("k").Is(1L));
            var version = t.Root.Version;

            t.Root.Write(w =>
            {
                w.Assume(t.Num.Of(7L));
                try
                {
                    w.Assume(t.Num.Of(8L), f.Of("k").Is(2L));
                }
                catch (FactConflictException)
                {
                }
            });

            Assert.True(t.Root.Check(t.Num.Of(7L)));
            Assert.True(t.Root.Check(t.Num.Of(8L)));
            Assert.Equal(1L, t.Root.Get(f.Of("k")));
            Assert.NotEqual(version, t.Root.Version);
        }

        /// <summary>
        /// Inside a <c>Write</c>, a call that fails at its second fact keeps its first, egglog's command before the failed
        /// one, and writes nothing of the second.
        /// </summary>
        [Fact]
        public void AFailedCallInsideWriteKeepsTheFactsBeforeTheFailedOne()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            t.Root.Assume(f.Of("k").Is(1L));
            t.Root.Write(w =>
            {
                try
                {
                    w.Assume(t.Num.Of(8L), f.Of("k").Is(2L));
                }
                catch (FactConflictException)
                {
                }
            });

            Assert.True(t.Root.Check(t.Num.Of(8L)));
            Assert.Equal(1L, t.Root.Get(f.Of("k")));
        }

        /// <summary>
        /// A batch filled from several threads, the misuse the thread guard exists to catch: every call is either
        /// refused as in use on another thread or written; none is lost or corrupts the batch.
        /// </summary>
        /// <remarks>
        /// The calls run on the batch's own thread and on seven dedicated threads at once. <c>Parallel.For</c> would
        /// not do: it may run every iteration on the calling thread when the pool is busy (as it is when other test
        /// classes run in parallel), every call is then the batch's own, and nothing is refused although the guard
        /// works.
        /// </remarks>
        [Fact]
        public void AWriteFilledFromSeveralThreadsFailsFast()
        {
            var t = new NodeTheory();
            var refused = 0;
            var other = new System.Collections.Concurrent.ConcurrentBag<string>();
            void Call(ScopeWriter w, int i)
            {
                try
                {
                    w.Assume(t.Num.Of(i));
                }
                catch (InvalidOperationException e) when (e.Message.Contains("in use on another thread", StringComparison.Ordinal))
                {
                    System.Threading.Interlocked.Increment(ref refused);
                }
                catch (Exception e)
                {
                    other.Add(e.GetType().Name + ": " + e.Message);
                }
            }

            var failure = Record.Exception(() => t.Root.Write(w =>
            {
                var threads = new System.Threading.Thread[7];
                for (var k = 0; k < threads.Length; k++)
                {
                    var first = k + 1;
                    threads[k] = new System.Threading.Thread(() =>
                    {
                        for (var i = first; i < 20000; i += 8)
                            Call(w, i);
                    });
                    threads[k].Start();
                }

                for (var i = 0; i < 20000; i += 8)
                    Call(w, i);

                foreach (var thread in threads)
                    thread.Join();
            }));

            var written = 0;
            for (var i = 0; i < 20000; i++)
                if (t.Root.Check(t.Num.Of(i)))
                    written++;

            Assert.True(other.IsEmpty && failure is null && refused > 0,
                $"refused by the guard: {refused}; written: {written} of 20000; other exceptions: {other.Count} ({string.Join(" | ", System.Linq.Enumerable.Take(System.Linq.Enumerable.Distinct(other), 3))}); Write threw: {failure?.GetType().Name}");
        }

        /// <summary>
        /// Every scope call enters the thread domain's guard for the whole call, so a second thread fails fast instead
        /// of interleaving: a <see cref="ScopeWriter"/> call from another thread during the batch is refused.
        /// </summary>
        /// <remarks>
        /// The call runs on a dedicated thread. A pool task waited on with
        /// <c>Task.Run(...).GetAwaiter().GetResult()</c> may run inline on the waiting thread (the wait tries to before
        /// it blocks), which makes the call a same-thread call that the re-entrant guard rightly allows.
        /// </remarks>
        [Fact]
        public void AScopeWriterCallFromAnotherThreadIsRefused()
        {
            var t = new NodeTheory();
            Exception? fromOtherThread = null;
            var caller = Environment.CurrentManagedThreadId;
            var callee = caller;
            t.Root.Write(w =>
            {
                var thread = new System.Threading.Thread(() =>
                {
                    callee = Environment.CurrentManagedThreadId;
                    fromOtherThread = Record.Exception(() => w.Assume(t.Num.Of(9L)));
                });
                thread.Start();
                thread.Join();
            });

            Assert.NotEqual(caller, callee);
            Assert.IsType<InvalidOperationException>(fromOtherThread);
            Assert.False(t.Root.Check(t.Num.Of(9L)));
        }

        /// <summary>
        /// A public exception constructor validates its arguments like any public API:
        /// <see cref="FactConflictException"/> refuses a <c>null</c> table.
        /// </summary>
        [Fact]
        public void FactConflictExceptionValidatesItsArguments()
        {
            var ex = Record.Exception(() => new FactConflictException(null!, Array.Empty<object>(), 1L, 2L));
            Assert.IsType<ArgumentNullException>(ex);
        }

        /// <summary>
        /// A failed call that the write delegate catches leaves the batch's other calls: its earlier fact and term are
        /// written, and the class its <c>Make</c> returned is valid.
        /// </summary>
        [Fact]
        public void AFailedCallThatTheWriteDelegateCatchesLeavesTheOtherCalls()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var made = default(Node);
            t.Root.Write(w =>
            {
                w.Assume(t.Num.Of(5L));
                made = w.Make(t.A.Of());
                try
                {
                    w.Assume(t.Num.Of(f.Of(1L)));
                }
                catch (InvalidOperationException)
                {
                    // (f 1) has no value: the call writes nothing.
                }
            });

            Assert.True(t.Root.Check(t.Num.Of(5L)));
            Assert.True(t.Root.AreEqual(t.Root.Find(made), t.A.Of()));
            Assert.Equal(0, t.Root.CountRows(f));
        }

        /// <summary>
        /// A conflict inside a <c>Write</c> and caught there leaves the batch's earlier value written.
        /// </summary>
        [Fact]
        public void AConflictCaughtInsideWriteLeavesTheEarlierValue()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            t.Root.Write(w =>
            {
                w.Assume(f.Of("k").Is(1L));
                try
                {
                    w.Assume(f.Of("k").Is(2L));
                }
                catch (FactConflictException)
                {
                    // The call writes nothing.
                }
            });

            Assert.Equal(1L, t.Root.Get(f.Of("k")));
        }

        /// <summary>
        /// A fact that fails at its second argument, after its first argument planned new terms, keeps those terms, as
        /// egglog's failed command does, but not its row.
        /// </summary>
        [Fact]
        public void APartlyPlannedFactThatFailsKeepsItsTerms()
        {
            // egglog at 5e655cae: (fail (link (Wrap (A)) (f 1))) (check (A)) (check (Wrap (A))), its terms made before the
            // read with no value.
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var link = t.Graph.DefineRelation<Node, long>("link");
            t.Root.Write(w =>
            {
                try
                {
                    // Plans Wrap(A) and A as new terms, then rejects the fact at its second argument (a function read
                    // with no value).
                    w.Assume(link.Of(t.Wrap.Of(t.A.Of()), f.Of(1L)));
                }
                catch (InvalidOperationException)
                {
                    // Rejected.
                }
            });

            Assert.True(t.Root.Check(Fact.Exists(t.A.Of())));
            Assert.True(t.Root.Check(Fact.Exists(t.Wrap.Of(t.A.Of()))));
            Assert.Equal(0, t.Root.CountRows(link));
        }

        /// <summary>
        /// A host write never gives a function without a lattice a second value: a host union that would merge two keys
        /// holding different values is refused with <see cref="FactConflictException"/>, leaves the classes apart, and
        /// does not fault the root.
        /// </summary>
        [Fact]
        public void AHostUnionThatMergesTwoConflictingKeys()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, long>("f");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(f.Of(a).Is(1L), f.Of(b).Is(2L));
            var thrown = Record.Exception(() => t.Root.Union(a, b));
            Assert.False(t.Root.IsFaulted, "the root is faulted by " + thrown?.GetType().Name);
            Assert.IsType<FactConflictException>(thrown);
            Assert.False(t.Root.AreEqual(a, b));
        }

        /// <summary>
        /// The same inside one batch: two keys of one function without a lattice that a union of the batch merges are a
        /// <see cref="FactConflictException"/>, and the root is not faulted.
        /// </summary>
        [Fact]
        public void ABatchWhoseUnionMergesTwoConflictingKeysIsAFactConflict()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, long>("f");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var thrown = Record.Exception(() => t.Root.Write(w =>
            {
                w.Assume(f.Of(a).Is(1L), f.Of(b).Is(2L));
                w.Union(a, b);
            }));
            Assert.False(t.Root.IsFaulted, "the root is faulted by " + thrown?.GetType().Name);
            Assert.IsType<FactConflictException>(thrown);
        }

        /// <summary>
        /// A <see cref="ScopeWriter"/> captured and used on another thread while the batch is open would write into the
        /// batch from a thread that does not hold the root's guard: the call is refused with
        /// <see cref="InvalidOperationException"/>.
        /// </summary>
        [Fact]
        public void AScopeWriterUsedFromAnotherThreadIsRefused()
        {
            var t = new NodeTheory();
            Exception? thrown = null;
            t.Root.Write(w =>
            {
                var thread = new System.Threading.Thread(() => thrown = Record.Exception(() => w.Assume(t.Num.Of(3L))));
                thread.Start();
                thread.Join();
            });
            Assert.IsType<InvalidOperationException>(thrown);
        }

    }

}
