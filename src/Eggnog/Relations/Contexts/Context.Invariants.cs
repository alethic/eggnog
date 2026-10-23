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
        /// How many times the gate check (the gate's count against the live descendants) tries to read the descendant
        /// tree and the gate in one consistent state before it gives up for this run of the checker.
        /// </summary>
        const int GateReadAttempts = 16;

        /// <summary>
        /// Checks the context's invariants and throws listing every violation found: the debug checker that tests run
        /// after every rebuild, and that DEBUG builds run at the end of <see cref="Rebuild"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Composition: the union-find part (<see cref="CheckUnionFindInvariants"/>: the union-find's classes, journal,
        /// cursor and local ids, the frozen prefix and the gate), then, in a child, the tables part
        /// (<see cref="ContextTables.CheckInvariants"/>: what the context holds and hides, and local ids in ancestor
        /// layers), the container-layers part (<see cref="ContainerLayers.CheckInvariants"/>: each layer's own
        /// environment, the ids it may own, and a frozen layer unchanged) and the eq-container part
        /// (<see cref="CheckEqContainerElements"/>), and at every level the executor
        /// part (<see cref="ExecutorInvariants.Check"/>: views, frozen catalogs, sort order, stamps and keys as the
        /// executor reads them). The root's rows are checked canonical where the root settles, at
        /// <see cref="CreateChild"/> and <see cref="Freeze"/>, in DEBUG builds (<see cref="CheckRootSettled"/>):
        /// elsewhere a raw root write may leave a stale row until the next rebuild, as in egglog. Cost: linear in the
        /// context's overlay and journal, its parent's displacements since its creation, its ancestors' overlays,
        /// journals and root rows (checked for the context's local ids), the root's id range at the root, and the
        /// tables part's cost.
        /// </para>
        /// <para>
        /// A context that is not frozen is checked inside its thread domain, so nothing it reads changes under the
        /// check; a frozen context is read without it.
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
            CollectInvariantViolations(violations, eqContainers: true);
            if (violations.Count > 0)
                throw new InvalidOperationException($"The context at depth {Depth} violates {violations.Count} invariant(s):{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
        }

        /// <summary>
        /// Adds every violation <see cref="CheckInvariants"/> reports to <paramref name="violations"/>, without
        /// throwing: the union-find part, the tables part, the container-layers part, the executor part and, with
        /// <paramref name="eqContainers"/>, the eq-container part (<see cref="CheckEqContainerElements"/>). The bridge
        /// composes its own checker from it (<c>EGraphContext.CheckInvariants</c>), reporting the eq-container part
        /// itself. The caller holds the domain of a context that is not frozen, and the context is clean.
        /// </summary>
        /// <param name="violations">Receives one line per violation.</param>
        /// <param name="eqContainers">Whether to include the eq-container part.</param>
        internal void CollectInvariantViolations(List<string> violations, bool eqContainers)
        {
            CheckUnionFindInvariants(violations);
            tables?.CheckInvariants(violations);
            containerLayers?.CheckInvariants(violations);
            if (eqContainers)
                CheckEqContainerElements(violations);

            ExecutorInvariants.Check(this, violations);
        }

        /// <summary>
        /// Checks that the root is settled: every id cell of every live row of every registered table is canonical in
        /// the root's view, as after <see cref="Settle"/>; throws listing the violations, each starting with
        /// <c>root settled:</c>. The root part of the tables
        /// check, which DEBUG builds run where the root settles (<see cref="CreateChild"/>, <see cref="Freeze"/>).
        /// </summary>
        /// <remarks>Cost: O(root rows × id columns), plus the union-find's finds.</remarks>
        /// <exception cref="InvalidOperationException">This is not the root, or a root row names a non-canonical
        /// id.</exception>
        internal void CheckRootSettled()
        {
            if (parent is not null)
                throw new InvalidOperationException("Only the root settles.");

            var violations = new List<string>();
            var find = store.RootUnionFind.UnionFind;
            foreach (var id in store.Schema.FunctionTables)
            {
                var s = store.Schema[id]!;
                var columns = s.RebuildColumns;
                if (columns.Length == 0)
                    continue;

                var rows = s.Root.Rows;
                for (var r = 0; r < rows.Count; r++)
                {
                    if (rows.IsStale(r))
                        continue;

                    var cells = rows.GetRow(r);
                    foreach (var column in columns)
                    {
                        var cell = cells[column.Rep];
                        if (find.FindNaive(cell) != cell)
                            Report(violations, $"root settled: root row {r} of table {id} names {cell} in column {column.Rep}, which is {find.FindNaive(cell)} at the root");
                    }
                }
            }

            if (violations.Count > 0)
                throw new InvalidOperationException($"The root is not settled; {violations.Count} violation(s):{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
        }

        /// <summary>
        /// Adds to <paramref name="violations"/> every own displacement of a child that an eq-sort container holds (the
        /// storage half of the bridge's eq-container check), each starting with <c>eq-sort containers:</c>: what the
        /// eq-container guard refuses. Nothing at the root or without a container type holding ids. O(journal ×
        /// container types) probes.
        /// </summary>
        /// <param name="violations">Receives one line per violation.</param>
        internal void CheckEqContainerElements(List<string> violations)
        {
            if (containers is not { HasEqContainers: true } values)
                return;

            foreach (var id in journal!.Ids)
                if (IsOwnDisplacement(id) && values.AnyEqContainerMentions(id))
                    Report(violations, $"eq-sort containers: the context displaced {id}, which an eq-sort container holds");
        }

        /// <summary>
        /// DEBUG builds: <see cref="CheckRootSettled"/>, where the root settles; nothing in a release build.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        void AssertRootSettled() => CheckRootSettled();

        /// <summary>
        /// Adds to <paramref name="violations"/> every violation of the union-find invariants.
        /// </summary>
        /// <remarks>
        /// At the root, the union-find array (<c>parents[i] ≤ i</c>, one row per displaced id, rows ordered by
        /// timestamp, every displaced id non-canonical) and, once frozen, full compression. In a child: (1) the overlay
        /// partitions its members into classes of at least two whose canonical is their minimum, each member canonical
        /// in the parent and mapped to its slot; (2) coarsening <c>find_C(find_P(x)) = find_C(x)</c> and minimality on
        /// the members, the journal, the parent's displacements since creation and sampled ids; (3) journal ids unique,
        /// ordered, non-canonical, not displaced in the parent's view at creation, every non-canonical member
        /// journaled, and completeness (every parent displacement since creation is non-canonical and journaled); (4)
        /// the cursor bounds; (5) no id local to the child in an ancestor's overlay, journal or root rows. At every
        /// level: (6) the frozen prefix and a flattened map that agrees with the chain; (7) the descendant gate equals
        /// the live descendants counted recursively. Each violation starts with the name of its check: <c>root
        /// union-find:</c> for the root's array, <c>frozen prefix:</c> for its compression and for (6), <c>overlay
        /// classes:</c> and <c>overlay members:</c> (a member not canonical in the parent) for (1), <c>coarsening:</c>
        /// for (2) and for a member that does not find its class's canonical, <c>journal:</c> for (3), <c>cursor
        /// bounds:</c> for (4) and the journal mark, <c>local ids:</c> for (5) and for a local id journaled as the
        /// parent's, and <c>gate:</c> for (7). Item 7 compares the two only when it read them in one consistent state
        /// (<see cref="TryCountDescendantsConsistently"/>): always so for a context that is not frozen, whose
        /// descendants share the domain the checker holds; under a frozen context, whose descendants come and go on
        /// other threads, it retries and, if they never stand still, checks nothing this time rather than report a
        /// violation that does not exist.
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
        /// The root's union-find array (each parent at or below its index, one row per displaced id, rows ordered by
        /// timestamp, every displaced id non-canonical), and compression once frozen.
        /// </summary>
        void CheckRootUnionFind(List<string> violations)
        {
            var displaced = store.RootUnionFind.UnionFind;
            var parents = displaced.UnionFind.Parents;
            for (var i = 0; i < parents.Length; i++)
            {
                var p = parents[i];
                if (p > (uint)i)
                    Report(violations, $"root union-find: parents[{i}] = {p} is above its index");
                else if (frozen && parents[(int)p] != p)
                    Report(violations, $"frozen prefix: the frozen root is not compressed at {i}");
            }

            var children = displaced.Children;
            var stamps = displaced.Timestamps;
            for (var row = 0; row < children.Length; row++)
            {
                var id = children[row];
                if (displaced.FindNaive(id) == id)
                    Report(violations, $"root union-find: displaced id {id} (row {row}) is canonical");

                if (displaced.TryGetRow(id, out var found) == false || found != row)
                    Report(violations, $"root union-find: displaced id {id} maps to row {found}, not {row}");

                if (row > 0 && stamps[row] < stamps[row - 1])
                    Report(violations, $"root union-find: row {row} has timestamp {stamps[row]} below the previous row's {stamps[row - 1]}");
            }

            if (journalMark > children.Length)
                Report(violations, $"cursor bounds: the journal mark {journalMark} is beyond the {children.Length} displaced ids");
        }

        /// <summary>
        /// The union-find invariants of a child: its overlay's classes, coarsening, its journal, its cursor and its
        /// local ids.
        /// </summary>
        void CheckChildUnionFind(List<string> violations)
        {
            var o = overlay!;
            var j = journal!;
            var pf = parentFinder!;
            var f = finder;
            var blocks = idBlocks!;

            // (1) The overlay's classes.
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
                    Report(violations, $"overlay classes: the class in slot {slot} has {count} member");

                var min = uint.MaxValue;
                foreach (var m in list)
                {
                    min = Math.Min(min, m);
                    members.Add(m);
                    if (seen.Add(m) == false)
                        Report(violations, $"overlay classes: {m} is a member twice");

                    if (o.TryGetClass(m, out var k) == false || k != slot)
                        Report(violations, $"overlay classes: member {m} of slot {slot} maps to slot {k}");

                    if (pf.Find(m) != m)
                        Report(violations, $"overlay members: member {m} of slot {slot} is not canonical in the parent (it is {pf.Find(m)} there)");

                    var cm = f.Find(m);
                    if (cm != canonical)
                        Report(violations, $"coarsening: member {m} of slot {slot} finds {cm}, not its class's canonical {canonical}");

                    // Every member that is not its class's canonical has stopped being canonical here, so it is
                    // journaled.
                    if (m != canonical && j.Contains(m) == false)
                        Report(violations, $"journal: member {m} is not canonical (its class's canonical is {canonical}) and is not journaled");
                }

                if (canonical != min)
                    Report(violations, $"overlay classes: the class in slot {slot} has canonical {canonical}, not its minimum {min}");
            }

            if (seen.Count != o.MemberCount)
                Report(violations, $"overlay classes: the classes hold {seen.Count} members, the class map {o.MemberCount}");

            if (occupied != o.ClassCount)
                Report(violations, $"overlay classes: {occupied} slots are occupied, the overlay counts {o.ClassCount} classes");

            // (3) The journal.
            var ids = j.Ids;
            var stamps = j.Timestamps;
            for (var row = 0; row < ids.Length; row++)
            {
                var id = ids[row];
                if (j.TryGetRow(id, out var found) == false || found != row)
                    Report(violations, $"journal: journaled id {id} maps to row {found}, not {row}");

                if (row > 0 && stamps[row] < stamps[row - 1])
                    Report(violations, $"journal: journal row {row} has timestamp {stamps[row]} below the previous row's {stamps[row - 1]}");

                if (f.Find(id) == id)
                    Report(violations, $"journal: journaled id {id} (row {row}) is canonical");

                if (DisplacedInParentAtCreation(id))
                    Report(violations, $"journal: journaled id {id} was already displaced in the parent's view when the context was created");

                if (blocks.Contains(id) && IsOwnDisplacement(id) == false)
                    Report(violations, $"local ids: local id {id} is journaled as displaced by the parent");
            }

            // (4) The cursor.
            var parentCount = parent!.DisplacedCount;
            if (unionFindCursor < parentDisplacedAtCreation || unionFindCursor > parentCount)
                Report(violations, $"cursor bounds: the cursor {unionFindCursor} is outside [{parentDisplacedAtCreation}, {parentCount}]");

            if (IsUnionFindCurrent == false)
                Report(violations, $"cursor bounds: the context is clean but its cursor {unionFindCursor} is behind the parent's {parentCount} displaced ids");

            if (journalMark > j.Count)
                Report(violations, $"cursor bounds: the journal mark {journalMark} is beyond the journal's {j.Count} rows");

            // Completeness: every parent displacement since creation stopped being canonical here and is journaled.
            var parentIds = parent.DisplacedIds;
            var suffix = new List<uint>();
            for (var i = parentDisplacedAtCreation; i < Math.Min(parentCount, parentIds.Length); i++)
            {
                var id = parentIds[i];
                suffix.Add(id);
                if (f.Find(id) == id)
                    Report(violations, $"journal: the parent displaced {id} but it is canonical here");
                else if (j.Contains(id) == false)
                    Report(violations, $"journal: the parent displaced {id} after this context was created, and it is not journaled");
            }

            // (2) Coarsening and minimality.
            foreach (var x in members)
                CheckCoarsening(violations, x, pf, f);

            foreach (var x in ids)
                CheckCoarsening(violations, x, pf, f);

            foreach (var x in suffix)
                CheckCoarsening(violations, x, pf, f);

            foreach (var x in SampleIds())
                CheckCoarsening(violations, x, pf, f);

            // (5) No ancestor holds an id local to this context.
            for (var level = 0; level < Depth; level++)
            {
                var a = ancestors[level];
                if (a.IsRoot)
                {
                    var root = store.RootUnionFind.UnionFind;
                    foreach (var id in root.Children)
                        if (blocks.Contains(id) || blocks.Contains(root.FindNaive(id)))
                            Report(violations, $"local ids: a root union-find row mentions an id local to the context at depth {Depth} ({id} → {root.FindNaive(id)})");

                    continue;
                }

                var ao = a.overlay!;
                for (var slot = 0; slot < ao.SlotCount; slot++)
                    foreach (var m in ao.MembersOf(slot))
                        if (blocks.Contains(m))
                            Report(violations, $"local ids: the overlay of the ancestor at depth {level} holds {m}, local to the context at depth {Depth}");

                foreach (var id in a.journal!.Ids)
                    if (blocks.Contains(id))
                        Report(violations, $"local ids: the journal of the ancestor at depth {level} holds {id}, local to the context at depth {Depth}");
            }
        }

        /// <summary>
        /// A frozen context's ancestors are frozen, and a frozen child's flattened map agrees with its chain.
        /// </summary>
        void CheckFrozenPrefix(List<string> violations)
        {
            if (frozen == false)
                return;

            for (var level = 0; level < Depth; level++)
                if (ancestors[level].frozen == false)
                    Report(violations, $"frozen prefix: the context is frozen but its ancestor at depth {level} is not");

            if (parent is null)
                return;

            var flat = LayeredUnionFind.Flattened(store.RootUnionFind.UnionFind, flattened);
            if (ReferenceEquals(flat.Prefix, FinderForChildren.Prefix) == false)
                Report(violations, "frozen prefix: the frozen context does not give its children its flattened find");

            var keys = Members(finder);
            finder.Prefix?.CollectKeys(keys);
            flattened?.CollectKeys(keys);
            keys.AddRange(SampleIds());

            foreach (var x in keys)
            {
                var chain = finder.Find(x);
                var flatFind = flat.Find(x);
                if (chain != flatFind)
                    Report(violations, $"frozen prefix: the flattened find maps {x} to {flatFind}, the chain to {chain}");
            }
        }

        /// <summary>
        /// Checks the find of one id: coarsening over the parent, minimality, and idempotence.
        /// </summary>
        static void CheckCoarsening(List<string> violations, uint x, LayeredUnionFind parentFind, LayeredUnionFind find)
        {
            var cx = find.Find(x);
            var px = parentFind.Find(x);
            if (find.Find(px) != cx)
                Report(violations, $"coarsening: find(find_P({x}) = {px}) = {find.Find(px)}, but find({x}) = {cx}");

            if (cx > x || cx > px)
                Report(violations, $"coarsening: find({x}) = {cx} is above {x} or its parent canonical {px}");

            if (find.Find(cx) != cx)
                Report(violations, $"coarsening: find({x}) = {cx} is not canonical itself");
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
        /// <param name="violations">The violations found so far.</param>
        /// <param name="message">The violation.</param>
        internal static void Report(List<string> violations, string message)
        {
            if (violations.Count < MaxReportedViolations)
                violations.Add(message);
            else if (violations.Count == MaxReportedViolations)
                violations.Add("... further violations omitted");
        }

    }

}
