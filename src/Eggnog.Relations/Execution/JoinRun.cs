using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The state of one join task: the trie node and binding of every atom and variable, the stack of factorized leaf
    /// scans, the materializations of a decomposed plan, the run-time stage order, and per-position scratch. egglog's
    /// <c>JoinState</c>, <c>BindingInfo</c> and the locals of <c>run_plan</c>
    /// (core-relations/src/free_join/execute.rs:654-1805).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The serial executor uses one instance for every rule of a rule set, and the database keeps it for the next rule
    /// set (<see cref="Rebind"/>, <see cref="Park"/>), so its node pool, per-node indexes, frame buffers and scratch are
    /// reused rather than reallocated per run; the parallel executor uses one per rule task and
    /// one per morsel (<see cref="CloneForMorsel"/>, egglog's shallow <c>BindingInfo</c> clone). Each rule starts with
    /// <see cref="BeginRule"/> (root nodes over every table, narrowed by the headers, then the existence checks) and
    /// ends with <see cref="EndRule"/>.
    /// </para>
    /// <para>
    /// Moving a node out of the bindings (egglog's <c>unwrap_val</c>) is reading its slot and nulling it; moving it back
    /// is writing it again. Bindings are never cleared: every frame overwrites the variables and atoms its stage
    /// touches, so values left by abandoned branches are never read.
    /// </para>
    /// <para>
    /// Memory: frames are replayed position by position, depth first, so each position owns one
    /// <see cref="FrameUpdates"/> buffer, one scratch node per atom for dense refinements (egglog reuses a uniquely
    /// owned node in <c>insert_subset</c>), probers and scan buffers, all reused. Nodes a frame creates for sparse
    /// refinements are released after the frame's recursion. In a parallel run, the state reachable from a hand-off
    /// must not change underneath the task. Hand-offs happen only at positions 0 and 1 and only for more than one
    /// frame, so a leaf scan's single frame is never handed off, and a task forked at position 1 can reach only what
    /// position 0 installed: the nodes installed while replaying frames at position 0 and the leaf-scan rows of
    /// position 0 are fresh and never reused or released (the garbage collector reclaims them, as it does the root
    /// nodes and the materializations of a parallel rule), and the nodes' caches are published thread-safely.
    /// </para>
    /// <para>
    /// Not thread-safe; one thread drives an instance at a time.
    /// </para>
    /// </remarks>
    internal sealed partial class JoinRun
    {

        /// <summary>
        /// The number of frames a stage buffers before draining: egglog's default <c>morsel_size</c>.
        /// </summary>
        public const int DefaultMorselSize = 256;

        /// <summary>
        /// Candidate subsets of at most this many rows are copied and refined in every frame; larger ones go through
        /// the parent node's child cache (core-relations/src/free_join/execute.rs:1124).
        /// </summary>
        const int SmallRefinement = 16;

        /// <summary>
        /// A stage enumerating more than this many candidates may re-sort the stages after it
        /// (core-relations/src/free_join/execute.rs:974).
        /// </summary>
        const int ResortThreshold = 32;

        /// <summary>
        /// The positions of a subset an existence check scans per call.
        /// </summary>
        const int ExistenceChunk = 64;

        readonly Database db;
        ExecutionSignals signals;
        readonly bool parallel;
        readonly TrieNodePool pool;
        readonly FrameUpdatesPool? framePool;

        // Rule state.
        PlanTemplate? template;
        int atomCount;
        Table[] tables = Array.Empty<Table>();
        TableInfo[] infos = Array.Empty<TableInfo>();
        TrieNode?[] nodes = Array.Empty<TrieNode?>();
        TrieNode?[] roots = Array.Empty<TrieNode?>();
        uint[] values = Array.Empty<uint>();
        Materialization?[] materializations = Array.Empty<Materialization?>();
        Variable[] boundVariables = Array.Empty<Variable>();
        bool[] boundMask = Array.Empty<bool>();
        BindingSet[] bindingSets = new BindingSet[4];
        int bindingSetCount;

        // Block state.
        JoinStage[] stages = Array.Empty<JoinStage>();
        PreparedBlock? prepared;
        int target;
        InstrOrder order = new(0);
        bool[] leafScans = Array.Empty<bool>();

        // Per-position scratch.
        FrameUpdates?[] frames = Array.Empty<FrameUpdates?>();
        TrieNode?[]?[] scratch = Array.Empty<TrieNode?[]?>();
        Prober[]?[] probers = Array.Empty<Prober[]?>();
        TaggedRowBuffer?[] scanBuffers = Array.Empty<TaggedRowBuffer?>();
        TaggedRowBuffer?[] leafBuffers = Array.Empty<TaggedRowBuffer?>();
        long[] timesRefined = Array.Empty<long>();
        int[] probedBy = Array.Empty<int>();
        uint[] keyScratch = new uint[4];
        uint[] rowScratch = new uint[4];
        SparseColumnIndex[] sparseFree = new SparseColumnIndex[8];
        int sparseFreeCount;

        /// <summary>
        /// Initializes an idle join task.
        /// </summary>
        /// <param name="database">The database whose tables are joined.</param>
        /// <param name="signals">The stop signal shared by every state of the run.</param>
        /// <param name="parallel">Whether the run hands work to other tasks, so shared state must not be reused.</param>
        /// <param name="framePool">The run's recycled frame buffers, for a parallel run; <c>null</c> otherwise.</param>
        public JoinRun(Database database, ExecutionSignals signals, bool parallel, FrameUpdatesPool? framePool = null)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(signals);
            db = database;
            this.signals = signals;
            this.parallel = parallel;
            this.framePool = framePool;
            pool = new TrieNodePool(parallel);
        }

        /// <summary>
        /// Gets the current bindings, indexed by variable. Sinks read the matched values from it.
        /// </summary>
        public uint[] Values => values;

        /// <summary>
        /// Gets the variables bound by the blocks run so far in the current rule, ascending: what egglog's
        /// <c>DenseIdMap</c> of bindings holds when a match reaches the action, so the first match of a batch maps each.
        /// </summary>
        public Variable[] BoundVariables => boundVariables;

        /// <summary>
        /// Gets the number of rows of <paramref name="atom"/>'s current node, for <see cref="JoinSizes"/>.
        /// </summary>
        /// <param name="atom">The atom's index.</param>
        /// <returns>The size.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetAtomSize(int atom)
        {
            var node = nodes[atom];
            Debug.Assert(node is not null, "a stage that is sorted or estimated read an atom whose node is moved out");
            return node!.Size;
        }

        /// <summary>
        /// Gets the number of keys of materialization <paramref name="mat"/>, for <see cref="JoinSizes"/>.
        /// </summary>
        /// <param name="mat">The materialization's index.</param>
        /// <returns>The size.</returns>
        public int GetMaterializationSize(int mat) => materializations[mat]!.Count;

        /// <summary>
        /// Sets up the current rule: a root node over every row of each atom's table, narrowed by every header subset of
        /// the rule's instance, then the existence checks of the atoms without variables. egglog's opening of each plan
        /// in <c>run_rule_set</c> (core-relations/src/free_join/execute.rs:517-539), plus the fix of map §8.1 row 2.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <returns><c>false</c> if the rule cannot match: it has no instance, a header or root is empty, or an atom
        /// without variables has no visible row satisfying its constraints. Call <see cref="EndRule"/> either way.</returns>
        public bool BeginRule(RulePlan rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            var plan = rule.Template;
            template = plan;
            var atoms = plan.Atoms;
            atomCount = atoms.IdCount;
            EnsureAtoms(atomCount);
            for (var a = 0; a < atomCount; a++)
            {
                if (atoms.TryGetValue(new AtomId(a), out var info) == false)
                    continue;

                var tableInfo = db.GetTableInfo(info.Table);
                infos[a] = tableInfo;
                tables[a] = tableInfo.Table;
                var root = pool.Rent(tableInfo.Table.All());
                roots[a] = root;
                nodes[a] = root;
            }

            if (values.Length < plan.VariableCount)
                values = new uint[plan.VariableCount];

            if (boundMask.Length < plan.VariableCount)
                boundMask = new bool[plan.VariableCount];
            else
                Array.Clear(boundMask);

            boundVariables = Array.Empty<Variable>();
            bindingSetCount = 0;
            var instance = rule.Instance;
            if (instance is null)
                return false;

            // Several headers on one atom compose by intersection. Root nodes are not shared yet, so they are narrowed
            // in place (egglog's Arc::try_unwrap).
            var headers = instance.Headers;
            var subsets = instance.HeaderSubsets;
            for (var h = 0; h < headers.Length; h++)
            {
                if (subsets[h].IsEmpty)
                    return false;

                var node = nodes[headers[h].Atom.Index]!;
                node.Intersect(subsets[h].AsRef());
                if (node.IsEmpty)
                    return false;
            }

            // Fix (map §8.1 row 2, core-relations/src/free_join/plan.rs:1502-1505): no stage scans an atom without
            // variables, and egglog only checks that its header subset is non-empty, which may hold only tombstones and
            // ignores its slow constraints. Check that a visible row satisfying them exists.
            foreach (var check in plan.ExistenceChecks)
                if (ExistsVisibleRow(check) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Ends the current rule: releases the root nodes and their caches, the scratch nodes' caches and the
        /// materializations, unless the run is parallel, where tasks may still read them and the garbage collector
        /// reclaims them instead.
        /// </summary>
        public void EndRule()
        {
            if (parallel == false)
            {
                for (var a = 0; a < atomCount; a++)
                    roots[a]?.Release(pool);

                ResetScratch();
                for (var i = 0; i < materializations.Length; i++)
                    materializations[i]?.Dispose();

                Array.Clear(materializations);
            }
            else
            {
                // Morsels of the result block may still read the materializations: drop the array, not its contents.
                materializations = Array.Empty<Materialization?>();
            }

            Array.Clear(roots, 0, atomCount);
            Array.Clear(nodes, 0, atomCount);
            Array.Clear(bindingSets, 0, bindingSetCount);
            bindingSetCount = 0;
            for (var p = 0; p < frames.Length; p++)
                frames[p]?.Clear();

            template = null;
        }

        /// <summary>
        /// Creates the state a morsel task starts from: egglog's <c>clone_state</c>
        /// (core-relations/src/free_join/execute.rs:2546-2554). The bindings, nodes and binding-set stack are copied
        /// (the nodes and leaf rows themselves are shared, and immutable from now on), the stage order and leaf-scan
        /// flags are copied, the materializations are shared, and the scratch is the task's own.
        /// </summary>
        /// <returns>The task's join.</returns>
        public JoinRun CloneForMorsel()
        {
            var child = new JoinRun(db, signals, parallel: true, framePool)
            {
                template = template,
                atomCount = atomCount,
                tables = tables,
                infos = infos,
                nodes = (TrieNode?[])nodes.Clone(),
                roots = new TrieNode?[atomCount],
                values = (uint[])values.Clone(),
                materializations = materializations,
                boundVariables = boundVariables,
                boundMask = boundMask,
                bindingSets = new BindingSet[Math.Max(4, bindingSetCount)],
                bindingSetCount = bindingSetCount,
                stages = stages,
                prepared = prepared,
                target = target,
                order = order.Clone(),
                leafScans = (bool[])leafScans.Clone(),
            };

            Array.Copy(bindingSets, child.bindingSets, bindingSetCount);
            child.EnsurePositions(stages.Length);
            if (prepared is not null)
                child.EnsureKeyScratch(prepared);

            return child;
        }

        /// <summary>
        /// Checks that some visible row of the atom's current node satisfies the check's slow constraints.
        /// </summary>
        bool ExistsVisibleRow(ExistenceCheck check)
        {
            var atom = check.Atom.Index;
            var rows = nodes[atom]!.Rows;
            var table = tables[atom];
            var sink = new ExistenceSink();
            var next = 0;
            do
            {
                next = table.ScanGenericBounded(rows, next, ExistenceChunk, check.SlowConstraints, ref sink);
                if (sink.Found)
                    return true;
            }
            while (next >= 0);

            return false;
        }

        /// <summary>
        /// Resets the caches of every scratch node, so their indexes and children go back to the pools.
        /// </summary>
        void ResetScratch()
        {
            for (var p = 0; p < scratch.Length; p++)
            {
                var row = scratch[p];
                if (row is null)
                    continue;

                foreach (var node in row)
                    node?.ResetCaches(pool);
            }
        }

        /// <summary>
        /// Makes the per-atom arrays hold at least <paramref name="count"/> atoms.
        /// </summary>
        void EnsureAtoms(int count)
        {
            if (nodes.Length >= count)
                return;

            tables = new Table[count];
            infos = new TableInfo[count];
            nodes = new TrieNode?[count];
            roots = new TrieNode?[count];
            timesRefined = new long[count];
            probedBy = new int[count];
            for (var p = 0; p < scratch.Length; p++)
                scratch[p] = null;
        }

        /// <summary>
        /// Makes the per-position arrays hold at least <paramref name="count"/> positions.
        /// </summary>
        void EnsurePositions(int count)
        {
            if (timesRefined.Length < atomCount)
            {
                timesRefined = new long[atomCount];
                probedBy = new int[atomCount];
            }

            if (frames.Length >= count)
                return;

            Array.Resize(ref frames, count);
            Array.Resize(ref scratch, count);
            Array.Resize(ref probers, count);
            Array.Resize(ref scanBuffers, count);
            Array.Resize(ref leafBuffers, count);
        }

    }

}
