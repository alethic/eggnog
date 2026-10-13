using System;
using System.Collections.Generic;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// Host write batches: atomic (a batch whose callback throws or whose call is rejected writes nothing, and the
    /// classes its <c>Make</c> calls returned are invalid), canonical, checked against functions without a lattice,
    /// confined to the thread that holds the scope's guard, and silent when nothing changes.
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

        /// <summary>
        /// If the write callback throws or a call is rejected, nothing is written: a rejected call whose exception the
        /// callback catches still leaves the batch unwritten, and the version where it was.
        /// </summary>
        [Fact]
        public void AWriteWithARejectedCallWritesNothingEvenWhenTheCallbackCatches()
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

            Assert.False(t.Root.Check(t.Num.Of(7L)));
            Assert.False(t.Root.Check(t.Num.Of(8L)));
            Assert.Equal(version, t.Root.Version);
        }

        /// <summary>
        /// Nothing is written when a fact fails: inside a <c>Write</c>, a rejected call leaves none of the facts it
        /// planned before the failing one.
        /// </summary>
        [Fact]
        public void ARejectedCallInsideWriteLeavesNoPartOfItself()
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

            Assert.False(t.Root.Check(t.Num.Of(8L)));
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
        /// A rejected call that the write delegate catches still leaves the whole batch unwritten: its earlier fact and
        /// term are not written, the version does not move, and the class its <c>Make</c> returned is invalid.
        /// </summary>
        [Fact]
        public void ARejectedCallThatTheWriteDelegateCatchesStillWritesNothing()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var version = t.Root.Version;
            var made = default(Node);
            t.Root.Write(w =>
            {
                w.Assume(t.Num.Of(5L));
                made = w.Make(t.A.Of());
                try
                {
                    w.Assume(t.Num.Of(f.Of(1L)));
                }
                catch (ArgumentException)
                {
                    // The call was rejected; the batch must write nothing.
                }
            });

            Assert.False(t.Root.Check(t.Num.Of(5L)));
            Assert.False(t.Root.Check(Fact.Exists(t.A.Of())));
            Assert.Equal(version, t.Root.Version);
            Assert.Throws<ArgumentException>(() => t.Root.Find(made));
        }

        /// <summary>
        /// A conflict rejected inside a <c>Write</c> and caught there leaves the batch's earlier value unwritten too.
        /// </summary>
        [Fact]
        public void ARejectedConflictCaughtInsideWriteStillWritesNothing()
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
                    // Rejected.
                }
            });

            Assert.False(t.Root.TryGet(f.Of("k"), out _));
        }

        /// <summary>
        /// A fact rejected at its second argument, after its first argument planned new terms, leaves none of those
        /// terms.
        /// </summary>
        [Fact]
        public void APartlyPlannedFactThatIsRejectedLeavesNoTerm()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var link = t.Graph.DefineRelation<Node, long>("link");
            t.Root.Write(w =>
            {
                try
                {
                    // Plans Wrap(A) and A as new terms, then rejects the fact at its second argument (a function read).
                    w.Assume(link.Of(t.Wrap.Of(t.A.Of()), f.Of(1L)));
                }
                catch (ArgumentException)
                {
                    // Rejected.
                }
            });

            Assert.False(t.Root.Check(Fact.Exists(t.A.Of())));
            Assert.False(t.Root.Check(Fact.Exists(t.Wrap.Of(t.A.Of()))));
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
