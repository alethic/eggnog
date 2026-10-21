using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// Checks the context's invariants and throws <see cref="ContextInvariantException"/> listing every violation:
        /// the storage checker (<see cref="Context.CheckInvariants"/>: union-find, tables and executor parts) plus the
        /// bridge's own parts. Always compiled; tests run it after every rebuild, rule run and flush.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The bridge's parts: <b>rule progress</b> (every own watermark at most the clock and the context's semi-naive
        /// floor, at least its creation in a child, with as many constants as its rule; every watermark a run would
        /// use, own or inherited, at most the floor, an inherited one at most the context's creation);
        /// <b>frozen state</b> (a frozen context holds no host writes and no failure, its database is frozen, and every
        /// rebuild index of its own tables is current); <b>the stop flag</b> (a stopped context sees a contradiction
        /// row, and a context that stops on contradictions and sees one is stopped); <b>eq-sort containers</b> (no own
        /// displacement is held by an eq-sort container: the storage check
        /// <see cref="Context.CheckEqContainerElements"/>); <b>the tree</b> (the storage context's host is this
        /// context, and each live storage child's host is a bridge child of this one); <b>member maps</b> (in a child,
        /// a member map built now has exactly the overlay's members that are not local, each in its group root's
        /// overlay class, canonical in the parent and mapped to its group's root here, and the cached map, when current,
        /// equals it). Each violation starts with the name of its part: <c>rule progress:</c>, <c>frozen state:</c>,
        /// <c>stop flag:</c>, <c>eq-sort containers:</c>, <c>tree:</c> or <c>member maps:</c>.
        /// </para>
        /// <para>
        /// A stopped context answers only <see cref="IsContradictory"/> and may have stopped its merge with writes
        /// pending: only the stop flag and the tree are checked on it. Any other context must be clean
        /// (<see cref="IsClean"/>), except that host writes held by a frozen context are reported as a frozen-state
        /// violation. Cost: the storage checker's, plus O(rules × constants × depth), plus O(overlay members × depth)
        /// for the member maps.
        /// </para>
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is not stopped and not clean, or its domain is held by
        /// another thread.</exception>
        /// <exception cref="ContextInvariantException">An invariant is violated.</exception>
        public void CheckInvariants()
        {
            context.ThrowIfDisposed();
            var current = context.IsFrozen ? null : context.Domain;
            using var scope = current is null ? default : current.Enter();
            var violations = new List<string>();
            if (IsStopped)
            {
                CheckStop(violations);
                CheckTree(violations);
            }
            else
            {
                // A frozen context cannot stage host writes (it is not writable), so host writes there are corrupt
                // state that the frozen-state check reports rather than a precondition the caller missed.
                if (IsClean == false && (context.IsFrozen == false || CleanApartFromOwnHostWrites() == false))
                    throw new InvalidOperationException("Invariants are checked on a clean context; call EnsureClean() first.");

                context.CollectInvariantViolations(violations, eqContainers: false);
                CheckProgress(violations);
                CheckFrozenState(violations);
                CheckStop(violations);
                CheckEqContainers(violations);
                CheckMemberMaps(violations);
                CheckTree(violations);
            }

            if (violations.Count > 0)
                throw new ContextInvariantException(Depth, violations);
        }

        /// <summary>
        /// Checks the context's rule progress and the watermarks its runs would use.
        /// </summary>
        void CheckProgress(List<string> violations)
        {
            var clock = owner.Store.ReadClock();
            var floor = context.SemiNaiveFloor;
            var rules = new List<RuleInfo>();
            owner.Rules.CopyLive(rules);
            foreach (var info in rules)
            {
                var id = info.Id;
                if (TryOwn(id, info.Generation, out var p))
                {
                    if (p.LastRunAt > clock)
                        Context.Report(violations, $"rule progress: rule {id} ran at {p.LastRunAt}, after the clock {clock}");

                    if (p.LastRunAt > floor)
                        Context.Report(violations, $"rule progress: rule {id} ran at {p.LastRunAt}, above the context's semi-naive floor {floor}");

                    // A scheduler's query rule may hold its parent's run as its own (RuleProgress.Inherited).
                    if (parent is not null && p.LastRunAt < context.CreatedAt && p.Inherited == false)
                        Context.Report(violations, $"rule progress: rule {id} ran at {p.LastRunAt}, before the context was created at {context.CreatedAt}");

                    if (info.CachedPlan is { } planned)
                    {
                        var expected = planned.Plan.IdConstants.Length + planned.Plan.Actions.IdConstants.Length;
                        var recorded = p.Constants?.Length ?? 0;
                        if (recorded != expected)
                            Context.Report(violations, $"rule progress: rule {id} recorded {recorded} executed constant(s), its plan has {expected}");
                    }
                }

                // The watermark a run here would use now, own or inherited.
                if (info.CachedPlan is not { } plan || context.IsFrozen)
                    continue;

                var cached = plan.Plan;
                var constants = CanonicalConstants.Compute(context.Database.View, cached.IdConstants, cached.Actions.IdConstants, cached.WrittenConstants);
                var watermark = GetWatermark(id, info, constants);
                if (watermark > floor)
                    Context.Report(violations, $"rule progress: rule {id} would run from {watermark}, above the context's semi-naive floor {floor}");

                if (parent is not null && TryOwn(id, info.Generation, out _) == false && watermark > context.CreatedAt)
                    Context.Report(violations, $"rule progress: rule {id} would inherit {watermark}, after the context's creation at {context.CreatedAt}");
            }
        }

        /// <summary>
        /// Checks that a frozen context's state is current.
        /// </summary>
        void CheckFrozenState(List<string> violations)
        {
            if (context.IsFrozen == false)
                return;

            if (hostWrites.IsEmpty == false)
                Context.Report(violations, "frozen state: a frozen context holds host writes");

            var database = context.Database;
            if (database.IsFrozen == false)
                Context.Report(violations, "frozen state: a frozen context's database is not frozen");

            if (database.Failure is { } failure)
                Context.Report(violations, $"frozen state: a frozen context holds a failure that was not taken ({failure})");

            var schema = owner.Store.Schema;
            foreach (var id in schema.FunctionTables)
            {
                if (schema[id] is not { IsContextPrivate: false } s || s.RebuildColumns.Length == 0)
                    continue;

                var table = parent is null ? s.Root : context.Tables!.TryGetLayer(id)?.Own;
                if (table is null)
                    continue;

                if (table.RebuildIndex is not { } index || index.NeedsRefresh(table))
                    Context.Report(violations, $"frozen state: the rebuild index of table {id} is not current in a frozen context");
            }
        }

        /// <summary>
        /// Gets whether the context would be clean (<see cref="IsClean"/>) without its own host writes: its storage context
        /// is clean, and so is every ancestor up to the first frozen one.
        /// </summary>
        bool CleanApartFromOwnHostWrites()
        {
            if (context.IsClean == false)
                return false;

            for (var up = parent; up is not null && up.IsFrozen == false; up = up.parent)
                if (up.hostWrites.IsEmpty == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Checks that the stop flag agrees with the contradiction rows the context sees.
        /// </summary>
        void CheckStop(List<string> violations)
        {
            var contradiction = owner.Store.Schema.Contradiction;
            if (IsStopped)
            {
                if (contradiction is not { } c)
                {
                    Context.Report(violations, "stop flag: the context is stopped, but the e-graph has no contradiction function");
                    return;
                }

                if (context.Tables is { } tables && tables.GetView(c).FindRow(ReadOnlySpan<uint>.Empty, out _) < 0 && OwnContradictionRow(tables, c) == false)
                    Context.Report(violations, "stop flag: the context is stopped, but no contradiction row is visible in it or merged into it");

                return;
            }

            if (contradiction is not null && context.Options.StopOnContradiction && context.IsContradictory)
                Context.Report(violations, "stop flag: the context stops on contradictions and sees a contradiction row, but is not stopped");
        }

        /// <summary>
        /// Gets whether the child's own layer of the contradiction table holds a live row, hidden or not by its view.
        /// </summary>
        static bool OwnContradictionRow(ContextTables tables, TableId contradiction)
        {
            if (tables.TryGetLayer(contradiction)?.Own is not { } own)
                return false;

            var rows = own.Rows;
            for (var r = 0; r < rows.Count; r++)
                if (rows.IsStale(r) == false)
                    return true;

            return false;
        }

        /// <summary>
        /// Checks that no own displacement of a child is an element of an eq-sort container: the storage check, its
        /// violations reported as they are, under <c>eq-sort containers:</c>.
        /// </summary>
        void CheckEqContainers(List<string> violations)
        {
            var found = new List<string>();
            context.CheckEqContainerElements(found);
            foreach (var violation in found)
                Context.Report(violations, violation);
        }

        /// <summary>
        /// Checks the tree: the storage context's host is this context, its parent is the parent's, and every live
        /// storage child is hosted by a bridge child of this context.
        /// </summary>
        void CheckTree(List<string> violations)
        {
            if (ReferenceEquals(context.Host, this) == false)
                Context.Report(violations, "tree: the storage context's host is not this context");

            if (ReferenceEquals(parent?.context, context.Parent) == false)
                Context.Report(violations, "tree: the parent's storage context is not the storage context's parent");

            foreach (var child in context.SnapshotChildren())
            {
                if (child.Host is not EGraphContext host)
                    Context.Report(violations, $"tree: a child at depth {child.Depth} has no bridge context");
                else if (ReferenceEquals(host.parent, this) == false || ReferenceEquals(host.context, child) == false)
                    Context.Report(violations, $"tree: the bridge context of a child at depth {child.Depth} is not linked to this context");
            }
        }

    }

}
