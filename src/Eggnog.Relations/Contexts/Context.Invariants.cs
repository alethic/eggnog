using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// The most violations one check reports; the rest are counted, not listed.
        /// </summary>
        const int MaxReportedViolations = 64;

        /// <summary>
        /// The number of ids <see cref="CheckUnionFindInvariants"/> samples across the store's id range for coarsening.
        /// </summary>
        const int SampledIds = 256;

        /// <summary>
        /// How many times the gate check (§12.7 item 7) tries to read the descendant tree and the gate in one consistent
        /// state before it gives up for this run of the checker.
        /// </summary>
        const int GateReadAttempts = 16;

        /// <summary>
        /// Checks the context's invariants (§12.7, L3) and throws listing every violation found: the debug checker that
        /// tests run after every rebuild, and that DEBUG builds run at the end of <see cref="Rebuild"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Interim (M9.2): the union-find part (<see cref="CheckUnionFindInvariants"/>). The tables part
        /// (<c>ContextTables.CheckInvariants</c>, I12-I18) and the executor part arrive with the layers and the layered
        /// executor (M9.4, M10.2). Cost: linear in the context's overlay and journal, its parent's displacements since its
        /// creation, its ancestors' overlays, journals and root rows (I10), and the root's id range at the root.
        /// </para>
        /// <para>
        /// A context that is not frozen is checked inside its thread domain (I24), so nothing it reads changes under the
        /// check; a frozen context is read without it (§8.3).
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">The context is not clean, an invariant is violated, or the
        /// context is not frozen and its domain is held by another thread.</exception>
        internal void CheckInvariants()
        {
            ThrowIfDisposed();
            var current = frozen ? null : Domain;
            using var scope = current is null ? default : current.Enter();
            if (IsClean == false)
                throw new InvalidOperationException("Invariants are checked on a clean context; call Rebuild() first.");

            var violations = new List<string>();
            CheckUnionFindInvariants(violations);
            if (violations.Count > 0)
                throw new InvalidOperationException($"The context at depth {Depth} violates {violations.Count} invariant(s):{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
        }

        /// <summary>
        /// Adds to <paramref name="violations"/> every violation of the union-find invariants (§12.2, §12.7 item 1).
        /// </summary>
        /// <remarks>
        /// At the root, I4 (<c>parents[i] ≤ i</c>, one row per displaced id, rows ordered by timestamp, every displaced id
        /// non-canonical) and, once frozen, full compression. In a child: (1) I5/I6, the overlay partitions its members
        /// into classes of at least two whose canonical is their minimum, each member canonical in the parent and mapped to
        /// its slot; (2) I7, coarsening <c>find_C(find_P(x)) = find_C(x)</c> and minimality on the members, the journal,
        /// the parent's displacements since creation and sampled ids; (3) I8, journal ids unique, ordered, non-canonical,
        /// not displaced in the parent's view at creation, J (every non-canonical member is journaled) and completeness
        /// (every parent displacement since creation is non-canonical and journaled); (4) I9, the cursor bounds;
        /// (5) I10, no id local to the child in an ancestor's overlay, journal or root rows. At every level: (6) I23, the
        /// frozen prefix and a flattened map that agrees with the chain; (7) the descendant gate equals the live
        /// descendants counted recursively. Item 7 compares the two only when it read them in one consistent state
        /// (<see cref="TryCountDescendantsConsistently"/>): always so for a context that is not frozen, whose descendants
        /// share the domain the checker holds; under a frozen context, whose descendants come and go on other threads, it
        /// retries and, if they never stand still, checks nothing this time rather than report a violation that does not
        /// exist.
        /// </remarks>
        /// <param name="violations">Receives one line per violation.</param>
        internal void CheckUnionFindInvariants(List<string> violations)
        {
            ArgumentNullException.ThrowIfNull(violations);
            if (parent is null)
                CheckRootUnionFind(violations);
            else
                CheckChildUnionFind(violations);

            CheckFrozenPrefix(violations);
            if (TryCountDescendantsConsistently(out var counted, out var live) && counted != live)
                Report(violations, $"gate: {live} live descendants counted by the gate, {counted} found");
        }

        /// <summary>
        /// Counts the live descendants by walking the children and reads the gate, in one consistent state of the tree:
        /// no creation or disposal below this context began or ended between the reads (<see cref="DescendantGate"/>'s
        /// change marks). Retries up to <see cref="GateReadAttempts"/> times.
        /// </summary>
        /// <param name="counted">The live descendants found by the walk.</param>
        /// <param name="live">The gate's count.</param>
        /// <returns><c>true</c> if the two were read in one consistent state; <c>false</c> if the tree kept changing.</returns>
        bool TryCountDescendantsConsistently(out int counted, out int live)
        {
            var spin = new SpinWait();
            for (var attempt = 0; attempt < GateReadAttempts; attempt++)
            {
                var before = gate.Changes;
                if (gate.IsChanging == false)
                {
                    counted = CountLiveDescendants();
                    live = gate.LiveDescendants;
                    if (gate.IsChanging == false && gate.Changes == before)
                        return true;
                }

                spin.SpinOnce();
            }

            counted = 0;
            live = 0;
            return false;
        }

        /// <summary>
        /// I4 at the root, and compression once frozen.
        /// </summary>
        void CheckRootUnionFind(List<string> violations)
        {
            var displaced = store.RootUnionFind.UnionFind;
            var parents = displaced.UnionFind.Parents;
            for (var i = 0; i < parents.Length; i++)
            {
                var p = parents[i];
                if (p > (uint)i)
                    Report(violations, $"I4: parents[{i}] = {p} is above its index");
                else if (frozen && parents[(int)p] != p)
                    Report(violations, $"I23: the frozen root is not compressed at {i}");
            }

            var children = displaced.Children;
            var stamps = displaced.Timestamps;
            for (var row = 0; row < children.Length; row++)
            {
                var id = children[row];
                if (displaced.FindNaive(id) == id)
                    Report(violations, $"I4: displaced id {id} (row {row}) is canonical");

                if (displaced.TryGetRow(id, out var found) == false || found != row)
                    Report(violations, $"I4: displaced id {id} maps to row {found}, not {row}");

                if (row > 0 && stamps[row] < stamps[row - 1])
                    Report(violations, $"I4: row {row} has timestamp {stamps[row]} below the previous row's {stamps[row - 1]}");
            }

            if (journalMark > children.Length)
                Report(violations, $"I9: the journal mark {journalMark} is beyond the {children.Length} displaced ids");
        }

        /// <summary>
        /// I5-I10 in a child.
        /// </summary>
        void CheckChildUnionFind(List<string> violations)
        {
            var o = overlay!;
            var j = journal!;
            var pf = parentFinder!;
            var f = finder;
            var blocks = idBlocks!;

            // (1) I5, I6: the overlay's classes.
            var seen = new HashSet<uint>();
            var members = new List<uint>();
            var occupied = 0;
            for (var slot = 0; slot < o.SlotCount; slot++)
            {
                var count = o.CountOf(slot);
                if (count == 0)
                    continue;

                occupied++;
                var list = o.MembersOf(slot);
                var canonical = o.CanonicalOf(slot);
                if (count < 2)
                    Report(violations, $"I5: the class in slot {slot} has {count} member");

                var min = uint.MaxValue;
                foreach (var m in list)
                {
                    min = Math.Min(min, m);
                    members.Add(m);
                    if (seen.Add(m) == false)
                        Report(violations, $"I5: {m} is a member twice");

                    if (o.TryGetClass(m, out var k) == false || k != slot)
                        Report(violations, $"I5: member {m} of slot {slot} maps to slot {k}");

                    if (pf.Find(m) != m)
                        Report(violations, $"I6: member {m} of slot {slot} is not canonical in the parent (it is {pf.Find(m)} there)");

                    var cm = f.Find(m);
                    if (cm != canonical)
                        Report(violations, $"I7: member {m} of slot {slot} finds {cm}, not its class's canonical {canonical}");

                    // J (I8): every member that is not its class's canonical has stopped being canonical here.
                    if (m != canonical && j.Contains(m) == false)
                        Report(violations, $"I8: member {m} is not canonical (its class's canonical is {canonical}) and is not journaled");
                }

                if (canonical != min)
                    Report(violations, $"I5: the class in slot {slot} has canonical {canonical}, not its minimum {min}");
            }

            if (seen.Count != o.MemberCount)
                Report(violations, $"I5: the classes hold {seen.Count} members, the class map {o.MemberCount}");

            if (occupied != o.ClassCount)
                Report(violations, $"I5: {occupied} slots are occupied, the overlay counts {o.ClassCount} classes");

            // (3) I8: the journal.
            var ids = j.Ids;
            var stamps = j.Timestamps;
            for (var row = 0; row < ids.Length; row++)
            {
                var id = ids[row];
                if (j.TryGetRow(id, out var found) == false || found != row)
                    Report(violations, $"I8: journaled id {id} maps to row {found}, not {row}");

                if (row > 0 && stamps[row] < stamps[row - 1])
                    Report(violations, $"I8: journal row {row} has timestamp {stamps[row]} below the previous row's {stamps[row - 1]}");

                if (f.Find(id) == id)
                    Report(violations, $"I8: journaled id {id} (row {row}) is canonical");

                if (DisplacedInParentAtCreation(id))
                    Report(violations, $"I8: journaled id {id} was already displaced in the parent's view when the context was created");

                if (blocks.Contains(id) && IsOwnDisplacement(id) == false)
                    Report(violations, $"I10: local id {id} is journaled as displaced by the parent");
            }

            // (4) I9: the cursor.
            var parentCount = parent!.DisplacedCount;
            if (unionFindCursor < parentDisplacedAtCreation || unionFindCursor > parentCount)
                Report(violations, $"I9: the cursor {unionFindCursor} is outside [{parentDisplacedAtCreation}, {parentCount}]");

            if (IsUnionFindCurrent == false)
                Report(violations, $"I9: the context is clean but its cursor {unionFindCursor} is behind the parent's {parentCount} displaced ids");

            if (journalMark > j.Count)
                Report(violations, $"I9: the journal mark {journalMark} is beyond the journal's {j.Count} rows");

            // Completeness (I8): every parent displacement since creation stopped being canonical here and is journaled.
            var parentIds = parent.DisplacedIds;
            var suffix = new List<uint>();
            for (var i = parentDisplacedAtCreation; i < Math.Min(parentCount, parentIds.Length); i++)
            {
                var id = parentIds[i];
                suffix.Add(id);
                if (f.Find(id) == id)
                    Report(violations, $"I8: the parent displaced {id} but it is canonical here");
                else if (j.Contains(id) == false)
                    Report(violations, $"I8: the parent displaced {id} after this context was created, and it is not journaled");
            }

            // (2) I7: coarsening and minimality.
            foreach (var x in members)
                CheckCoarsening(violations, x, pf, f);

            foreach (var x in ids)
                CheckCoarsening(violations, x, pf, f);

            foreach (var x in suffix)
                CheckCoarsening(violations, x, pf, f);

            foreach (var x in SampleIds())
                CheckCoarsening(violations, x, pf, f);

            // (5) I10: no ancestor holds an id local to this context.
            for (var level = 0; level < Depth; level++)
            {
                var a = ancestors[level];
                if (a.IsRoot)
                {
                    var root = store.RootUnionFind.UnionFind;
                    foreach (var id in root.Children)
                        if (blocks.Contains(id) || blocks.Contains(root.FindNaive(id)))
                            Report(violations, $"I10: a root union-find row mentions an id local to the context at depth {Depth} ({id} → {root.FindNaive(id)})");

                    continue;
                }

                var ao = a.overlay!;
                for (var slot = 0; slot < ao.SlotCount; slot++)
                    foreach (var m in ao.MembersOf(slot))
                        if (blocks.Contains(m))
                            Report(violations, $"I10: the overlay of the ancestor at depth {level} holds {m}, local to the context at depth {Depth}");

                foreach (var id in a.journal!.Ids)
                    if (blocks.Contains(id))
                        Report(violations, $"I10: the journal of the ancestor at depth {level} holds {id}, local to the context at depth {Depth}");
            }
        }

        /// <summary>
        /// I23: a frozen context's ancestors are frozen, and a frozen child's flattened map agrees with its chain.
        /// </summary>
        void CheckFrozenPrefix(List<string> violations)
        {
            if (frozen == false)
                return;

            for (var level = 0; level < Depth; level++)
                if (ancestors[level].frozen == false)
                    Report(violations, $"I23: the context is frozen but its ancestor at depth {level} is not");

            if (parent is null)
                return;

            var flat = LayeredUnionFind.Flattened(store.RootUnionFind.UnionFind, flattened);
            if (ReferenceEquals(flat.Prefix, FinderForChildren.Prefix) == false)
                Report(violations, "I23: the frozen context does not give its children its flattened find");

            var keys = Members(finder);
            finder.Prefix?.CollectKeys(keys);
            flattened?.CollectKeys(keys);
            keys.AddRange(SampleIds());

            foreach (var x in keys)
            {
                var chain = finder.Find(x);
                var flatFind = flat.Find(x);
                if (chain != flatFind)
                    Report(violations, $"I23: the flattened find maps {x} to {flatFind}, the chain to {chain}");
            }
        }

        /// <summary>
        /// Checks I7 for one id: coarsening over the parent, minimality, and idempotence.
        /// </summary>
        static void CheckCoarsening(List<string> violations, uint x, LayeredUnionFind parentFind, LayeredUnionFind find)
        {
            var cx = find.Find(x);
            var px = parentFind.Find(x);
            if (find.Find(px) != cx)
                Report(violations, $"I7: find(find_P({x}) = {px}) = {find.Find(px)}, but find({x}) = {cx}");

            if (cx > x || cx > px)
                Report(violations, $"I7: find({x}) = {cx} is above {x} or its parent canonical {px}");

            if (find.Find(cx) != cx)
                Report(violations, $"I7: find({x}) = {cx} is not canonical itself");
        }

        /// <summary>
        /// Gets whether <paramref name="id"/> was displaced in the parent's view when this child was created: in the
        /// parent's displaced ids before <see cref="parentDisplacedAtCreation"/>, or, recursively, displaced in the
        /// grandparent's view when the parent was created.
        /// </summary>
        bool DisplacedInParentAtCreation(uint id)
        {
            var level = parent!;
            var mark = parentDisplacedAtCreation;
            while (true)
            {
                if (level.parent is null)
                    return store.RootUnionFind.UnionFind.TryGetRow(id, out var row) && row < mark;

                if (level.journal!.TryGetRow(id, out var journalRow) && journalRow < mark)
                    return true;

                mark = level.parentDisplacedAtCreation;
                level = level.parent;
            }
        }

        /// <summary>
        /// Counts the live descendants by walking the children.
        /// </summary>
        int CountLiveDescendants()
        {
            Context[] snapshot;
            lock (children)
                snapshot = children.ToArray();

            var count = 0;
            foreach (var child in snapshot)
                if (child.disposed == false)
                    count += 1 + child.CountLiveDescendants();

            return count;
        }

        /// <summary>
        /// Gets every member of every level of <paramref name="chain"/>.
        /// </summary>
        static List<uint> Members(LayeredUnionFind chain)
        {
            var result = new List<uint>();
            foreach (var level in chain.Levels)
                for (var slot = 0; slot < level.SlotCount; slot++)
                    foreach (var m in level.MembersOf(slot))
                        result.Add(m);

            return result;
        }

        /// <summary>
        /// Gets <see cref="SampledIds"/> ids spread over the ids the store has minted so far.
        /// </summary>
        IEnumerable<uint> SampleIds()
        {
            var limit = (ulong)Math.Max(1L, store.Counters.Read(store.IdCounter));
            for (var i = 0UL; i < SampledIds; i++)
                yield return (uint)(i * limit / SampledIds);
        }

        /// <summary>
        /// Adds a violation, up to <see cref="MaxReportedViolations"/>.
        /// </summary>
        static void Report(List<string> violations, string message)
        {
            if (violations.Count < MaxReportedViolations)
                violations.Add(message);
            else if (violations.Count == MaxReportedViolations)
                violations.Add("... further violations omitted");
        }

    }

}
