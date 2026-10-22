using System;

namespace Eggnog
{

    /// <summary>
    /// The writes of one <see cref="Scope.Write"/> batch, egglog's commands one after the other: each call is checked
    /// as it is made, and the batch is staged, flushed and rebuilt after the callback returns, and before a call that
    /// needs an earlier call's merge of classes rebuilt. Reads are not available; the scope's own members refuse calls
    /// while the batch is open. Valid only during the batch (<see cref="ObjectDisposedException"/> afterwards).
    /// </summary>
    /// <remarks>
    /// Every call enters the scope's thread domain for its duration: from the thread that called
    /// <see cref="Scope.Write"/> it is re-entrant, and from any other thread it throws
    /// <see cref="InvalidOperationException"/> instead of interleaving with the batch, which it rejects:
    /// <see cref="Scope.Write"/> then writes nothing more. In any other call that throws, the fact that failed keeps
    /// what it staged before it failed, as egglog's failed command does: the terms its calls made and what its primitives
    /// wrote, but not its own write; and the batch's other facts stand, the call's own earlier ones included. The batch flushes before a call that needs what an earlier
    /// call's merge of classes makes, as egglog's next command runs after the last one's rebuild (see
    /// <see cref="Scope.Write"/>). While the batch is open, the scope's writable ancestors refuse writes and runs.
    /// </remarks>
    public sealed class ScopeWriter
    {

        readonly HostBatch batch;
        volatile bool closed;

        /// <summary>
        /// Initializes a writer for <paramref name="batch"/>.
        /// </summary>
        /// <param name="scope">The scope written.</param>
        /// <param name="batch">The batch.</param>
        internal ScopeWriter(Scope scope, HostBatch batch)
        {
            Scope = scope;
            this.batch = batch;
        }

        /// <summary>
        /// Gets the scope written.
        /// </summary>
        public Scope Scope { get; }

        /// <summary>
        /// Adds ground facts to the batch (see <see cref="Scope.Assume"/>).
        /// </summary>
        /// <param name="facts">The facts.</param>
        /// <exception cref="ArgumentException">A fact is not a write.</exception>
        /// <exception cref="FactConflictException">A function without a lattice would get a second value, from the facts
        /// or from a merge of the batch, two classes of a function of a sort that the batch's merges make meet included;
        /// the fact that fails keeps the terms it made, but not its write.</exception>
        /// <exception cref="InvalidOperationException">The call came from another thread, or from a user delegate the
        /// engine invoked.</exception>
        /// <exception cref="ObjectDisposedException">The batch has ended.</exception>
        public void Assume(params Fact[] facts)
        {
            Enter();
            try
            {
                ArgumentNullException.ThrowIfNull(facts);
                foreach (var fact in facts)
                    batch.PlanFact(fact ?? throw new ArgumentNullException(nameof(facts), "A fact is null."));
            }
            finally
            {
                Scope.Guard.Exit();
            }
        }

        /// <summary>
        /// Gets the class of a term: an existing class, or a fresh id that names the new class once the batch commits.
        /// Usable in later calls of the same batch; use <see cref="Scope.Find{TSort}(TSort)"/> for the canonical member
        /// afterwards.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="term">The term.</param>
        /// <returns>The class, valid in this batch and, once it commits, in the scope.</returns>
        /// <exception cref="ArgumentException">A subterm reads a function, computes, is a variable, or holds a class
        /// value this scope cannot use.</exception>
        /// <exception cref="InvalidOperationException">The call came from another thread, or from a user delegate the
        /// engine invoked.</exception>
        /// <exception cref="ObjectDisposedException">The batch has ended.</exception>
        public TSort Make<TSort>(ConstructorTerm<TSort> term) where TSort : struct, ISort<TSort>
        {
            Enter();
            try
            {
                ArgumentNullException.ThrowIfNull(term);
                return TSort.FromId(new ClassId(batch.PlanTerm(term), batch.Origin));
            }
            finally
            {
                Scope.Guard.Exit();
            }
        }

        /// <summary>
        /// Adds a union to the batch.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="a">One class.</param>
        /// <param name="b">The other class.</param>
        /// <exception cref="ArgumentException">A class value is not usable in this scope.</exception>
        /// <exception cref="FactConflictException">The union would merge two keys of a function without a lattice that
        /// hold different values, or two different classes; the union writes nothing.</exception>
        /// <exception cref="InvalidOperationException">The call came from another thread, or from a user delegate the
        /// engine invoked.</exception>
        /// <exception cref="ObjectDisposedException">The batch has ended.</exception>
        public void Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>
        {
            Enter();
            try
            {
                var x = ClassChecks.Require(Scope, a.Id, nameof(a), batch.Origin);
                var y = ClassChecks.Require(Scope, b.Id, nameof(b), batch.Origin);
                batch.PlanUnion(x, y);
            }
            finally
            {
                Scope.Guard.Exit();
            }
        }

        /// <summary>
        /// Adds the subsumption of a ground constructor term to the batch (see <see
        /// cref="Scope.Subsume{TSort}(ConstructorTerm{TSort})"/>). A term the batch makes, before or after this call,
        /// is made subsumed.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="term">The term: an application of a constructor defined <see
        /// cref="TableOptions.Subsumable"/>.</param>
        /// <exception cref="ArgumentException">The constructor is not subsumable, or a subterm reads a function,
        /// computes, is a variable, or holds a class value this scope cannot use.</exception>
        /// <exception cref="InvalidOperationException">The call came from another thread, or from a user delegate the
        /// engine invoked.</exception>
        /// <exception cref="ObjectDisposedException">The batch has ended.</exception>
        public void Subsume<TSort>(ConstructorTerm<TSort> term) where TSort : struct, ISort<TSort>
        {
            Enter();
            try
            {
                ArgumentNullException.ThrowIfNull(term);
                batch.PlanSubsume(term);
            }
            finally
            {
                Scope.Guard.Exit();
            }
        }

        /// <summary>
        /// Adds the subsumption of ground relation rows to the batch (see <see cref="Scope.Subsume(Fact[])"/>). A row
        /// the batch inserts, before or after this call, is inserted subsumed.
        /// </summary>
        /// <param name="rows">The rows, of relations defined <see cref="TableOptions.Subsumable"/>.</param>
        /// <exception cref="ArgumentException">A fact is not a relation row of the scope written, a relation is not
        /// subsumable, or a term in a row is not ground or holds a class value this scope cannot use.</exception>
        /// <exception cref="InvalidOperationException">The call came from another thread, or from a user delegate the
        /// engine invoked.</exception>
        /// <exception cref="ObjectDisposedException">The batch has ended.</exception>
        public void Subsume(params Fact[] rows)
        {
            Enter();
            try
            {
                ArgumentNullException.ThrowIfNull(rows);
                foreach (var row in rows)
                    batch.PlanSubsume(row ?? throw new ArgumentNullException(nameof(rows), "A fact is null."));
            }
            finally
            {
                Scope.Guard.Exit();
            }
        }

        /// <summary>
        /// Ends the batch's calls.
        /// </summary>
        internal void Close() => closed = true;

        /// <summary>
        /// Enters the scope's thread domain for one call and brings the scope up to date with its ancestors. A
        /// refusal while the batch is open rejects the batch; a call after the batch ended changes nothing. The batch's end
        /// is checked again once the domain is entered: the batch's thread closes the writer before it leaves the domain, so
        /// a call from another thread that waited out the batch sees it ended instead of planning into a committed batch.
        /// Cost: beyond the guard, two reads of the change stamp per writable scope from here up to the first frozen one.
        /// </summary>
        /// <exception cref="InvalidOperationException">The call came from another thread, or from a user delegate the
        /// engine invoked.</exception>
        /// <exception cref="ObjectDisposedException">The batch has ended.</exception>
        void Enter()
        {
            if (closed)
            {
                EngineCallMarker.ThrowIfActive();
                throw new ObjectDisposedException(nameof(ScopeWriter), "The Write batch has ended.");
            }

            try
            {
                EngineCallMarker.ThrowIfActive();
                Scope.EnterGuard();
            }
            catch
            {
                batch.Reject();
                throw;
            }

            if (closed)
            {
                Scope.Guard.Exit();
                throw new ObjectDisposedException(nameof(ScopeWriter), "The Write batch has ended.");
            }

            // A call the callback made in the domain between two of the batch's calls (an ancestor's query syncing its
            // mirrors, say) may have left the scope behind its ancestors; writes and runs that would change what the batch
            // read are refused. The catch-up is left out of Version.
            try
            {
                Scope.CatchUp();
            }
            catch
            {
                Scope.Guard.Exit();
                batch.Reject();
                throw;
            }
        }

    }

}
