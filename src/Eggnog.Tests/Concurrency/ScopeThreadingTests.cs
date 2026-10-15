using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Predicates;
using Eggnog.Tests.Predicates.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Concurrency
{

    /// <summary>
    /// Thread domains of scopes under load: a domain is shared by a writable scope and its children and reassigned when
    /// a scope freezes; a second thread fails fast; frozen scopes are read, queried and extracted from many threads in
    /// transient query children; releases wait for the last call; and runs meet definitions made on other threads.
    /// Every test runs on dedicated threads, never on pool threads that could run a task inline.
    /// </summary>
    public sealed class ScopeThreadingTests
    {

        /// <summary>
        /// How long any test waits for another thread before it fails.
        /// </summary>
        static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Starts <paramref name="body"/> on a dedicated background thread; an exception it throws lands in
        /// <paramref name="errors"/>[<paramref name="slot"/>].
        /// </summary>
        static Thread Spawn(Action body, Exception?[] errors, int slot)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception exception)
                {
                    errors[slot] = exception;
                }
            })
            { IsBackground = true };
            thread.Start();
            return thread;
        }

        /// <summary>
        /// Gets a scope's private children lock: holding it stands for a concurrent <c>CreateChild</c>, <c>Children</c>
        /// or transient query that holds it, and widens the window between the engine's freeze and the rule layer's
        /// reassignment of guards to a deterministic one.
        /// </summary>
        static object ChildrenLockOf(Scope scope) =>
            typeof(Scope).GetField("childrenLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scope)!;

        /// <summary>
        /// A child created on another thread once its parent's context is frozen, but before the freeze has given the
        /// parent's children their new guards, keeps the guard its call holds: a third thread that writes into the
        /// child during that call fails fast as in use on another thread, and the call exits cleanly. Holding the
        /// parent's children lock keeps that window open.
        /// </summary>
        [Fact]
        public void AChildCreatedAsItsParentFreezesKeepsTheGuardItsCallHolds()
        {
            var t = new ScopeTheory();
            var all = t.Graph.DefineQuery("all", q => q.When(t.Fact.Of(q.Var<string>("x"))));
            t.Root.Freeze();
            var parent = t.Root.CreateChild(t.Outer);
            parent.Assume(t.Fact.Of("a"));
            var childrenLock = ChildrenLockOf(parent);
            var errors = new Exception?[3];
            using var entered = new ManualResetEventSlim();
            using var go = new ManualResetEventSlim();
            var depthInCall = -1;
            Scope child;
            Thread freezer;
            Thread caller;

            Monitor.Enter(childrenLock);
            try
            {
                freezer = Spawn(parent.Freeze, errors, 0);
                Assert.True(SpinWait.SpinUntil(() => parent.IsFrozen, Patience));

                // The parent's context is frozen: a child made now gets a thread domain of its own, and a call on it
                // may start at once on another thread.
                child = parent.CreateChild();
                var created = child;
                caller = Spawn(() => created.ForEachMatch(all, _ =>
                {
                    entered.Set();
                    go.Wait(Patience);
                    depthInCall = created.Guard.Depth;
                    return false;
                }), errors, 1);
                Assert.True(entered.Wait(Patience));
            }
            finally
            {
                Monitor.Exit(childrenLock);
            }

            Assert.True(freezer.Join(Patience));
            Assert.Null(errors[0]);

            // The caller is still inside its call on the child: a second thread must fail fast.
            var intruder = Spawn(() => child.Assume(t.Fact.Of("intruder")), errors, 2);
            Assert.True(intruder.Join(Patience));
            go.Set();
            Assert.True(caller.Join(Patience));

            var observed = string.Join("; ",
                errors[2] is InvalidOperationException refused && refused.Message.Contains("in use on another thread", StringComparison.Ordinal)
                    ? "the intruder was refused" : $"the intruder's write ran during the call ({errors[2]?.GetType().Name ?? "no exception"})",
                depthInCall > 0 ? "the call held the child's guard" : "the call did not hold the child's guard",
                errors[1] is null ? "the call ended cleanly" : $"the call's exit threw {errors[1]!.GetType().Name}: {errors[1]!.Message}");
            if (observed != "the intruder was refused; the call held the child's guard; the call ended cleanly")
                Assert.Fail(observed);
        }

        /// <summary>
        /// The race of <see cref="AChildCreatedAsItsParentFreezesKeepsTheGuardItsCallHolds"/> without any help: a
        /// thread waits for the parent's context to freeze, creates a child and calls into it at once, while the
        /// freezing thread is still giving the parent's live children their new guards.
        /// </summary>
        [Fact]
        public void ChildrenCreatedAsTheirParentFreezesKeepTheGuardsTheirCallsHold()
        {
            const int Trials = 60;
            const int Siblings = 300;
            const int Readers = 2;
            var reassigned = 0;
            var torn = 0;
            var failedExits = new List<string>();
            for (var trial = 0; trial < Trials; trial++)
            {
                var t = new ScopeTheory();
                var all = t.Graph.DefineQuery("all", q => q.When(t.Fact.Of(q.Var<string>("x"))));
                t.Root.Freeze();
                var parent = t.Root.CreateChild(t.Outer);
                parent.Assume(t.Fact.Of("a"));
                for (var i = 0; i < Siblings; i++)
                    parent.CreateChild();

                using var frozen = new ManualResetEventSlim();
                var errors = new Exception?[2 + Readers];
                var depth = -1;
                var stop = 0;
                ScopeGuard? created = null;
                Scope? child = null;

                // Readers list the parent's children meanwhile (a snapshot under its children lock, any thread), so the
                // freezing thread and the creating thread meet that lock in either order.
                var readers = Enumerable.Range(0, Readers).Select(r => Spawn(() =>
                {
                    while (Volatile.Read(ref stop) == 0)
                        _ = parent.Children.Count;
                }, errors, 2 + r)).ToArray();
                var caller = Spawn(() =>
                {
                    while (parent.IsFrozen == false)
                        Thread.SpinWait(1);

                    var made = parent.CreateChild();
                    child = made;
                    created = made.Guard;
                    made.ForEachMatch(all, _ =>
                    {
                        frozen.Wait(Patience);
                        depth = made.Guard.Depth;
                        return false;
                    });
                }, errors, 0);
                var freezer = Spawn(() =>
                {
                    parent.Freeze();
                    frozen.Set();
                }, errors, 1);

                Assert.True(freezer.Join(Patience));
                Assert.True(caller.Join(Patience));
                Volatile.Write(ref stop, 1);
                foreach (var reader in readers)
                    Assert.True(reader.Join(Patience));

                Assert.Null(errors[1]);
                Assert.Null(errors[2]);
                Assert.Null(errors[3]);
                if (ReferenceEquals(created, child?.Guard) == false)
                    reassigned++;

                if (depth == 0)
                    torn++;

                if (errors[0] is { } error)
                    failedExits.Add(error.Message);
            }

            var observed = $"{torn} torn, {failedExits.Count} failed exits{(failedExits.Count > 0 ? " (" + failedExits[0] + ")" : "")}; {reassigned} of {Trials} children reassigned";
            if (torn > 0 || failedExits.Count > 0)
                Assert.Fail(observed);
        }

        /// <summary>
        /// Everything <c>Run</c> refuses it refuses before the first iteration, and a rule defined from another thread
        /// joins the next iteration. A global rule defined during a run that reads a tag the running scope cannot
        /// resolve meets both: the run does not fail half-way, leaving its first iterations' writes behind and losing
        /// its report; it completes.
        /// </summary>
        [Fact]
        public void ARuleDefinedDuringARunThatReadsATagTheScopeLacksDoesNotFailTheRunHalfway()
        {
            var t = new ScopeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            t.DefineBlocking(entered, release);
            t.Root.Freeze();
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));

            var errors = new Exception?[1];
            RunReport? report = null;
            var run = Spawn(() => report = outer.Run(Schedule.Saturate(t.Rules)), errors, 0);
            Assert.True(entered.Wait(Patience));

            // The root is frozen, so this thread may define a rule whose first scoped read creates a mirror.
            t.Rules.DefineRule("reads inner", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Inner).Of(x));
                r.Then(t.Seen.Of(x));
            });
            release.Set();
            Assert.True(run.Join(Patience));

            var observed = errors[0] is null
                ? $"completed: {report!.StopReason}"
                : $"threw {errors[0]!.GetType().Name} after writing seen(a) = {outer.Check(t.Seen.Of("a"))}: {errors[0]!.Message}";
            if (observed.StartsWith("completed", StringComparison.Ordinal) == false)
                Assert.Fail(observed);
        }

        /// <summary>
        /// A freeze plans the default variant of every live query, and holds each query it lists while it plans, so a
        /// query disposed on another thread meanwhile does not make the engine refuse the list: under a steady churn of
        /// single-request queries, each defined and disposed for one request, every freeze succeeds.
        /// </summary>
        [Fact]
        public void AFreezeSucceedsWhileOtherThreadsDefineRunAndDisposeQueries()
        {
            const int Churners = 6;
            const int Freezes = 400;
            var t = new ScopeTheory();
            t.Root.Freeze();
            var schema = t.Root.CreateChild(t.Outer);
            schema.Assume(t.Fact.Of("a"));
            schema.Freeze();

            var stop = 0;
            var errors = new Exception?[Churners];
            var churners = new Thread[Churners];
            for (var c = 0; c < Churners; c++)
            {
                var id = c;
                churners[c] = Spawn(() =>
                {
                    var serial = 0;
                    var batch = new List<Query>();
                    while (Volatile.Read(ref stop) == 0)
                    {
                        for (var i = 0; i < 40; i++)
                            batch.Add(t.Graph.DefineQuery($"churn {id}/{serial++}", q => q.When(t.Fact.Of(q.Var<string>("x")))));

                        foreach (var query in batch)
                        {
                            schema.Count(query);
                            query.Dispose();
                        }

                        batch.Clear();
                    }
                }, errors, id);
            }

            var failures = new List<string>();
            try
            {
                for (var i = 0; i < Freezes; i++)
                {
                    var inner = t.Root.CreateChild(t.Inner);
                    inner.Assume(t.Fact.Of("b" + i));
                    try
                    {
                        inner.Freeze();
                    }
                    catch (Exception exception)
                    {
                        failures.Add($"{exception.GetType().Name}: {exception.Message}");
                    }

                    inner.Dispose();
                }
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                foreach (var churner in churners)
                    Assert.True(churner.Join(Patience));
            }

            Assert.All(errors, Assert.Null);
            if (failures.Count > 0)
                Assert.Fail($"{failures.Count} of {Freezes} freezes failed: {failures[0]}");
        }

        /// <summary>
        /// While a run holds a writable domain (a parent, its running child and the child's own child) every member of
        /// every scope of the domain fails fast on another thread, and nothing it attempted is written.
        /// </summary>
        [Fact]
        public void EveryMemberOfABusyDomainFailsFastOnAnotherThread()
        {
            var t = new NodeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            t.Rules.DefineRule("block", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(x, _ =>
                {
                    entered.Set();
                    release.Wait(Patience);
                    return true;
                });
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var noRules = t.Graph.DefineRuleset("none");
            var nums = t.Graph.DefineQuery("nums", q => q.When(t.Num.Of(q.Var<long>("x"))));
            t.Root.Freeze();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            var child = parent.CreateChild();
            var grandchild = child.CreateChild();
            child.Assume(t.Num.Of(0L));
            var extractor = child.CreateExtractor();
            var errors = new Exception?[1];
            var run = Spawn(() => child.Run(Schedule.Repeat(1, t.Rules)), errors, 0);
            Assert.True(entered.Wait(Patience));

            var refusedAll = new List<string>();
            foreach (var scope in new[] { parent, child, grandchild })
            {
                var calls = new (string Name, Action Call)[]
                {
                    ("Assume", () => scope.Assume(t.Num.Of(100L))),
                    ("Make", () => scope.Make(t.B.Of())),
                    ("Union", () => scope.Union(a, a)),
                    ("Write", () => scope.Write(w => w.Assume(t.Num.Of(101L)))),
                    ("Run", () => scope.Run(noRules)),
                    ("Check", () => scope.Check(t.Num.Of(0L))),
                    ("TryGet", () => scope.TryGet(t.A.Of(), out _)),
                    ("Find", () => scope.Find(a)),
                    ("AreEqual", () => scope.AreEqual(a, a)),
                    ("CountRows", () => scope.CountRows(t.Num)),
                    ("Rows", () => scope.Rows(t.Num)),
                    ("Any", () => scope.Any(nums)),
                    ("Count", () => scope.Count(nums)),
                    ("ForEachMatch", () => scope.ForEachMatch(nums, _ => true)),
                    ("IsContradictory", () => _ = scope.IsContradictory),
                    ("CreateChild", () => scope.CreateChild()),
                    ("CreateExtractor", () => scope.CreateExtractor()),
                    ("DefineRule", () => scope.DefineRule(noRules, "late", r =>
                    {
                        var x = r.Var<long>("x");
                        r.When(t.Num.Of(x));
                        r.Then(t.Num.Of(x));
                    })),
                    ("Freeze", scope.Freeze),
                };
                foreach (var (name, call) in calls)
                {
                    var slot = new Exception?[1];
                    Assert.True(Spawn(call, slot, 0).Join(Patience));
                    if (slot[0] is not InvalidOperationException refused || refused.Message.Contains("in use on another thread", StringComparison.Ordinal) == false)
                        refusedAll.Add($"{scope.Path}.{name}: {slot[0]?.GetType().Name ?? "ran"}");
                }
            }

            var extract = new Exception?[1];
            Assert.True(Spawn(() => extractor.Extract(a), extract, 0).Join(Patience));
            if (extract[0] is not InvalidOperationException)
                refusedAll.Add($"extractor: {extract[0]?.GetType().Name ?? "ran"}");

            release.Set();
            Assert.True(run.Join(Patience));
            Assert.Null(errors[0]);
            Assert.Empty(refusedAll);
            Assert.False(child.Check(t.Num.Of(100L)));
            Assert.False(child.Check(t.Num.Of(101L)));
            Assert.False(parent.Check(t.Num.Of(100L)));
            Assert.True(child.Check(t.Num.Of(1L)));
            Assert.Empty(child.LocalRules);
        }

        /// <summary>
        /// Freezing a scope gives each live child subtree its own domain. Two children that shared the parent's domain
        /// then run at the same time on two threads (a barrier in their rule proves the overlap), and a grandchild
        /// still shares its running parent's domain.
        /// </summary>
        [Fact]
        public void AFreezeSplitsItsChildrenIntoDomainsThatRunTogether()
        {
            var t = new ScopeTheory();
            using var bothInside = new Barrier(2);
            using var go = new ManualResetEventSlim();
            var overlapped = 0;
            t.Rules.DefineRule("meet", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Where(x, _ =>
                {
                    if (bothInside.SignalAndWait(Patience))
                        Interlocked.Increment(ref overlapped);

                    go.Wait(Patience);
                    return true;
                });
                r.Then(t.Seen.Of(x));
            });
            t.Root.Freeze();
            var parent = t.Root.CreateChild(t.Outer);
            var left = parent.CreateChild();
            var right = parent.CreateChild();
            var leftChild = left.CreateChild();
            left.Assume(t.Fact.Of("l"));
            right.Assume(t.Fact.Of("r"));
            Assert.Same(parent.Guard, left.Guard);
            parent.Freeze();
            Assert.NotSame(left.Guard, right.Guard);
            Assert.Same(left.Guard, leftChild.Guard);

            var errors = new Exception?[3];
            var runs = new[]
            {
                Spawn(() => left.Run(Schedule.Repeat(1, t.Rules)), errors, 0),
                Spawn(() => right.Run(Schedule.Repeat(1, t.Rules)), errors, 1),
            };
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref overlapped) == 2, Patience));
            Assert.True(Spawn(() => leftChild.Check(t.Fact.Of("l")), errors, 2).Join(Patience));
            go.Set();
            foreach (var run in runs)
                Assert.True(run.Join(Patience));

            Assert.Null(errors[0]);
            Assert.Null(errors[1]);
            Assert.Contains("in use on another thread", Assert.IsType<InvalidOperationException>(errors[2]).Message);
            Assert.True(left.Check(t.Seen.Of("l")));
            Assert.True(right.Check(t.Seen.Of("r")));
            Assert.False(left.Check(t.Seen.Of("r")));
        }

        /// <summary>
        /// Queries on a frozen scope run at the same time in transient children (a barrier in the query's filter proves
        /// the overlap); the children are counted in the engine but never listed; a disposal during them is released
        /// after the last one, exactly once, and uncounts the frozen tag.
        /// </summary>
        [Fact]
        public void EightQueriesOnAFrozenScopeOverlapInTransientChildrenAndDeferItsRelease()
        {
            const int Threads = 8;
            var t = new ScopeTheory();
            using var allInside = new Barrier(Threads + 1);
            using var go = new ManualResetEventSlim();
            var meet = t.Graph.DefineQuery("meet", q =>
            {
                var x = q.Var<string>("x");
                q.When(t.Fact.In(t.Outer).Of(x));
                q.Where(x, _ =>
                {
                    allInside.SignalAndWait(Patience);
                    go.Wait(Patience);
                    return true;
                });
            });
            t.Root.Freeze();
            var schema = t.Root.CreateChild(t.Outer);
            schema.Assume(t.Fact.Of("a"));
            schema.Freeze();
            Assert.Equal(1, t.Graph.Mirrors.FrozenCount(t.Outer));

            var errors = new Exception?[Threads];
            var counts = new long[Threads];
            var threads = Enumerable.Range(0, Threads).Select(i => Spawn(() => counts[i] = schema.Count(meet), errors, i)).ToArray();
            Assert.True(allInside.SignalAndWait(Patience));

            Assert.Empty(schema.Children);
            Assert.Equal(Threads, schema.Engine.Context.Context.ChildCount);
            schema.Dispose();
            Assert.False(schema.Engine.IsDisposed);
            Assert.Equal(1, t.Graph.Mirrors.FrozenCount(t.Outer));

            go.Set();
            foreach (var thread in threads)
                Assert.True(thread.Join(Patience));

            Assert.All(errors, Assert.Null);
            Assert.All(counts, count => Assert.Equal(1L, count));
            Assert.True(schema.Engine.IsDisposed);
            Assert.Equal(0, t.Graph.Mirrors.FrozenCount(t.Outer));
            Assert.Equal(0, t.Root.Engine.Context.Context.ChildCount);
            Assert.Empty(t.Root.Children);
        }

        /// <summary>
        /// A frozen schema with a local rule and a scoped read, queried, extracted (one extractor per call) and
        /// given query children from eight dedicated threads, is disposed in the middle of it. Every call either
        /// completes or is refused with <see cref="ObjectDisposedException"/>; the schema is released once, after
        /// the last child and call; its local rule is freed and its tag uncounted.
        /// </summary>
        [Fact]
        public void AFrozenSchemaDisposedUnderLoadIsReleasedOnceAfterTheLastCall()
        {
            const int Threads = 8;
            var c = new PredicateTheory(new EGraph());
            var present = c.Graph.DefineQuery("present", q => q.When(c.Present.In(c.Schema).Of(q.Var<string>("p"))));
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            DiscriminatedSchema.Declare(c, schema);
            var local = schema.DefineRule(c.Horn, "local present", r =>
            {
                var p = r.Var<string>("p");
                r.When(c.Present.Of(p));
                r.Then(c.Present.Of(p));
            });
            var terms = DiscriminatedSchema.Queries(c);
            var inSchema = terms.Select(term => schema.Make(term)).ToArray();
            schema.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            schema.Freeze();
            var description = local.Compiled.Variants.Default!.Description;
            var expectedPresent = schema.Count(present);
            var expectedInSchema = inSchema.Select(x => schema.Extract(x).ToString()).ToArray();
            var expected = terms.Select(term =>
            {
                using var query = schema.CreateChild(c.Query);
                var q = query.Make(term);
                query.Assume(c.WhereClause.Of(q));
                query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
                return query.Extract(q).ToString();
            }).ToArray();

            var stop = 0;
            var completed = 0;
            var errors = new Exception?[Threads];
            var wrong = new List<string>();
            var threads = Enumerable.Range(0, Threads).Select(i => Spawn(() =>
            {
                for (var round = 0; Volatile.Read(ref stop) == 0 || round < 4; round++)
                {
                    try
                    {
                        var k = (i + round) % terms.Length;
                        if (schema.Count(present) != expectedPresent)
                            lock (wrong) wrong.Add($"count on {i}");

                        if (schema.CreateExtractor().Extract(inSchema[k]).ToString() != expectedInSchema[k])
                            lock (wrong) wrong.Add($"schema extraction on {i}");

                        using var query = schema.CreateChild(c.Query);
                        var q = query.Make(terms[k]);
                        query.Assume(c.WhereClause.Of(q));
                        query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
                        if (query.CreateExtractor().Extract(q).ToString() != expected[k])
                            lock (wrong) wrong.Add($"extraction on {i}");

                        Interlocked.Increment(ref completed);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            }, errors, i)).ToArray();

            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref completed) >= Threads * 2, Patience));
            schema.Dispose();
            Volatile.Write(ref stop, 1);
            foreach (var thread in threads)
                Assert.True(thread.Join(Patience));

            Assert.All(errors, Assert.Null);
            Assert.Empty(wrong);
            Assert.True(schema.Engine.IsDisposed);
            Assert.False(c.Graph.VariantsByDescription.ContainsKey(description));
            Assert.Equal(0, c.Graph.Mirrors.FrozenCount(c.Schema));
            Assert.Equal(0, c.Graph.Root.Engine.Context.Context.ChildCount);
            Assert.Empty(c.Graph.Root.Children);
        }

        /// <summary>
        /// Children disposed on another thread while their writable parent freezes are deferred to the parent's guard
        /// (the freezing thread holds it), then released through the guards the freeze gave them. None is lost: every
        /// engine child is disposed and the parent lists none.
        /// </summary>
        [Fact]
        public void ChildrenDisposedWhileTheirParentFreezesAreAllReleased()
        {
            const int Trials = 30;
            const int Children = 40;
            for (var trial = 0; trial < Trials; trial++)
            {
                var t = new ScopeTheory();
                t.Root.Freeze();
                var parent = t.Root.CreateChild(t.Outer);
                parent.Assume(t.Fact.Of("a"));
                var children = new Scope[Children];
                for (var i = 0; i < Children; i++)
                {
                    children[i] = parent.CreateChild();
                    if (i % 4 == 0)
                        children[i].CreateChild();
                }

                using var start = new ManualResetEventSlim();
                var errors = new Exception?[2];
                var disposer = Spawn(() =>
                {
                    start.Wait(Patience);
                    foreach (var child in children)
                        child.Dispose();
                }, errors, 0);
                var freezer = Spawn(() =>
                {
                    start.Wait(Patience);
                    parent.Freeze();
                }, errors, 1);
                start.Set();
                Assert.True(disposer.Join(Patience));
                Assert.True(freezer.Join(Patience));
                Assert.Null(errors[0]);
                Assert.Null(errors[1]);

                // The grandchildren keep every fourth child alive; disposing them releases the rest.
                foreach (var child in children)
                    foreach (var grandchild in child.Children)
                        grandchild.Dispose();

                Assert.True(parent.IsFrozen);
                Assert.All(children, child => Assert.True(child.Engine.IsDisposed, child.Path));
                Assert.Empty(parent.Children);
                Assert.Equal(0, parent.Engine.Context.Context.ChildCount);
            }
        }

        /// <summary>
        /// Query scopes with a degree of parallelism of 4, forced onto the parallel paths, run at the same time on four
        /// dedicated threads under one frozen schema, and each has the serial run's facts.
        /// </summary>
        [Fact]
        public void ParallelQueryScopesOnConcurrentThreadsHaveTheSerialFacts()
        {
            const int Degree = 4;
            static (PredicateTheory C, Scope Schema) Compile(int degree)
            {
                var c = new PredicateTheory(new EGraph(new EGraphOptions { DegreeOfParallelism = degree }));
                c.Graph.Root.Freeze();
                var schema = c.Graph.Root.CreateChild(c.Schema, degree > 1 ? new ScopeOptions { DegreeOfParallelism = degree } : null);
                if (degree > 1)
                    schema.Engine.Context.Context.Database.ParallelRuleSetsOverride = true;

                DiscriminatedSchema.Declare(c, schema);
                schema.Run(Schedule.Saturate(c.Horn, c.Lattice));
                schema.Freeze();
                return (c, schema);
            }

            static IReadOnlyList<string> Ask(PredicateTheory c, Scope schema, ConstructorTerm<Pred> term, int degree)
            {
                using var query = schema.CreateChild(c.Query, degree > 1 ? new ScopeOptions { DegreeOfParallelism = degree } : null);
                if (degree > 1)
                    query.Engine.Context.Context.Database.ParallelRuleSetsOverride = true;

                var q = query.Make(term);
                query.Assume(c.WhereClause.Of(q));
                query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
                return ScopeFacts.Of(query);
            }

            var (serialTheory, serialSchema) = Compile(1);
            var expected = DiscriminatedSchema.Queries(serialTheory).Select(term => Ask(serialTheory, serialSchema, term, 1)).ToArray();

            var (c, schema) = Compile(Degree);
            var terms = DiscriminatedSchema.Queries(c);
            var results = new IReadOnlyList<string>[terms.Length * 2];
            var errors = new Exception?[Degree];
            var next = -1;
            var threads = Enumerable.Range(0, Degree).Select(i => Spawn(() =>
            {
                for (var k = Interlocked.Increment(ref next); k < results.Length; k = Interlocked.Increment(ref next))
                    results[k] = Ask(c, schema, terms[k % terms.Length], Degree);
            }, errors, i)).ToArray();
            foreach (var thread in threads)
                Assert.True(thread.Join(Patience));

            Assert.All(errors, Assert.Null);
            for (var k = 0; k < results.Length; k++)
                Assert.Equal(expected[k % expected.Length], results[k]);

            Assert.Equal(0, schema.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// Two graphs, each with a frozen schema under load on its own threads, never see each other's frozen tags
        /// or guards: tags of the same name in two graphs stay independent while both are queried and disposed.
        /// </summary>
        [Fact]
        public void TwoGraphsUnderConcurrentLoadKeepTheirTagCountsApart()
        {
            const int Threads = 4;
            var graphs = new[] { new ScopeTheory(), new ScopeTheory() };
            var queries = graphs.Select(t => t.Graph.DefineQuery("outer facts", q => q.When(t.Fact.In(t.Outer).Of(q.Var<string>("x"))))).ToArray();
            var schemas = new Scope[2];
            for (var g = 0; g < 2; g++)
            {
                graphs[g].Root.Freeze();
                schemas[g] = graphs[g].Root.CreateChild(graphs[g].Outer);
                schemas[g].Assume(graphs[g].Fact.Of("g" + g));
                schemas[g].Freeze();
            }

            var errors = new Exception?[Threads * 2];
            var wrong = 0;
            var threads = Enumerable.Range(0, Threads * 2).Select(i => Spawn(() =>
            {
                var g = i % 2;
                for (var round = 0; round < 200; round++)
                {
                    using var child = schemas[g].CreateChild(graphs[g].Inner);
                    if (child.Count(queries[g]) != 1 || child.Check(graphs[g].Fact.Of("g" + (1 - g))))
                        Interlocked.Increment(ref wrong);
                }
            }, errors, i)).ToArray();
            foreach (var thread in threads)
                Assert.True(thread.Join(Patience));

            Assert.All(errors, Assert.Null);
            Assert.Equal(0, wrong);
            schemas[0].Dispose();
            Assert.Equal(0, graphs[0].Graph.Mirrors.FrozenCount(graphs[0].Outer));
            Assert.Equal(1, graphs[1].Graph.Mirrors.FrozenCount(graphs[1].Outer));

            // The first graph's tag may gain a mirror again; the second's still refuses one.
            graphs[0].Rules.DefineRule("late outer", r =>
            {
                var x = r.Var<string>("x");
                r.When(graphs[0].Seen.In(graphs[0].Outer).Of(x));
                r.Then(graphs[0].Fact.Of(x));
            });
            Assert.Throws<RuleDefinitionException>(() =>
                graphs[1].Rules.DefineRule("late outer", r =>
                {
                    var x = r.Var<string>("x");
                    r.When(graphs[1].Seen.In(graphs[1].Outer).Of(x));
                    r.Then(graphs[1].Fact.Of(x));
                }));
        }

        /// <summary>
        /// Each child of a frozen scope is its own thread domain, and a scope's guard is reassigned only while no call
        /// of the domain is in progress. A child created on another thread in the window between the engine freeze and
        /// <c>Freeze</c>'s reassignment of the child guards (held open here by holding the mirror registry's lock,
        /// which <c>AddFrozen</c> takes) and already running keeps its domain: a third thread gets the rule layer's
        /// refusal, and the run completes.
        /// </summary>
        [Fact]
        public void AChildCreatedWhileItsParentFreezesKeepsItsThreadDomain()
        {
            var t = new ScopeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            t.DefineBlocking(entered, release);
            t.Root.Freeze();
            var parent = t.Root.CreateChild(t.Outer);
            var registryLock = typeof(MirrorRegistry).GetField("sync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(t.Graph.Mirrors)!;

            Task freeze;
            Scope? child = null;
            Task<RunReport>? run = null;
            Monitor.Enter(registryLock);
            try
            {
                freeze = Task.Factory.StartNew(parent.Freeze, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(SpinWait.SpinUntil(() => parent.IsFrozen, TimeSpan.FromSeconds(30)));

                // The engine froze the parent; Freeze waits in AddFrozen. Another thread creates a child and runs it.
                run = Task.Factory.StartNew(() =>
                {
                    child = parent.CreateChild();
                    child.Assume(t.Fact.Of("a"));
                    return child.Run(Schedule.Saturate(t.Rules));
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            }
            finally
            {
                Monitor.Exit(registryLock);
            }

            Assert.True(freeze.Wait(TimeSpan.FromSeconds(30)));

            // The child's domain is in use by the run: a second thread must get the rule layer's refusal.
            var intruder = Task.Factory.StartNew(() => Record.Exception(() => child!.Assume(t.Fact.Of("b"))),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
            release.Set();
            var finished = Record.Exception(() => run.GetAwaiter().GetResult());

            Assert.Contains("is in use on another thread", Assert.IsType<InvalidOperationException>(intruder).Message);
            Assert.Null(finished);
            var report = run.Result;
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(child!.Check(t.Seen.Of("a")));
        }

    }

}
