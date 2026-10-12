using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review probes of the public API against the rule-layer spec (R1a). Each test asserts the spec's behaviour.
    /// </summary>
    public sealed class ApiReviewTests
    {

        /// <summary>
        /// §3.1 Scope.Write: "If write throws or a call is rejected, nothing is written". A rejected call whose exception
        /// the callback catches must still leave the batch unwritten.
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
        /// §3.1 Scope.Assume/ScopeWriter.Assume: "Nothing is written when a fact fails". Inside a Write, a rejected call
        /// must not leave the facts it planned before the failing one.
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
        /// D47 misuse that the guard exists to catch: a batch filled from several threads. Every call must either be
        /// refused or be written; none may be lost or corrupt the batch.
        /// </summary>
        /// <remarks>
        /// The calls run on the batch's own thread and on seven dedicated threads at once. The review's version used
        /// <c>Parallel.For</c>, which may run every iteration on the calling thread when the pool is busy (as it is when
        /// other test classes run in parallel); every call is then legitimately the batch's own, nothing is refused, and
        /// the test failed although the guard worked.
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
        /// D39 with §3.1 Scope.Write: a value made by a batch whose call was rejected is invalid afterwards.
        /// </summary>
        [Fact]
        public void AMakeOfABatchWithARejectedCallIsInvalidAfterwards()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            t.Root.Assume(f.Of("k").Is(1L));
            var made = default(Node);
            t.Root.Write(w =>
            {
                made = w.Make(t.A.Of());
                try
                {
                    w.Assume(f.Of("k").Is(2L));
                }
                catch (FactConflictException)
                {
                }
            });

            Assert.Throws<ArgumentException>(() => t.Root.Find(made));
        }

        /// <summary>
        /// D47: every scope call enters the thread domain's guard for the whole call, "so a second thread fails fast
        /// instead of interleaving". A ScopeWriter call from another thread during the batch must be refused.
        /// </summary>
        /// <remarks>
        /// The call runs on a dedicated thread. The review's version used <c>Task.Run(...).GetAwaiter().GetResult()</c>,
        /// whose task the waiting pool thread may run inline (it tries to before blocking), which makes the call a
        /// same-thread call that the re-entrant guard rightly allows; the test then failed whenever the pool was busy.
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
        /// D39: default(ClassId) names no class and every scope rejects it; class values never cross into value cells
        /// (D20: "There are no sets of classes", §6.1: classes are compared only with Is and IsNot). ValueStore.Register
        /// must not make ClassId, or a sort, a value type.
        /// </summary>
        [Fact]
        public void RegisterRefusesClassIdAndSortsAsValueTypes()
        {
            var g = new EGraph();
            Assert.Throws<ArgumentException>(() => g.Values.Register<ClassId>("cid"));
            Assert.Throws<ArgumentException>(() => g.Values.Register<Node>("node"));
        }

        /// <summary>
        /// The consequence of the previous probe: once ClassId is a value type, a host write stores default(ClassId),
        /// which D39 says every scope rejects.
        /// </summary>
        [Fact]
        public void ADefaultClassIdNeverReachesATable()
        {
            var g = new EGraph();
            try
            {
                g.Values.Register<ClassId>("cid");
            }
            catch (ArgumentException)
            {
                return;
            }

            var f = g.DefineFunction<string, ClassId>("f");
            Assert.Throws<ArgumentException>(() => g.Root.Assume(f.Of("k").Is(default(ClassId))));
        }

        /// <summary>
        /// ValueSet's element order is Comparer&lt;T&gt;.Default; RegisterSet must not register a set type whose sets
        /// cannot hold two elements.
        /// </summary>
        [Fact]
        public void RegisterSetRefusesAnElementTypeWithNoOrder()
        {
            var g = new EGraph();
            g.Values.Register<Opaque>("Opaque", Comparer<Opaque>.Create((x, y) => x.X.CompareTo(y.X)));
            var registered = Record.Exception(() => g.Values.RegisterSet<Opaque>());
            if (registered is ArgumentException)
                return;

            Assert.Null(registered);
            var two = Record.Exception(() => ValueSet.Of(new Opaque(1), new Opaque(2)));
            Assert.Null(two);
        }

        /// <summary>
        /// D15: values used only for reading never intern. ColumnType&lt;T&gt;.Compare is a read; it must not grow the
        /// graph's pools.
        /// </summary>
        [Fact]
        public void CompareDoesNotIntern()
        {
            var g = new EGraph();
            var type = g.Values.Register<Opaque>("Opaque");
            var before = g.Values.InternedCount;
            type.Compare(new Opaque(1), new Opaque(2));
            Assert.Equal(before, g.Values.InternedCount);
        }

        /// <summary>
        /// A public exception constructor validates its arguments like any public API.
        /// </summary>
        [Fact]
        public void FactConflictExceptionValidatesItsArguments()
        {
            var ex = Record.Exception(() => new FactConflictException(null!, Array.Empty<object>(), 1L, 2L));
            Assert.IsType<ArgumentNullException>(ex);
        }

    }

}
