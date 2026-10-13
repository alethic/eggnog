using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review probes of R2's public surface: each test asserts what the spec (rule-layer §3, §6.11, §6.13, D38, D49) and
    /// the XML docs of the public members promise.
    /// </summary>
    public sealed class R2ApiReviewTests
    {

        /// <summary>
        /// §3.6 (RunOptions.Observer), §6.11 and §6.13: an observer may only read; "a write, a Run, or any other call that
        /// changes a scope", and every "structural call", throws InvalidOperationException("Not allowed inside a run
        /// observer"). CreateChild adds a child to the running scope's tree.
        /// </summary>
        [Fact]
        public void CreateChildFromARunObserverIsRefused()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            var scope = t.Root.CreateChild(t.Outer);
            scope.Assume(t.Fact.Of("a"));
            Exception? refused = null;
            Scope? made = null;
            scope.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = _ =>
                {
                    if (made is not null || refused is not null)
                        return;

                    try
                    {
                        made = scope.CreateChild(t.Inner);
                    }
                    catch (Exception exception)
                    {
                        refused = exception;
                    }
                },
            });

            Assert.Null(made);
            var error = Assert.IsType<InvalidOperationException>(refused);
            Assert.Equal("Not allowed inside a run observer", error.Message);
        }

        /// <summary>
        /// §6.11: a call from a run observer that changes a scope throws InvalidOperationException with the message
        /// "Not allowed inside a run observer", as Assume, Run and Scope.DefineRule do. Freeze changes the running scope.
        /// </summary>
        [Fact]
        public void FreezeFromARunObserverGivesTheObserverMessage()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            t.Root.Freeze();
            var scope = t.Root.CreateChild(t.Outer);
            scope.Assume(t.Fact.Of("a"));
            Exception? refused = null;
            scope.Run(Schedule.Saturate(t.Rules), new RunOptions { Observer = _ => refused ??= Record.Exception(scope.Freeze) });

            var error = Assert.IsType<InvalidOperationException>(refused);
            Assert.False(scope.IsFrozen);
            Assert.Equal("Not allowed inside a run observer", error.Message);
        }

        /// <summary>
        /// Scope.Any and Query's remarks, D16 and D49: a query writes nothing, so it never moves Version. A query whose
        /// scoped atoms read a writable tagged scope syncs that scope's mirror first (D48).
        /// </summary>
        [Fact]
        public void AScopedQueryOnAWritableTaggedScopeLeavesItsVersion()
        {
            var t = new ScopeTheory();
            var query = t.Graph.DefineQuery("scoped", q => q.When(t.Fact.In(t.Outer).Of(q.Var<string>("x"))));
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));
            var version = outer.Version;

            Assert.True(outer.Any(query));

            Assert.Equal(version, outer.Version);
        }

        /// <summary>
        /// Scope.Count, Extractor and D49: queries never invalidate an extractor, scoped queries included.
        /// </summary>
        [Fact]
        public void AScopedQueryOnAWritableTaggedScopeLeavesItsExtractors()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var leaf = graph.DefineConstructor<long, Node>("Leaf");
            var fact = graph.DefineRelation<string>("fact");
            var outerTag = graph.DefineScopeTag("outer");
            var query = graph.DefineQuery("scoped", q => q.When(fact.In(outerTag).Of(q.Var<string>("x"))));
            var outer = graph.Root.CreateChild(outerTag);
            var node = outer.Make(leaf.Of(1L));
            outer.Assume(fact.Of("a"));
            var extractor = outer.CreateExtractor();

            Assert.Equal(1, outer.Count(query));

            Assert.Equal("(Leaf 1)", extractor.Extract(node).ToString());
        }

        /// <summary>
        /// §6.13: an unresolved tag at a query is an InvalidOperationException "before any work". A query reading two tags,
        /// one of which does not resolve, must not sync the other's mirror first.
        /// </summary>
        [Fact]
        public void AnUnresolvedTagAtAQueryIsRefusedBeforeAnyWork()
        {
            var t = new ScopeTheory();
            var query = t.Graph.DefineQuery("two tags", q =>
            {
                var x = q.Var<string>("x");
                q.When(t.Fact.In(t.Outer).Of(x), t.Seen.In(t.Inner).Of(x));
            });
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));
            var version = outer.Version;

            Assert.Throws<InvalidOperationException>(() => outer.Count(query));

            Assert.Equal(version, outer.Version);
        }

        /// <summary>
        /// D38, §6.3 and §6.13: another graph's tag passed to Fact.In is an ArgumentException at that call, inside define.
        /// </summary>
        [Fact]
        public void FactInWithAnotherGraphsTagIsAnArgumentException()
        {
            var t = new ScopeTheory();
            var foreign = new EGraph().DefineScopeTag("outer");

            Assert.Throws<ArgumentException>(() => t.Rules.DefineRule("foreign tag", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x), Fact.In(foreign, x.Is("a")));
                r.Then(t.Seen.Of(x));
            }));
            Assert.Throws<ArgumentException>(() => t.Graph.DefineQuery("foreign tag", q =>
            {
                var x = q.Var<string>("x");
                q.When(t.Fact.Of(x), Fact.In(foreign, x.Is("a")));
            }));
        }

        /// <summary>
        /// §3.3 and §6.6.2: a write through a scoped view, or through Fact.In, is an ArgumentException in a host write, and
        /// nothing is written.
        /// </summary>
        [Fact]
        public void HostWritesThroughScopedViewsAreArgumentExceptions()
        {
            var t = new ScopeTheory();
            var outer = t.Root.CreateChild(t.Outer);
            var version = outer.Version;

            Assert.Throws<ArgumentException>(() => outer.Assume(t.Fact.Of("b"), t.Fact.In(t.Outer).Of("a")));
            Assert.Throws<ArgumentException>(() => outer.Assume(t.Fact.Of("b"), Fact.In(t.Outer, t.Fact.Of("a"))));
            Assert.Throws<ArgumentException>(() => outer.Assume(t.Best.In(t.Outer).Of("k").Is(1L)));

            Assert.Equal(version, outer.Version);
            Assert.False(outer.Check(t.Fact.Of("b")));
        }

        /// <summary>
        /// §6.13: a host write on a frozen scope is an InvalidOperationException; so is DefineRule, with §6.7's message.
        /// </summary>
        [Fact]
        public void AFrozenScopeRefusesWritesAndLocalRules()
        {
            var t = new ScopeTheory();
            t.Root.Freeze();
            var outer = t.Root.CreateChild(t.Outer);
            outer.Freeze();

            Assert.Throws<InvalidOperationException>(() => outer.Assume(t.Fact.Of("a")));
            Assert.Throws<InvalidOperationException>(() => outer.Write(w => w.Assume(t.Fact.Of("a"))));
            var error = Assert.Throws<InvalidOperationException>(() => outer.DefineRule(t.Rules, "late", r => r.Then(t.Seen.Of("a"))));
            Assert.Equal("Scope 'root/outer#1' is frozen; its local rules are fixed", error.Message);
        }

        /// <summary>
        /// D40 and §3.1: the identity members of a disposed, released scope still answer; only use throws.
        /// </summary>
        [Fact]
        public void TheIdentityOfAReleasedScopeStillAnswers()
        {
            var t = new ScopeTheory();
            var child = t.Root.CreateChild(t.Outer);
            child.Dispose();

            Assert.True(child.IsDisposed);
            Assert.Same(t.Root, child.Parent);
            Assert.Same(t.Outer, child.Tag);
            Assert.Equal("root/outer#1", child.Path);
            Assert.False(child.IsFaulted);
            Assert.Null(child.Failure);
            Assert.False(child.IsFrozen);
            Assert.Empty(child.Children);
            Assert.Empty(child.LocalRules);
            Assert.Throws<ObjectDisposedException>(() => child.Check(t.Fact.Of("a")));
            Assert.Throws<ObjectDisposedException>(() => child.CreateChild());
            _ = child.Version;
        }

        /// <summary>
        /// §2.4, §3.8 and §6.12: extractors over a frozen scope are thread-safe. The library's other objects either are
        /// thread-safe or fail fast on a second thread (D47); one extractor shared by 8 threads over a frozen scope must
        /// do one or the other: a call either extracts the right term or fails fast as in use on another thread, never
        /// returns a wrong answer ("no finite term" included) or throws an unrelated exception.
        /// </summary>
        [Fact]
        public void OneExtractorSharedByEightThreadsOverAFrozenScope()
        {
            var t = new NodeTheory();
            var terms = new Node[400];
            for (var i = 0; i < terms.Length; i++)
                terms[i] = t.Root.Make(t.Wrap.Of(t.Add.Of(t.Lit.Of(i), t.Wrap.Of(t.Lit.Of(i + 1L)))));

            t.Root.Freeze();
            var extractor = t.Root.CreateExtractor();
            var rendered = new string?[8][];
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            using var start = new System.Threading.ManualResetEventSlim();
            RunOnThreads(rendered.Length, k =>
            {
                rendered[k] = new string?[terms.Length];
                start.Wait(TimeSpan.FromSeconds(30));
                for (var j = 0; j < terms.Length; j++)
                {
                    var i = (j * 7 + k * 53) % terms.Length;
                    try
                    {
                        rendered[k][i] = extractor.Extract(terms[i]).ToString();
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
            }, start.Set);

            var wrong = 0;
            for (var i = 0; i < terms.Length; i++)
                foreach (var r in rendered)
                    if (r![i] is { } text && text != $"(Wrap (Add (Lit {i}) (Wrap (Lit {i + 1}))))")
                        wrong++;

            var unrelated = 0;
            foreach (var error in errors)
                if (error.GetType() != typeof(InvalidOperationException) || error.Message.Contains("another thread", StringComparison.Ordinal) == false)
                    unrelated++;

            Assert.True(wrong == 0 && unrelated == 0,
                $"{wrong} wrong terms; {errors.Count} failed calls; {unrelated} unrelated exceptions: {string.Join("; ", System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(errors, e => e.GetType().Name + ": " + e.Message)))}");
        }

        /// <summary>
        /// §2.4 and §6.12: reads of a frozen scope take no guard and are thread-safe: Check, Get, Find, AreEqual,
        /// CountRows, Rows and CreateChild from 8 threads give the serial answers.
        /// </summary>
        [Fact]
        public void ReadsOfAFrozenScopeFromEightThreads()
        {
            var t = new NodeTheory();
            var terms = new Node[200];
            for (var i = 0; i < terms.Length; i++)
            {
                terms[i] = t.Root.Make(t.Wrap.Of(t.Lit.Of(i)));
                t.Root.Assume(t.Num.Of(i), t.Tag.Of(terms[i]));
            }

            t.Root.Freeze();
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            RunOnThreads(8 * 20, k =>
            {
                var i = k % terms.Length;
                if (t.Root.Check(t.Num.Of(i), t.Tag.Of(terms[i])) == false)
                    failures.Add($"check {i}");
                if (t.Root.Get(t.Wrap.Of(t.Lit.Of(i))) != t.Root.Find(terms[i]))
                    failures.Add($"get {i}");
                if (t.Root.AreEqual(terms[i], t.Wrap.Of(t.Lit.Of(i))) == false)
                    failures.Add($"equal {i}");
                if (t.Root.CountRows(t.Num) != terms.Length || t.Root.Rows(t.Tag).Count != terms.Length)
                    failures.Add($"rows {k}");
                using var child = t.Root.CreateChild();
                child.Assume(t.Num.Of(-1L - k));
                if (child.CountRows(t.Num) != terms.Length + 1)
                    failures.Add($"child {k}");
            }, null);

            Assert.Empty(failures);
            Assert.Empty(t.Root.Children);
        }

        /// <summary>
        /// Pins what the spec leaves open. §6.7 says "a Dispose called from a run observer is deferred until the run ends,
        /// through the guard", but its TryRelease defers only for a guard another thread holds: a child of the running
        /// scope disposed from the observer is released at once, mid-run.
        /// </summary>
        [Fact]
        public void ADisposeFromARunObserverReleasesAChildAtOnce()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            var scope = t.Root.CreateChild(t.Outer);
            var child = scope.CreateChild(t.Inner);
            scope.Assume(t.Fact.Of("a"));
            bool? releasedInObserver = null;
            scope.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = _ =>
                {
                    if (releasedInObserver is not null)
                        return;

                    child.Dispose();
                    releasedInObserver = child.Engine.IsDisposed;
                },
            });

            Assert.True(releasedInObserver);
        }

        /// <summary>
        /// Pins what the spec leaves open: an Until fact whose tag does not resolve is refused before any work, as an
        /// ArgumentException ("an Until fact the scope cannot read", §6.13), not the InvalidOperationException of "an
        /// unresolved tag at Run".
        /// </summary>
        [Fact]
        public void AnUntilFactWithAnUnresolvedTagIsAnArgumentException()
        {
            var t = new ScopeTheory();
            var scope = t.Root.CreateChild(t.Outer);

            Assert.Throws<ArgumentException>(() => scope.Run(Schedule.Until(t.Rules, t.Fact.In(t.Inner).Of("a"))));
        }

        /// <summary>
        /// D6 and §6.12: from R2, a rule or query definition whose first scoped read creates a mirror is a table
        /// definition, so from another thread while the writable root is in use it throws InvalidOperationException. The
        /// docs of Ruleset.DefineRule and EGraph.DefineQuery say "Any thread, any time" and list no such exception.
        /// </summary>
        [Fact]
        public void AFirstScopedReadFromAnotherThreadWhileTheRootRunsIsRefused()
        {
            using var run = new BlockingRun();
            var tag = run.Graph.DefineScopeTag("outer");
            run.Start();

            var rule = Record.Exception(() => run.Rules.DefineRule("scoped", r =>
            {
                var x = r.Var<long>("x");
                r.When(run.Seed.In(tag).Of(x));
                r.Then(run.Done.Of(x));
            }));
            var query = Record.Exception(() => run.Graph.DefineQuery("scoped", q => q.When(run.Done.In(tag).Of(q.Var<long>("x")))));
            run.Finish();

            Assert.IsType<InvalidOperationException>(rule);
            Assert.IsType<InvalidOperationException>(query);
        }

        /// <summary>
        /// Runs <paramref name="body"/> for 0..<paramref name="count"/>-1 on 8 dedicated threads (never pool threads, so the
        /// suite's pool-sensitive tests running alongside are not starved), calling <paramref name="started"/> once every
        /// thread is running, and waits for them.
        /// </summary>
        static void RunOnThreads(int count, Action<int> body, Action? started)
        {
            var next = -1;
            var threads = new System.Threading.Thread[Math.Min(8, count)];
            for (var n = 0; n < threads.Length; n++)
            {
                threads[n] = new System.Threading.Thread(() =>
                {
                    for (var k = System.Threading.Interlocked.Increment(ref next); k < count; k = System.Threading.Interlocked.Increment(ref next))
                        body(k);
                });
                threads[n].Start();
            }

            started?.Invoke();
            foreach (var thread in threads)
                thread.Join();
        }

    }

}
