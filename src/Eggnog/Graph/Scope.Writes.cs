using System;
using System.Runtime.ExceptionServices;

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
        /// <returns>The class, usable in this scope and its descendants; once this scope is disposed it refuses new calls
        /// itself, and the class stays usable in its live descendants.</returns>
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
                uint id;
                try
                {
                    id = batch.PlanTerm(term);
                }
                catch
                {
                    // egglog's failed command keeps the terms its calls made before it failed.
                    CommitBefore(batch);
                    throw;
                }

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
        /// Writes ground facts as one batch, egglog's commands one after the other (see <see cref="Write"/>): relation
        /// rows; <c>F.Of(k).Is(v)</c> (merged by F's lattice); <c>C.Of(k).Is(x)</c> (unions outputs); <c>a.Is(b)</c> of a
        /// sort (a union); a call run for its effect alone (<see cref="Fact.Exists"/>, egglog's bare action). Facts that
        /// hold already write nothing, so they move neither <see cref="Version"/> nor any extractor. A fact that fails
        /// ends the call as egglog's failed command ends its program: what it staged before it failed stands, the terms
        /// its calls made and what its primitives wrote, while its own write does not happen; the facts before it stand,
        /// and the facts after it are not written. On a case whose contradiction stopped it, or a scope under one, the
        /// call does nothing.
        /// </summary>
        /// <param name="facts">The facts.</param>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="FactConflictException">A function without a lattice holds another value, or a union the facts
        /// make would merge two of its keys that hold different values.</exception>
        /// <exception cref="ArgumentException">A fact is a value equality that is not a function write or a filter; or it
        /// writes through a scoped view (<c>T.In(tag)</c>) or a <see cref="Fact.In"/>, which are read-only.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen (write in a child of it) or faulted, or in use on
        /// another thread; or the call comes from a run observer or a user delegate the engine invoked.</exception>
        /// <exception cref="RuleEvaluationException">A primitive had no result or threw, or a lattice's merge failed:
        /// while planning a fact (the fact keeps what it staged before, the facts before it stand, and the scope is not
        /// faulted) or while merging, the catch-up the call makes first included (the scope is faulted).</exception>
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
                try
                {
                    foreach (var fact in facts)
                        batch.PlanFact(fact ?? throw new ArgumentNullException(nameof(facts), "A fact is null."));
                }
                catch
                {
                    // egglog's commands before the one that failed stand.
                    CommitBefore(batch);
                    throw;
                }

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
        /// hold different values or classes, directly or through the congruences it causes, when the rebuild compares
        /// them; nothing is written.</exception>
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
                try
                {
                    batch.PlanUnion(x, y);
                }
                catch
                {
                    // egglog's failed command keeps what it staged before it failed.
                    CommitBefore(batch);
                    throw;
                }

                Commit(batch);
                return distinct;
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Subsumes a ground constructor term as one atomic batch: egglog's <c>(subsume (f args...))</c>. The term's
        /// row stays, and <see cref="Check"/>, <see cref="Get{T}"/> and <see cref="Rows"/> still find it, but rule
        /// bodies, queries and split rules no longer match it, and extraction skips it; its class keeps every other
        /// member. A term that does not exist yet is made, subsumed; its subterms are made as by <see
        /// cref="Make{TSort}"/>, not subsumed. Subsumption is sticky, and a term already subsumed writes nothing, so it
        /// moves neither <see cref="Version"/> nor any extractor. In a child, the subsumption shadows the ancestor's
        /// row: the ancestors and the siblings still match it. On a case whose contradiction stopped it, or a scope
        /// under one, the call does nothing.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="term">The term: an application of a constructor defined <see
        /// cref="TableOptions.Subsumable"/>.</param>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">The constructor is not subsumable, or a subterm reads a function
        /// (<c>Get</c> it first), computes, is a variable, or holds a class value this scope cannot use.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen (write in a child of it) or faulted, or in
        /// use on another thread; or the call comes from a run observer or a user delegate the engine
        /// invoked.</exception>
        /// <exception cref="RuleEvaluationException">A merge failed; the scope is faulted.</exception>
        /// <exception cref="OverflowException">The graph has run out of ids or epochs (<see cref="EGraph.Usage"/>), and
        /// the scope is not faulted. When the ids ran out, or the epochs before the merge, nothing is written. When the
        /// epochs ran out in the rebuild after the merge, the write stays, and if that rebuild left work undone the
        /// scope's reads throw the same way (<see cref="Check"/>). Every later call that needs an id or an epoch throws
        /// the same way.</exception>
        public void Subsume<TSort>(ConstructorTerm<TSort> term) where TSort : struct, ISort<TSort>
        {
            ArgumentNullException.ThrowIfNull(term);
            EnterWrite();
            try
            {
                // A stopped case, or a scope under one, has no model: a subsumption changes nothing.
                if (IsStopped)
                    return;

                var batch = new HostBatch(this);
                try
                {
                    batch.PlanSubsume(term);
                }
                catch
                {
                    // egglog's failed command keeps the terms its calls made before it failed.
                    CommitBefore(batch);
                    throw;
                }

                Commit(batch);
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Subsumes ground relation rows (<c>r.Of(a, b)</c>) as one batch: egglog's <c>(subsume (r a b))</c>, one command
        /// per row.
        /// Each row stays, and <see cref="Check"/> and <see cref="Rows"/> still find it, but rule bodies, queries and
        /// split rules no longer match it. A row that does not exist yet is inserted, subsumed. Subsumption is sticky,
        /// and rows already subsumed write nothing, so they move neither <see cref="Version"/> nor any extractor. In a
        /// child, the subsumption shadows the ancestor's row: the ancestors and the siblings still match it. A row that
        /// fails keeps the terms its keys made, as egglog's failed command does, and the rows before it stand. On a case
        /// whose contradiction stopped it, or a scope under
        /// one, the call does nothing.
        /// </summary>
        /// <param name="rows">The rows, of relations defined <see cref="TableOptions.Subsumable"/>.</param>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">A fact is not a relation row, a relation is not subsumable, a term in a
        /// row is not ground or holds a class value this scope cannot use, or a row writes through a scoped view
        /// (<c>T.In(tag)</c>) or a <see cref="Fact.In"/>, which are read-only.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen (write in a child of it) or faulted, or in
        /// use on another thread; or the call comes from a run observer or a user delegate the engine
        /// invoked.</exception>
        /// <exception cref="RuleEvaluationException">A merge failed; the scope is faulted.</exception>
        /// <exception cref="OverflowException">The graph has run out of ids or epochs (<see cref="EGraph.Usage"/>), and
        /// the scope is not faulted. When the ids ran out, or the epochs before the merge, nothing is written. When the
        /// epochs ran out in the rebuild after the merge, the write stays, and if that rebuild left work undone the
        /// scope's reads throw the same way (<see cref="Check"/>). Every later call that needs an id or an epoch throws
        /// the same way.</exception>
        public void Subsume(params Fact[] rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            EnterWrite();
            try
            {
                // A stopped case, or a scope under one, has no model: a subsumption changes nothing.
                if (IsStopped)
                    return;

                var batch = new HostBatch(this);
                try
                {
                    foreach (var row in rows)
                        batch.PlanSubsume(row ?? throw new ArgumentNullException(nameof(rows), "A fact is null."));
                }
                catch
                {
                    CommitBefore(batch);
                    throw;
                }

                Commit(batch);
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Deletes the row of a ground function or constructor application as one atomic batch: egglog's
        /// <c>(delete (f args...))</c>. The row of the application's keys goes, if there is one, and nothing else: a
        /// deleted constructor term's class stays, with its other members, and rows that name the class stay. The keys
        /// are evaluated as egglog's top-level action evaluates them: a constructor term among them that does not exist
        /// yet is made, and a function read must find a value. A key with no row deletes nothing; when every term of it
        /// exists already, the call moves neither <see cref="Version"/> nor any extractor. A deletion is no change for a
        /// run's saturation, as in egglog. The root, and any scope without live descendants under a frozen
        /// parent, may delete; a scope with live descendants, a child of a writable parent and a case may not (the
        /// descendants would not see the deletion). Delete is a call of its own: a <see cref="ScopeWriter"/> batch does
        /// not delete. On a case whose contradiction stopped it, or a scope under one, the call does nothing.
        /// </summary>
        /// <typeparam name="T">The application's type.</typeparam>
        /// <param name="term">The application: <c>f.Of(k)</c> of a function or a constructor.</param>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">The term is not an application of a function or a constructor, it
        /// applies a scoped view (<c>T.In(tag)</c>), or a term in its keys is a variable or holds a class value this
        /// scope cannot use.</exception>
        /// <exception cref="InvalidOperationException">The scope may not delete (it has live descendants or a writable
        /// parent, or the function's merge is not a lattice and the scope is not the root); the scope is frozen or
        /// faulted, or in use on another thread; a function read in the keys has no value; or the call comes from a run
        /// observer or a user delegate the engine invoked. Nothing is written.</exception>
        /// <exception cref="RuleEvaluationException">A primitive in the keys threw or has no result, and nothing is
        /// written; or a merge failed, and the scope is faulted.</exception>
        /// <exception cref="OverflowException">The graph has run out of ids or epochs (<see cref="EGraph.Usage"/>), as
        /// for <see cref="Subsume{TSort}"/>.</exception>
        public void Delete<T>(Term<T> term)
        {
            ArgumentNullException.ThrowIfNull(term);
            EnterWrite();
            try
            {
                // A stopped case, or a scope under one, has no model: a deletion changes nothing.
                if (IsStopped)
                    return;

                var batch = new HostBatch(this);
                try
                {
                    batch.PlanDelete(term);
                }
                catch
                {
                    // egglog's failed command keeps the terms its calls made before it failed.
                    CommitBefore(batch);
                    throw;
                }

                Commit(batch);
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Deletes ground relation rows (<c>r.Of(a, b)</c>) as one batch: egglog's <c>(delete (r a b))</c>, one command
        /// per row. A row that does not exist deletes nothing. The rows' terms are evaluated as for
        /// <see cref="Delete{T}"/>, and the same scopes may delete. A row that fails deletes nothing, and the rows before it
        /// stand. On a case whose contradiction stopped it, or a scope under one, the call does nothing.
        /// </summary>
        /// <param name="rows">The rows.</param>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">A fact is not a relation row, a term in a row is a variable or holds a
        /// class value this scope cannot use, or a row is of a scoped view (<c>T.In(tag)</c>) or a
        /// <see cref="Fact.In"/>, which are read-only.</exception>
        /// <exception cref="InvalidOperationException">As for <see cref="Delete{T}"/>. The row deletes nothing, and the
        /// rows before it stand.</exception>
        /// <exception cref="RuleEvaluationException">As for <see cref="Delete{T}"/>.</exception>
        /// <exception cref="OverflowException">As for <see cref="Delete{T}"/>.</exception>
        public void Delete(params Fact[] rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            EnterWrite();
            try
            {
                // A stopped case, or a scope under one, has no model: a deletion changes nothing.
                if (IsStopped)
                    return;

                var batch = new HostBatch(this);
                try
                {
                    foreach (var row in rows)
                        batch.PlanDelete(row ?? throw new ArgumentNullException(nameof(rows), "A fact is null."));
                }
                catch
                {
                    CommitBefore(batch);
                    throw;
                }

                Commit(batch);
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Makes several writes one batch, egglog's commands one after the other: each call is checked as it is made, and
        /// the batch is staged, flushed and rebuilt once, after <paramref name="write"/> returns, unless a call needs what
        /// an earlier call's merge of classes makes, as egglog's next command sees it after the rebuild: the batch then
        /// flushes and rebuilds its earlier calls before that call. Each fact of a call is a command: one that fails writes
        /// nothing, as egglog's failed command does, and the batch's other facts stand, the call's own earlier facts
        /// included; an exception <paramref name="write"/> lets escape propagates once they are written.
        /// </summary>
        /// <remarks>
        /// A call from another thread, or from a user delegate the engine invoked, is refused and rejects the batch:
        /// <see cref="Write"/> then writes nothing more, even when <paramref name="write"/> caught the exception, and the
        /// class values the batch's <c>Make</c> calls returned since its last flush are invalid; so does a flush of the
        /// batch that fails. A call needs the earlier calls rebuilt when, after a merge of classes, it reads a function
        /// whose keys are classes, computes over classes, writes a function that holds classes, or one with a merge that
        /// is not a lattice whose keys are classes or whose merge compares classes or calls tables, or deletes a row keyed
        /// by classes; when it calls a primitive over the tables, or reads a key or writes a table that an earlier call's
        /// merge expression merges or reads, and that merge only the engine evaluates; after a merge expression that
        /// calls a primitive over the tables; and after a merge of classes, when the graph has a function of a sort that
        /// holds classes or is keyed by a map of them, whose rows the rebuild compares pass by pass, as egglog rebuilds
        /// after each command. A conflict a merge of the batch would cause is found in the fact that causes
        /// it, which then fails, keeping only the terms it made: two rows of a function of a sort that its merges make meet
        /// are compared at the
        /// end of that fact, as the rebuild's passes will compare them. While the batch is open, the scopes it reads through (its writable ancestors) refuse
        /// writes and runs, which would change what the batch's calls were checked against. Other calls of the thread
        /// domain are allowed, reads and queries of the ancestors and writes into descendants and siblings included; the
        /// batch is brought up to date with them before each of its calls and before it commits, without moving
        /// <see cref="Version"/>.
        /// </remarks>
        /// <param name="write">Makes the batch's calls through its <see cref="ScopeWriter"/>.</param>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen (write in a child of it) or faulted, or in use on
        /// another thread; or the call comes from a run observer or a user delegate the engine invoked.</exception>
        /// <exception cref="FactConflictException">Two rows of a function of a sort that the batch's merges make meet hold
        /// different classes when the rebuild compares them; the fact whose merge made them meet is not
        /// written.</exception>
        /// <exception cref="RuleEvaluationException">A merge failed; the scope is faulted.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="OverflowException">The graph has run out of ids or epochs (<see cref="EGraph.Usage"/>), in a
        /// call of the batch or as it commits, and the scope is not faulted. When the ids ran out in a call, its fact
        /// writes nothing; when they or the epochs ran out before a flush's merge, the flush writes nothing, and the batch
        /// nothing more. When the epochs ran out in the rebuild after the merge, the flush stays written, and if that
        /// rebuild left work undone the scope's reads throw the same way (<see cref="Check"/>). Every later call that needs
        /// an id or an epoch throws the same way.</exception>
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
                ExceptionDispatchInfo? failure = null;
                try
                {
                    write(writer);
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    CountOpenBatch(-1);
                    openBatch = null;
                    writer.Close();
                }

                // A call was refused for being made from another thread, or from a user delegate the engine invoked: the
                // batch writes nothing more, and its origin never commits.
                if (batch.IsRejected)
                {
                    failure?.Throw();
                    return;
                }

                // The calls are egglog's commands: those that did not fail stand, though the callback threw, unless the
                // failure faulted the scope or a flush stopped it.
                if (failure is not null && (IsFaulted || IsStopped))
                    failure.Throw();

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
                failure?.Throw();
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Commits what a batch planned before a call of it failed, as egglog's commands before a failed one stand, unless
        /// the failure faulted the scope or stopped it.
        /// </summary>
        void CommitBefore(HostBatch batch)
        {
            if (IsFaulted || IsStopped || batch.IsRejected)
                return;

            Commit(batch);
        }

        /// <summary>
        /// Commits a planned batch, or the part of it planned so far: stages and flushes it unless it is empty, and commits
        /// its origin. A failure a merge recorded faults the scope.
        /// </summary>
        /// <exception cref="RuleEvaluationException">A merge failed.</exception>
        internal void Commit(HostBatch batch)
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
