using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    /// <summary>
    /// Runs a body for each of several children of frozen contexts on concurrent threads, within the e-graph's
    /// <see cref="EGraph.ParallelismBudget"/> (colored-contexts spec §10.9, C9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Soundness. A child of a frozen context has a thread domain of its own (§8.3), and so does every descendant created
    /// under it while it is not frozen; the frozen ancestors they share are immutable. So children run concurrently exactly
    /// when each one's parent is frozen and none is an ancestor of another (two children in one line of descent would
    /// share the lower one's domain or read a context the other writes). <see cref="Run(IReadOnlyList{EGraphContext}, Action{EGraphContext, int})"/>
    /// checks both before running anything.
    /// </para>
    /// <para>
    /// Budget. Each body runs under a lease of its child's degree of parallelism (<see cref="EGraphContext.Parallelism"/>,
    /// capped at the budget's total): a child of a frozen context is serial by default and leases one thread, and a child
    /// given wider heuristics through <see cref="ContextOptions.Parallelism"/> leases more, for the parallel rule sets and
    /// rebuilds it may run. <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> runs the bodies on
    /// <c>max(1, Total / max child DOP)</c> threads, the calling thread among them, so the leases rarely wait; when they
    /// do (another run shares the budget), a body waits for threads before it starts.
    /// </para>
    /// <para>
    /// Nesting. A body may itself call <c>Run</c> on grandchildren (each frozen child's children). The nested call runs its
    /// bodies serially on the calling thread, under the lease that thread already holds: leasing again could wait forever
    /// once the outer run has leased the whole budget.
    /// </para>
    /// <para>
    /// Errors. As with <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>, once a body throws no new body
    /// starts and the running ones finish. A single exception is rethrown as itself, with its stack trace; several are
    /// thrown as the <see cref="AggregateException"/>. Each child's own state follows its operations' rules: an exception
    /// escaping a child's write or run faults that child only (X12).
    /// </para>
    /// <para>
    /// Cost. O(children + the sum of their depths) to check the arguments, one lease per body, and the thread pool's cost
    /// to schedule them; nothing per row.
    /// </para>
    /// </remarks>
    internal static class ConcurrentChildren
    {

        /// <summary>
        /// The number of leases the current thread holds through <see cref="Run(IReadOnlyList{EGraphContext}, Action{EGraphContext, int})"/>;
        /// a nested run on a thread holding one runs serially under it.
        /// </summary>
        [ThreadStatic]
        static int leasesHeld;

        /// <summary>
        /// Runs <paramref name="body"/> once for each of <paramref name="children"/>, concurrently.
        /// </summary>
        /// <param name="children">The children; see <see cref="Run(IReadOnlyList{EGraphContext}, Action{EGraphContext, int})"/>.</param>
        /// <param name="body">The work for one child; it may use that child (and contexts it creates) freely.</param>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">See <see cref="Run(IReadOnlyList{EGraphContext}, Action{EGraphContext, int})"/>.</exception>
        /// <exception cref="ObjectDisposedException">A child is disposed.</exception>
        /// <exception cref="InvalidOperationException">See <see cref="Run(IReadOnlyList{EGraphContext}, Action{EGraphContext, int})"/>.</exception>
        public static void Run(IReadOnlyList<EGraphContext> children, Action<EGraphContext> body)
        {
            ArgumentNullException.ThrowIfNull(body);
            Run(children, (child, _) => body(child));
        }

        /// <summary>
        /// Runs <paramref name="body"/> once for each of <paramref name="children"/>, concurrently, passing the child and its
        /// index in <paramref name="children"/>.
        /// </summary>
        /// <param name="children">The children: each one's parent frozen, none an ancestor of another, all of one e-graph,
        /// none listed twice. An empty list runs nothing.</param>
        /// <param name="body">The work for one child; it may use that child (and contexts it creates) freely, and must not
        /// use another child of the list.</param>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">A child is <c>null</c>, is listed twice, or belongs to another e-graph than
        /// the first.</exception>
        /// <exception cref="ObjectDisposedException">A child is disposed.</exception>
        /// <exception cref="InvalidOperationException">A child is a root, or its parent is not frozen (it would share a
        /// thread domain with its parent and its siblings); or a child is an ancestor of another.</exception>
        public static void Run(IReadOnlyList<EGraphContext> children, Action<EGraphContext, int> body)
        {
            ArgumentNullException.ThrowIfNull(children);
            ArgumentNullException.ThrowIfNull(body);
            var count = children.Count;
            if (count == 0)
                return;

            var budget = Check(children, out var threads, out var widest);
            if (count == 1 || leasesHeld > 0)
            {
                RunSerially(children, body, budget, threads);
                return;
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, budget.Total / widest) };
            try
            {
                Parallel.For(0, count, options, i => RunOne(children[i], i, body, budget, threads[i]));
            }
            catch (AggregateException e) when (e.InnerExceptions.Count == 1)
            {
                ExceptionDispatchInfo.Throw(e.InnerExceptions[0]);
            }
        }

        /// <summary>
        /// Checks the children (see <see cref="Run(IReadOnlyList{EGraphContext}, Action{EGraphContext, int})"/>) and
        /// returns the e-graph's budget, each child's lease size and the largest one.
        /// </summary>
        static ParallelismBudget Check(IReadOnlyList<EGraphContext> children, out int[] threads, out int widest)
        {
            var count = children.Count;
            var owner = (children[0] ?? throw NullChild(0)).Owner;
            var budget = owner.ParallelismBudget;
            var listed = new HashSet<EGraphContext>(count, ReferenceEqualityComparer.Instance);
            threads = new int[count];
            widest = 1;
            for (var i = 0; i < count; i++)
            {
                var child = children[i] ?? throw NullChild(i);
                if (ReferenceEquals(child.Owner, owner) == false)
                    throw new ArgumentException($"The child at {i} belongs to another e-graph than the first child.", nameof(children));

                if (listed.Add(child) == false)
                    throw new ArgumentException($"The child at {i} is listed twice; a context can run on one thread at a time.", nameof(children));

                child.Context.ThrowIfDisposed();
                if (child.Parent is not { IsFrozen: true })
                    throw new InvalidOperationException($"The context at {i} is {(child.IsRoot ? "the root" : "a child of a context that is not frozen")}; only children of frozen contexts have thread domains of their own and may run concurrently.");

                var dop = Math.Min(child.Parallelism.DegreeOfParallelism, budget.Total);
                threads[i] = dop;
                widest = Math.Max(widest, dop);
            }

            for (var i = 0; i < count; i++)
                for (var up = children[i].Parent; up is not null; up = up.Parent)
                    if (listed.Contains(up))
                        throw new InvalidOperationException($"The child at {i} descends from another child of the list; a context and its descendants cannot run concurrently.");

            return budget;
        }

        /// <summary>
        /// Runs every body in order on the calling thread: a single child, or a run nested in a body that holds a lease
        /// already (which covers it).
        /// </summary>
        static void RunSerially(IReadOnlyList<EGraphContext> children, Action<EGraphContext, int> body, ParallelismBudget budget, int[] threads)
        {
            if (leasesHeld > 0)
            {
                for (var i = 0; i < children.Count; i++)
                    body(children[i], i);

                return;
            }

            for (var i = 0; i < children.Count; i++)
                RunOne(children[i], i, body, budget, threads[i]);
        }

        /// <summary>
        /// Runs one body under a lease of <paramref name="threads"/> threads.
        /// </summary>
        static void RunOne(EGraphContext child, int index, Action<EGraphContext, int> body, ParallelismBudget budget, int threads)
        {
            using var lease = budget.Lease(threads);
            leasesHeld++;
            try
            {
                body(child, index);
            }
            finally
            {
                leasesHeld--;
            }
        }

        /// <summary>
        /// Builds the exception for a null child at <paramref name="index"/> of the list.
        /// </summary>
        static ArgumentException NullChild(int index) => new($"The child at {index} is null.", "children");

    }

}
