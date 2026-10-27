using System;
using System.Collections.Generic;

namespace Eggnog
{

    internal sealed partial class HostBatch
    {

        // egglog rebuilds after every command, and its next command sees the merged classes and rows; the batch flushes
        // once, at its end, and plans each fact against the engine as its earlier facts' writes leave it, merged by
        // their lattices but before their merges of classes and before the merge expressions only the engine evaluates.
        // A fact that needs what those make (a read of a function whose keys are classes, a computation over classes, a
        // write of a function that holds classes or whose merge is not a lattice, a deletion keyed by classes, a
        // primitive over the tables, a table such an expression reads, a read of a key such an expression merges, and
        // below the root a constant container of classes that the child's view does not hold yet, which its container
        // pass may make by rebuilding its copy of a container the merges reach) is planned after the batch flushes and
        // rebuilds what it planned before it (Rebuild), as egglog's next command runs after the last one's rebuild. The
        // flushed part then stays written whatever the batch's later calls do. Within a fact, its own merges come after
        // its writes, as egglog's do in its command, so a check of a write considers only the merges of the facts
        // before it (mergedBeforeFact).

        /// <summary>
        /// Whether the facts before the one being planned merged classes.
        /// </summary>
        bool mergedBeforeFact;

        /// <summary>
        /// The functions with a merge only the engine evaluates that the facts before the one being planned wrote.
        /// </summary>
        HashSet<TableHandle>? unknownTables;

        /// <summary>
        /// The tables read by the merge expressions of functions the fact being planned writes, which join
        /// <see cref="expressionReads"/> at its end.
        /// </summary>
        Dictionary<TableHandle, FunctionHandle>? factReaders;

        /// <summary>
        /// Whether a fact before the one being planned wrote a function whose merge expression calls a primitive over the
        /// tables, whose writes only the flush makes: every later fact needs the batch rebuilt.
        /// </summary>
        bool opaqueMerges;

        /// <summary>
        /// Whether a rebuild stopped the scope, a case its contradiction stopped: the batch's later calls plan nothing, as
        /// a stopped case's writes change nothing.
        /// </summary>
        bool stopped;

        /// <summary>
        /// Gets whether a fact may need the facts before it rebuilt.
        /// </summary>
        bool Unsettled => mergedBeforeFact || unknownTables is not null || expressionReads is not null;

        /// <summary>
        /// Gets whether every fact needs the facts before it rebuilt: after a merge expression's primitive wrote what it
        /// chose, and after a merge of classes when rows the rebuild compares by its passes may meet, which one rebuild of
        /// two facts' merges compares otherwise than egglog's two commands, each rebuilt, do (CheckClassOutputs).
        /// </summary>
        bool EveryFactRebuilds => opaqueMerges || (mergedBeforeFact && passSensitive);

        /// <summary>
        /// Flushes and rebuilds the facts planned so far before a fact that needs them so (<see cref="NeedsRebuild"/>).
        /// </summary>
        void RebuildFor(Fact fact)
        {
            if (EveryFactRebuilds || (Unsettled && NeedsRebuild(fact)))
                Rebuild();
        }

        /// <summary>
        /// Flushes and rebuilds the facts planned so far before a term that needs them so.
        /// </summary>
        void RebuildFor(Term term)
        {
            if (EveryFactRebuilds || (Unsettled && Needs(term.Shape)))
                Rebuild();
        }

        /// <summary>
        /// Flushes and rebuilds the facts planned so far before a union, which needs them so only when every fact does.
        /// </summary>
        void RebuildForUnion()
        {
            if (EveryFactRebuilds)
                Rebuild();
        }

        /// <summary>
        /// Flushes and rebuilds the facts planned so far before a deletion or a subsumption of an application that needs
        /// them so: one keyed by classes after a merge of classes, or of a table a merge expression reads.
        /// </summary>
        void RebuildFor(TableHandle table, Term[] arguments, bool deletes)
        {
            if (EveryFactRebuilds)
            {
                Rebuild();
                return;
            }

            if (Unsettled == false)
                return;

            if ((deletes && mergedBeforeFact && HasSortKey(table)) || ReadByExpression(table) || Needs(arguments))
                Rebuild();
        }

        /// <summary>
        /// Gets whether a fact needs the facts before it rebuilt.
        /// </summary>
        bool NeedsRebuild(Fact fact)
        {
            switch (fact)
            {
                case RelationFact relation:
                    return ReadByExpression(relation.Table) || Needs(relation.Arguments);
                case EqualityFact equality:
                {
                    var left = equality.Left.Shape;
                    var right = equality.Right.Shape;
                    if (IsApplication(left, TableKind.Function) && IsWritable(left, right))
                        return NeedsToWrite(left, right);

                    if (IsApplication(right, TableKind.Function) && IsWritable(right, left))
                        return NeedsToWrite(right, left);

                    if (IsApplication(left, TableKind.Constructor) && IsClassConstant(right))
                        return ReadByExpression(left.Table!) || Needs(left.Arguments);

                    if (IsApplication(right, TableKind.Constructor) && IsClassConstant(left))
                        return ReadByExpression(right.Table!) || Needs(right.Arguments);

                    return Needs(left) || Needs(right);
                }
                case ExistsFact exists:
                    return Needs(exists.Application.Shape);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Gets whether a write of a function's value needs the facts before it rebuilt: one of a function that holds
        /// classes, or whose merge is not a lattice and meets the merged classes or calls tables, after a merge of
        /// classes; one of a table a merge expression reads; or one whose terms need it.
        /// </summary>
        bool NeedsToWrite(TermShape application, TermShape value)
        {
            var function = (FunctionHandle)application.Table!;
            if (ReadByExpression(function))
                return true;

            if (mergedBeforeFact)
            {
                var output = function.Output!;
                var merge = function.Merge;
                if (merge is null ? output.InvolvesClasses : HasSortKey(function) || (output.InvolvesClasses && merge.ComparesValues) || merge.CanSimulate == false)
                    return true;
            }

            return Needs(application.Arguments) || Needs(value);
        }

        /// <summary>
        /// Gets whether any of the terms needs the facts before it rebuilt.
        /// </summary>
        bool Needs(Term[] terms)
        {
            foreach (var term in terms)
                if (Needs(term.Shape))
                    return true;

            return false;
        }

        /// <summary>
        /// Gets whether a term needs the facts before it rebuilt: a read of a function whose keys are classes after a
        /// merge of classes, or of a function a merge only the engine evaluates wrote; a constructor term of a table a
        /// merge expression reads, which it may make; a computation over classes after a merge of classes; below the
        /// root, after a merge of classes, a constant container of classes that the view does not hold yet
        /// (<see cref="InView"/>); or a primitive over the tables, a function value's application among them, which reads
        /// and writes what it chooses.
        /// </summary>
        bool Needs(TermShape shape)
        {
            switch (shape.Kind)
            {
                case TermShapeKind.Constant:
                {
                    // After a merge of classes, a constant's content may be one the child sees only once its container
                    // pass has rebuilt its copy of a container the merge reaches: interning it before would give the
                    // content a second id of the child's own, where egglog's next command finds the rebuilt one. A
                    // writable root makes the container, which its rebuild then merges with the rebuilt one.
                    if (mergedBeforeFact && scope.IsRoot == false && InView(shape) == false)
                        return true;

                    break;
                }
                case TermShapeKind.Application:
                {
                    var table = shape.Table!;
                    if (table.Kind == TableKind.Function)
                    {
                        if ((mergedBeforeFact && HasSortKey(table)) || (unknownTables is not null && unknownTables.Contains(table)))
                            return true;
                    }
                    else if (ReadByExpression(table))
                    {
                        return true;
                    }

                    break;
                }
                case TermShapeKind.Computation:
                {
                    var primitive = shape.Primitive!;
                    if (primitive.UsesTables)
                        return true;

                    if (mergedBeforeFact && OverClasses(primitive))
                        return true;

                    break;
                }
            }

            return Needs(shape.Arguments);
        }

        /// <summary>
        /// Gets whether a constant needs no container of classes the scope's view lacks: it holds no classes, or the view
        /// holds its container, as the cells it names are, which the rebuild after the flush rebuilds with the rest of
        /// the view. A container the view lacks may be one the container pass makes, or one no scope below the root may
        /// make, which the fact then refuses. One lookup by content per level of the view.
        /// </summary>
        bool InView(TermShape shape)
        {
            var type = TypeOf(shape);
            if (type.HoldsClasses == false || (type is IFnColumnType function && function.HoldsClassesIn(shape.Constant!) == false))
                return true;

            return type.TryEncodeExistingObject(shape.Constant!, scope.ContainerView, out _);
        }

        /// <summary>
        /// Gets whether a primitive takes or returns classes.
        /// </summary>
        bool OverClasses(Primitive primitive)
        {
            if (TypeOf(primitive.ResultType).InvolvesClasses)
                return true;

            foreach (var parameter in primitive.ParameterTypes)
                if (TypeOf(parameter).InvolvesClasses)
                    return true;

            return false;
        }

        /// <summary>
        /// Gets whether a merge expression of a function the facts before the one being planned wrote reads a table.
        /// </summary>
        bool ReadByExpression(TableHandle table) => expressionReads is not null && expressionReads.ContainsKey(table);

        /// <summary>
        /// Records that a function the fact being planned writes merges with an expression that reads a table.
        /// </summary>
        void NoteExpressionRead(TableHandle table, FunctionHandle reader) =>
            (factReaders ??= new Dictionary<TableHandle, FunctionHandle>(ReferenceEqualityComparer.Instance)).TryAdd(table, reader);

        /// <summary>
        /// Ends the planning of a fact: what it merged and the tables its merge expressions read bear on the next.
        /// </summary>
        void SettleFact()
        {
            mergedBeforeFact = mergedClasses;
            if (factReaders is null)
                return;

            foreach (var (table, reader) in factReaders)
                (expressionReads ??= new Dictionary<TableHandle, FunctionHandle>(ReferenceEqualityComparer.Instance)).TryAdd(table, reader);

            factReaders = null;
        }

        /// <summary>
        /// Flushes and rebuilds what the batch planned before the fact being planned, as egglog's commands so far, and
        /// starts planning afresh against the rebuilt scope, with a new origin; the classes the flushed part made are the
        /// scope's. A rejected batch writes nothing more, and forgets its plan instead. A flush that stops the scope, a
        /// case it contradicts, leaves the batch's later calls nothing to plan.
        /// </summary>
        /// <exception cref="InvalidOperationException">A scope under this one has a <see cref="Scope.Write"/> batch open,
        /// whose plan the flush would change, as a write here would.</exception>
        void Rebuild()
        {
            if (stopped)
                return;

            // A rejected batch writes nothing more, so it forgets its plan instead.
            if (rejected)
            {
                ClearPlan();
                return;
            }

            if (scope.HasBatchBelow)
                throw new InvalidOperationException(
                    $"Scope '{scope.Path}' cannot be written or run while a scope under it has a Write batch open; write it before or after the batch.");

            // A flush whose merge fails, as egglog's command whose merge panics, wrote what it staged, and the batch goes
            // on afresh, as egglog's later commands do. One that fails otherwise (the ids or the epochs ran out, a
            // conflict) leaves the batch nothing more to write: its later calls plan afresh, and are dropped.
            try
            {
                scope.Commit(this);
            }
            catch (RuleEvaluationException)
            {
                ClearPlan();
                stopped = scope.IsStopped;
                throw;
            }
            catch
            {
                rejected = true;
                ClearPlan();
                throw;
            }

            ClearPlan();
            stopped = scope.IsStopped;
        }

        /// <summary>
        /// Forgets what the batch planned, once a flush wrote it or failed, and starts planning afresh with a new origin.
        /// </summary>
        void ClearPlan()
        {
            pending.Clear();
            pendingByKey.Clear();
            fresh.Clear();
            rows.Clear();
            rowKeys.Clear();
            functionValues.Clear();
            currentValues.Clear();
            unknownValues = null;
            unknownTables = null;
            expressionReads = null;
            subsumes.Clear();
            subsumeKeys.Clear();
            subsumedTerms.Clear();
            unions.Clear();
            unionKeys.Clear();
            removes.Clear();
            removeKeys.Clear();
            ResetMergeCheck();
            opaqueMerges = false;
            notedMerges.Clear();
            MarkFact();
            mergedBeforeFact = false;
            Origin = new ScopeOrigin(graph, scope.Origin, scope.Path, true);
        }

    }

}
