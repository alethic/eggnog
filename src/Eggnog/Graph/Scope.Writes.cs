using System;

namespace Eggnog
{

    public sealed partial class Scope
    {

        /// <summary>
        /// Builds a ground term with get-or-add and returns its class, canonical after the write. Constants are interned.
        /// One atomic batch.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="term">The term.</param>
        /// <returns>The class, usable in this scope and its descendants until this scope is disposed.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">A subterm reads a function (<c>Get</c> it first), computes, is a variable, or
        /// holds a class value this scope cannot use.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen (write in a child of it) or faulted, or in use on
        /// another thread; or the call comes from a run observer or a user delegate the engine invoked.</exception>
        /// <exception cref="RuleEvaluationException">A merge failed; the scope is faulted.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, before the call
        /// or through it, or lies under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of ids or epochs (<see cref="EGraph.Usage"/>), and the
        /// scope is not faulted. When the ids ran out, or the epochs before the merge, nothing is written. When the epochs
        /// ran out in the rebuild after the merge, the write stays, and if that rebuild left work undone the scope's reads
        /// throw the same way (<see cref="Check"/>). Every later call that needs an id or an epoch throws the same
        /// way.</exception>
        public TSort Make<TSort>(ConstructorTerm<TSort> term) where TSort : struct, ISort<TSort>
        {
            ArgumentNullException.ThrowIfNull(term);
            EnterWrite();
            try
            {
                ThrowIfStopped();
                var batch = new HostBatch(this);
                var id = batch.PlanTerm(term);
                Commit(batch);

                // A case whose write made it contradictory stopped: the class names nothing in a scope with no model.
                ThrowIfStopped();
                return TSort.FromId(new ClassId(Engine.Find(id), Origin));
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Writes ground facts as one atomic batch: relation rows; <c>F.Of(k).Is(v)</c> (merged by F's lattice);
        /// <c>C.Of(k).Is(x)</c> (unions outputs); <c>a.Is(b)</c> of a sort (a union). Facts that hold already write
        /// nothing, so they move neither <see cref="Version"/> nor any extractor. Nothing is written when a fact fails. On a
        /// case whose contradiction stopped it, or a scope under one, the call does nothing.
        /// </summary>
        /// <param name="facts">The facts.</param>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="FactConflictException">A function without a lattice holds another value, or a union the facts
        /// make would merge two of its keys that hold different values.</exception>
        /// <exception cref="ArgumentException">A fact is a value equality that is not a function write, a filter, an
        /// existence, or reads or computes in a term; or it writes through a scoped view (<c>T.In(tag)</c>) or a
        /// <see cref="Fact.In"/>, which are read-only.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen (write in a child of it) or faulted, or in use on
        /// another thread; or the call comes from a run observer or a user delegate the engine invoked.</exception>
        /// <exception cref="RuleEvaluationException">A lattice's merge failed: while checking a fact (nothing is written,
        /// the scope is not faulted) or while merging, the catch-up the call makes first included (the scope is
        /// faulted).</exception>
        /// <exception cref="OverflowException">The graph has run out of ids or epochs (<see cref="EGraph.Usage"/>), and the
        /// scope is not faulted. When the ids ran out, or the epochs before the merge, nothing is written. When the epochs
        /// ran out in the rebuild after the merge, the write stays, and if that rebuild left work undone the scope's reads
        /// throw the same way (<see cref="Check"/>). Every later call that needs an id or an epoch throws the same
        /// way.</exception>
        public void Assume(params Fact[] facts)
        {
            ArgumentNullException.ThrowIfNull(facts);
            EnterWrite();
            try
            {
                // A stopped case, or a scope under one, has no model: a fact more changes nothing.
                if (IsStopped)
                    return;

                var batch = new HostBatch(this);
                foreach (var fact in facts)
                    batch.PlanFact(fact ?? throw new ArgumentNullException(nameof(facts), "A fact is null."));

                Commit(batch);
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Unions two classes as one atomic batch.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="a">One class.</param>
        /// <param name="b">The other class.</param>
        /// <returns><c>true</c> where the classes were distinct before the call; <c>false</c> on a case whose contradiction
        /// stopped it, or a scope under one.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">A class value is not usable in this scope.</exception>
        /// <exception cref="FactConflictException">The union would merge two keys of a function without a lattice that
        /// hold different values, directly or through the congruences it causes; nothing is written.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen (write in a child of it) or faulted, or in use on
        /// another thread; or the call comes from a run observer or a user delegate the engine invoked.</exception>
        /// <exception cref="RuleEvaluationException">A merge failed (the scope is faulted); or a lattice merge threw in the
        /// catch-up the call makes first, here or in a writable ancestor: the scope is faulted, and so is the
        /// ancestor.</exception>
        /// <exception cref="OverflowException">The graph has run out of ids or epochs (<see cref="EGraph.Usage"/>), and the
        /// scope is not faulted. When the ids ran out, or the epochs before the merge, nothing is written. When the epochs
        /// ran out in the rebuild after the merge, the write stays, and if that rebuild left work undone the scope's reads
        /// throw the same way (<see cref="Check"/>). Every later call that needs an id or an epoch throws the same
        /// way.</exception>
        public bool Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>
        {
            EnterWrite();
            try
            {
                // A stopped case, or a scope under one, has no model: no two of its classes are distinct there.
                if (IsStopped)
                    return false;

                var x = ClassChecks.Require(this, a.Id, nameof(a));
                var y = ClassChecks.Require(this, b.Id, nameof(b));
                var distinct = Engine.Find(x) != Engine.Find(y);
                var batch = new HostBatch(this);
                batch.PlanUnion(x, y);
                Commit(batch);
                return distinct;
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Makes several writes one atomic batch: each call is checked as it is made, and the batch is staged,
        /// flushed and rebuilt once, after <paramref name="write"/> returns. If <paramref name="write"/> throws or a call is
        /// rejected, nothing is written and the class values the batch's <c>Make</c> calls returned are invalid.
        /// </summary>
        /// <remarks>
        /// An exception <paramref name="write"/> lets escape propagates. A rejected call whose exception
        /// <paramref name="write"/> caught still rejects the batch: <see cref="Write"/> then returns without writing
        /// anything, and <see cref="Version"/> does not move. A <see cref="ScopeWriter"/> call from another thread is
        /// refused and rejects the batch too. A conflict a merge of the batch would cause is found as the call that
        /// causes it is made. While the batch is open, the scopes it reads through (its writable ancestors) refuse
        /// writes and runs, which would change what the batch's calls were checked against. Other calls of the thread
        /// domain are allowed, reads and queries of the ancestors and writes into descendants and siblings included; the
        /// batch is brought up to date with them before each of its calls and before it commits, without moving
        /// <see cref="Version"/>.
        /// </remarks>
        /// <param name="write">Makes the batch's calls through its <see cref="ScopeWriter"/>.</param>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen (write in a child of it) or faulted, or in use on
        /// another thread; or the call comes from a run observer or a user delegate the engine invoked.</exception>
        /// <exception cref="RuleEvaluationException">A merge failed; the scope is faulted.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of ids or epochs (<see cref="EGraph.Usage"/>), in a
        /// call of the batch or as it commits, and the scope is not faulted. When the ids ran out, or the epochs before
        /// the merge, the batch writes nothing. When the epochs ran out in the rebuild after the merge, the batch stays
        /// written, and if that rebuild left work undone the scope's reads throw the same way (<see cref="Check"/>). Every
        /// later call that needs an id or an epoch throws the same way.</exception>
        public void Write(Action<ScopeWriter> write)
        {
            ArgumentNullException.ThrowIfNull(write);
            EnterWrite();
            try
            {
                ThrowIfStopped();
                var batch = new HostBatch(this);
                var writer = new ScopeWriter(this, batch);
                openBatch = batch;
                CountOpenBatch(1);
                try
                {
                    write(writer);
                }
                finally
                {
                    CountOpenBatch(-1);
                    openBatch = null;
                    writer.Close();
                }

                // A call was rejected and the callback caught it: the batch writes nothing, and its origin never commits.
                if (batch.IsRejected)
                    return;

                // The callback may have made calls in this scope's domain that left it behind its ancestors without
                // changing what the batch read (a mirror sync before an ancestor's query). The catch-up is made here,
                // left out of Version as at the entry, so that the commit's flush makes only the batch's changes.
                if (batch.IsEmpty == false)
                {
                    Runtime.TakeFailure();
                    try
                    {
                        CatchUp();
                    }
                    catch (Relations.PanicException panic)
                    {
                        throw FaultWith(panic);
                    }
                }

                Commit(batch);
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Commits a planned batch: stages and flushes it unless it is empty, and commits its origin. A failure a merge
        /// recorded faults the scope.
        /// </summary>
        /// <exception cref="RuleEvaluationException">A merge failed.</exception>
        void Commit(HostBatch batch)
        {
            if (batch.IsEmpty == false)
            {
                Runtime.TakeFailure();
                try
                {
                    batch.Commit();
                }
                catch (Relations.PanicException panic)
                {
                    throw FaultWith(panic);
                }
            }

            batch.Origin.Committed = true;
        }

    }

}
