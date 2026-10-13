using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// Checks the context's invariants (colored-contexts spec §12.7) and throws <see cref="ContextInvariantException"/>
        /// listing every violation: the storage checker (<see cref="Context.CheckInvariants"/>: union-find, tables and
        /// executor parts) plus the bridge's own parts. Always compiled; tests run it after every rebuild, rule run and
        /// flush.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The bridge's parts: <b>C1</b>, rule progress (every own watermark at most the clock and the context's semi-naive
        /// floor, at least its creation in a child, with as many constants as its rule; every watermark a run would use,
        /// own or inherited, at most the floor, an inherited one at most the context's creation, §10.2, §7.2);
        /// <b>C2</b>, a frozen context's state current (no host writes, no failure, the database frozen, every rebuild
        /// index of its own tables current, I23); <b>C5</b>, the stop flag (a stopped context sees a contradiction row,
        /// and a context that stops on contradictions and sees one is stopped, I25); <b>C6</b>, no own displacement held by
        /// an eq-sort container (the storage check <see cref="Context.CheckEqContainerElements"/>, G-b); <b>C7</b>, the
        /// tree (the storage context's host is this context, and each live storage child's host is a bridge child of this
        /// one); <b>C4</b>, member maps (in a child, a member map built now has exactly the overlay's members that are not
        /// local, each canonical in the parent and mapped to its group's root here, and the cached map, when current,
        /// equals it, I32).
        /// </para>
        /// <para>
        /// A stopped context answers only <see cref="IsContradictory"/> and may have stopped its merge with writes pending:
        /// only C5 and C7 are checked on it. Any other context must be clean (<see cref="IsClean"/>), except that host writes
        /// held by a frozen context are reported as a C2 violation. Cost: the storage checker's, plus
        /// O(rules × constants × depth), plus O(overlay members × depth) for C4.
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
                // A frozen context cannot stage host writes (it is not writable), so host writes there are corrupt state
                // that C2 reports rather than a precondition the caller missed.
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
        /// C1: the context's rule progress and the watermarks its runs would use (§10.2, §7.2, I30).
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
                        Context.Report(violations, $"C1: rule {id} ran at {p.LastRunAt}, after the clock {clock}");

                    if (p.LastRunAt > floor)
                        Context.Report(violations, $"C1: rule {id} ran at {p.LastRunAt}, above the context's semi-naive floor {floor}");

                    if (parent is not null && p.LastRunAt < context.CreatedAt)
                        Context.Report(violations, $"C1: rule {id} ran at {p.LastRunAt}, before the context was created at {context.CreatedAt}");

                    if (info.CachedPlan is { } planned)
                    {
                        var expected = planned.Plan.IdConstants.Length + planned.Plan.Actions.IdConstants.Length;
                        var recorded = p.Constants?.Length ?? 0;
                        if (recorded != expected)
                            Context.Report(violations, $"C1: rule {id} recorded {recorded} executed constant(s), its plan has {expected} (I30)");
                    }
                }

                // The watermark a run here would use now, own or inherited.
                if (info.CachedPlan is not { } plan || context.IsFrozen)
                    continue;

                var cached = plan.Plan;
                var constants = CanonicalConstants.Compute(context.Database.View, cached.IdConstants, cached.Actions.IdConstants, cached.WrittenConstants);
                var watermark = GetWatermark(id, info, constants);
                if (watermark > floor)
                    Context.Report(violations, $"C1: rule {id} would run from {watermark}, above the context's semi-naive floor {floor} (§7.2)");

                if (parent is not null && TryOwn(id, info.Generation, out _) == false && watermark > context.CreatedAt)
                    Context.Report(violations, $"C1: rule {id} would inherit {watermark}, after the context's creation at {context.CreatedAt} (§10.2)");
            }
        }

        /// <summary>
        /// C2: a frozen context's state is current (I23).
        /// </summary>
        void CheckFrozenState(List<string> violations)
        {
            if (context.IsFrozen == false)
                return;

            if (hostWrites.IsEmpty == false)
                Context.Report(violations, "C2: a frozen context holds host writes");

            var database = context.Database;
            if (database.IsFrozen == false)
                Context.Report(violations, "C2: a frozen context's database is not frozen");

            if (database.Failure is { } failure)
                Context.Report(violations, $"C2: a frozen context holds a failure that was not taken ({failure})");

            var schema = owner.Store.Schema;
            foreach (var id in schema.FunctionTables)
            {
                if (schema[id] is not { IsContextPrivate: false } s || s.RebuildColumns.Length == 0)
                    continue;

                var table = parent is null ? s.Root : context.Tables!.TryGetLayer(id)?.Own;
                if (table is null)
                    continue;

                if (table.RebuildIndex is not { } index || index.NeedsRefresh(table))
                    Context.Report(violations, $"C2: the rebuild index of table {id} is not current in a frozen context (I23)");
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
        /// C5: the stop flag agrees with the contradiction rows the context sees (I25).
        /// </summary>
        void CheckStop(List<string> violations)
        {
            var contradiction = owner.Store.Schema.Contradiction;
            if (IsStopped)
            {
                if (contradiction is not { } c)
                {
                    Context.Report(violations, "C5: the context is stopped, but the e-graph has no contradiction function");
                    return;
                }

                if (context.Tables is { } tables && tables.GetView(c).FindRow(ReadOnlySpan<uint>.Empty, out _) < 0 && OwnContradictionRow(tables, c) == false)
                    Context.Report(violations, "C5: the context is stopped, but no contradiction row is visible in it or merged into it (I25)");

                return;
            }

            if (contradiction is not null && context.Options.StopOnContradiction && context.IsContradictory)
                Context.Report(violations, "C5: the context stops on contradictions and sees a contradiction row, but is not stopped (I25)");
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
        /// C6: no own displacement of a child is an element of an eq-sort container (G-b), the storage check relabelled.
        /// </summary>
        void CheckEqContainers(List<string> violations)
        {
            var found = new List<string>();
            context.CheckEqContainerElements(found);
            foreach (var violation in found)
                Context.Report(violations, "C6: " + violation);
        }

        /// <summary>
        /// C7: the storage context's host is this context, its parent is the parent's, and every live storage child is
        /// hosted by a bridge child of this context.
        /// </summary>
        void CheckTree(List<string> violations)
        {
            if (ReferenceEquals(context.Host, this) == false)
                Context.Report(violations, "C7: the storage context's host is not this context");

            if (ReferenceEquals(parent?.context, context.Parent) == false)
                Context.Report(violations, "C7: the parent's storage context is not the storage context's parent");

            foreach (var child in context.SnapshotChildren())
            {
                if (child.Host is not EGraphContext host)
                    Context.Report(violations, $"C7: a child at depth {child.Depth} has no bridge context");
                else if (ReferenceEquals(host.parent, this) == false || ReferenceEquals(host.context, child) == false)
                    Context.Report(violations, $"C7: the bridge context of a child at depth {child.Depth} is not linked to this context");
            }
        }

    }

}
