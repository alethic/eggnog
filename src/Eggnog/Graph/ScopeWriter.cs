using System;

namespace Eggnog
{

    /// <summary>
    /// The writes of one <see cref="Scope.Write"/> batch (D30): each call is checked as it is made, and the batch is
    /// staged, flushed and rebuilt once, after the callback returns. Reads are not available; the scope's own members
    /// refuse calls while the batch is open. Valid only during the batch (<see cref="ObjectDisposedException"/>
    /// afterwards).
    /// </summary>
    /// <remarks>
    /// Every call enters the scope's thread domain for its duration (D47): from the thread that called
    /// <see cref="Scope.Write"/> it is re-entrant, and from any other thread it throws
    /// <see cref="InvalidOperationException"/> instead of interleaving with the batch. A call that throws, for any reason,
    /// rejects the whole batch: <see cref="Scope.Write"/> then writes nothing, even when the callback caught the
    /// exception, and the classes the batch's <see cref="Make{TSort}"/> calls returned stay invalid (D39).
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
        /// or from a merge of the batch (D32).</exception>
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
            catch
            {
                batch.Reject();
                throw;
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
        /// <returns>The class, valid in this batch and, once it commits, in the scope (D39).</returns>
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
            catch
            {
                batch.Reject();
                throw;
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
        /// <exception cref="ArgumentException">A class value is not usable in this scope (D39).</exception>
        /// <exception cref="FactConflictException">The union would merge two keys of a function without a lattice that
        /// hold different values (D32).</exception>
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
            catch
            {
                batch.Reject();
                throw;
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
        /// Enters the scope's thread domain for one call (D47). A refusal while the batch is open rejects the batch; a
        /// call after the batch ended changes nothing.
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
                Scope.Guard.Enter(Scope);
            }
            catch
            {
                batch.Reject();
                throw;
            }
        }

    }

}
