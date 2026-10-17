using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// What the layered executor did to the rows of each kind of layer: rows its cover scans, drivers and existence
    /// checks enumerated, rows it put into indexes built for one stage or node (sparse, per-node and per-stage tuple
    /// indexes), cached-index pieces, and probes joined across layers, split between the context's own layer and its
    /// ancestors' layers; and the execution states it cloned, by site, with the mutation buffers they made and the
    /// allocations and growths of their buffer maps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-child cost must not grow with the root: a child's run may touch an ancestor's rows only through shared
    /// cached indexes (free), through the rows its own work reaches (probe results and their refinements, at most what
    /// it probed), and through the rows of a semi-naive variant's new side (<c>ts ≥ W</c>). So ancestor rows are
    /// counted in two kinds. <see cref="AncestorRowsScanned"/> and <see cref="AncestorDynamicIndexRows"/> count
    /// ancestor rows of an atom's root node (the layer's rows narrowed by the headers, before any refinement), which is
    /// the work that would grow with the root: once a rule's watermark is at or above every root stamp, both are 0 for
    /// a child's run. <see cref="AncestorRefinedRowsScanned"/> and <see cref="AncestorRefinedIndexRows"/> count
    /// ancestor rows reached through a refinement, bounded by the run's probe results.
    /// </para>
    /// <para>
    /// Off by default and free when off: every counting site tests <see cref="Enabled"/>, a static read-only field the
    /// JIT folds, so a run without <c>EGGNOG_COUNTERS</c> carries no counting code. With the environment variable set (to
    /// anything but <c>0</c> or <c>false</c>) each <see cref="Database"/> keeps one instance
    /// (<see cref="Database.ExecutorCounts"/>) that its runs add to, with interlocked adds since a parallel run's tasks
    /// count at once. The counts are per stage call, per index build, per clone or per buffer made, never per row.
    /// </para>
    /// <para>
    /// The clone counts cover every <see cref="ExecutionState"/> clone over a database view, whatever runs it: a rule
    /// set's action batches, and the parallel inserts and rebuilds of its merge. A state's mutation buffers are made in
    /// two ways: a clone takes a fresh handle of each buffer its parent holds (<see cref="BuffersInherited"/>), and any
    /// state makes one on its first write to a table it holds none for (<see cref="BuffersCreated"/>). The second kind
    /// records the buffer in the state's buffer map, a <see cref="DenseIdMap{TKey, TValue}"/> indexed by table id that
    /// a clone sizes to its parent's. A record that finds the map without arrays allocates its first ones
    /// (<see cref="BufferMapAllocations"/>); a record past the end of the arrays it has replaces them with larger ones
    /// (<see cref="BufferMapGrowths"/>). Sizing a clone's map up front would avoid only the growths: an empty map's
    /// first arrays are allocated either way.
    /// </para>
    /// <para>
    /// A row is an ancestor's when its layer is below the running context's depth; a root's run has only its own layer.
    /// </para>
    /// </remarks>
    internal sealed class ExecutorCounters
    {

        /// <summary>
        /// Whether counting is on: the <c>EGGNOG_COUNTERS</c> environment variable is set to anything but <c>0</c> or
        /// <c>false</c> when the type initializes.
        /// </summary>
        public static readonly bool Enabled = IsOn(Environment.GetEnvironmentVariable("EGGNOG_COUNTERS"));

        long ownRowsScanned;
        long ancestorRowsScanned;
        long ancestorRefinedRowsScanned;
        long ownIndexRows;
        long ancestorIndexRows;
        long ancestorRefinedIndexRows;
        long cachedPieces;
        long ancestorCachedPieces;
        long layeredProbes;
        long rules;
        readonly long[] clones = new long[CloneSiteCount];
        long buffersCreated;
        long buffersInherited;
        long bufferMapAllocations;
        long bufferMapGrowths;

        /// <summary>
        /// The number of <see cref="ExecutionStateCloneSite"/> values.
        /// </summary>
        static readonly int CloneSiteCount = Enum.GetValues<ExecutionStateCloneSite>().Length;

        /// <summary>
        /// Gets the rows of the context's own layer that scans, drivers and existence checks enumerated.
        /// </summary>
        public long OwnRowsScanned => Interlocked.Read(ref ownRowsScanned);

        /// <summary>
        /// Gets the ancestor rows of root nodes (narrowed by headers only) that scans, drivers and existence checks
        /// enumerated: 0 for a child's semi-naive run whose watermark is at or above every root stamp.
        /// </summary>
        public long AncestorRowsScanned => Interlocked.Read(ref ancestorRowsScanned);

        /// <summary>
        /// Gets the ancestor rows of refined nodes (probe results and their refinements) that scans and drivers enumerated.
        /// </summary>
        public long AncestorRefinedRowsScanned => Interlocked.Read(ref ancestorRefinedRowsScanned);

        /// <summary>
        /// Gets the rows of the own layer put into dynamic indexes.
        /// </summary>
        public long OwnDynamicIndexRows => Interlocked.Read(ref ownIndexRows);

        /// <summary>
        /// Gets the ancestor rows of root nodes put into dynamic indexes: 0 once the watermark is at or above every root
        /// stamp.
        /// </summary>
        public long AncestorDynamicIndexRows => Interlocked.Read(ref ancestorIndexRows);

        /// <summary>
        /// Gets the ancestor rows of refined nodes put into dynamic indexes.
        /// </summary>
        public long AncestorRefinedIndexRows => Interlocked.Read(ref ancestorRefinedIndexRows);

        /// <summary>
        /// Gets the prober pieces that read a layer's cached index (no build in the run).
        /// </summary>
        public long CachedIndexPieces => Interlocked.Read(ref cachedPieces);

        /// <summary>
        /// Gets the cached-index pieces over ancestor layers: shared indexes, at zero build cost to the child.
        /// </summary>
        public long AncestorCachedIndexPieces => Interlocked.Read(ref ancestorCachedPieces);

        /// <summary>
        /// Gets the probes of probers with several pieces, joined across layers.
        /// </summary>
        public long LayeredProbes => Interlocked.Read(ref layeredProbes);

        /// <summary>
        /// Gets the rules the executor began.
        /// </summary>
        public long Rules => Interlocked.Read(ref rules);

        /// <summary>
        /// Gets the execution states cloned, at every site.
        /// </summary>
        public long TotalClones
        {
            get
            {
                var total = 0L;
                for (var i = 0; i < clones.Length; i++)
                    total += Interlocked.Read(ref clones[i]);

                return total;
            }
        }

        /// <summary>
        /// Gets the mutation buffers execution states made on their first write to a table they held none for.
        /// </summary>
        public long BuffersCreated => Interlocked.Read(ref buffersCreated);

        /// <summary>
        /// Gets the mutation buffers clones took as fresh handles of their parents' buffers.
        /// </summary>
        public long BuffersInherited => Interlocked.Read(ref buffersInherited);

        /// <summary>
        /// Gets the buffers made on first write whose record found the state's buffer map without arrays, and allocated
        /// its first two.
        /// </summary>
        public long BufferMapAllocations => Interlocked.Read(ref bufferMapAllocations);

        /// <summary>
        /// Gets the buffers made on first write whose record outgrew the arrays the state's buffer map already had:
        /// each growth replaces both arrays with larger ones.
        /// </summary>
        public long BufferMapGrowths => Interlocked.Read(ref bufferMapGrowths);

        /// <summary>
        /// Gets the execution states cloned at <paramref name="site"/>.
        /// </summary>
        /// <param name="site">The clone site.</param>
        /// <returns>The clones.</returns>
        public long Clones(ExecutionStateCloneSite site) => Interlocked.Read(ref clones[(int)site]);

        /// <summary>
        /// Counts <paramref name="rows"/> rows a scan, a driver or an existence check enumerated in a layer.
        /// </summary>
        /// <param name="ancestor">Whether the layer is an ancestor's.</param>
        /// <param name="root">Whether the rows are a root node's (narrowed by headers only).</param>
        /// <param name="rows">The rows enumerated.</param>
        public void NoteScanned(bool ancestor, bool root, long rows)
        {
            if (ancestor == false)
                Interlocked.Add(ref ownRowsScanned, rows);
            else if (root)
                Interlocked.Add(ref ancestorRowsScanned, rows);
            else
                Interlocked.Add(ref ancestorRefinedRowsScanned, rows);
        }

        /// <summary>
        /// Counts <paramref name="rows"/> rows given to a dynamic index build over a layer.
        /// </summary>
        /// <param name="ancestor">Whether the layer is an ancestor's.</param>
        /// <param name="root">Whether the rows are a root node's (narrowed by headers only).</param>
        /// <param name="rows">The rows indexed.</param>
        public void NoteDynamicIndex(bool ancestor, bool root, long rows)
        {
            if (ancestor == false)
                Interlocked.Add(ref ownIndexRows, rows);
            else if (root)
                Interlocked.Add(ref ancestorIndexRows, rows);
            else
                Interlocked.Add(ref ancestorRefinedIndexRows, rows);
        }

        /// <summary>
        /// Counts a prober piece that reads a layer's cached index.
        /// </summary>
        /// <param name="ancestor">Whether the layer is an ancestor's.</param>
        public void NoteCachedPiece(bool ancestor)
        {
            Interlocked.Increment(ref cachedPieces);
            if (ancestor)
                Interlocked.Increment(ref ancestorCachedPieces);
        }

        /// <summary>
        /// Counts a probe joined across layers.
        /// </summary>
        public void NoteLayeredProbe() => Interlocked.Increment(ref layeredProbes);

        /// <summary>
        /// Counts a rule the executor began.
        /// </summary>
        public void NoteRule() => Interlocked.Increment(ref rules);

        /// <summary>
        /// Counts an execution state cloned at <paramref name="site"/>, with the <paramref name="inherited"/> buffers it
        /// took from its parent.
        /// </summary>
        /// <param name="site">The clone site.</param>
        /// <param name="inherited">The fresh handles the clone took of its parent's buffers.</param>
        public void NoteClone(ExecutionStateCloneSite site, int inherited)
        {
            Interlocked.Increment(ref clones[(int)site]);
            if (inherited > 0)
                Interlocked.Add(ref buffersInherited, inherited);
        }

        /// <summary>
        /// Counts a mutation buffer an execution state made on its first write to a table.
        /// </summary>
        /// <param name="capacityBefore">The slots of the state's buffer map before the buffer was recorded.</param>
        /// <param name="capacityAfter">The slots of the state's buffer map after the buffer was recorded.</param>
        public void NoteBufferCreated(int capacityBefore, int capacityAfter)
        {
            Interlocked.Increment(ref buffersCreated);
            if (capacityAfter == capacityBefore)
                return;

            if (capacityBefore == 0)
                Interlocked.Increment(ref bufferMapAllocations);
            else
                Interlocked.Increment(ref bufferMapGrowths);
        }

        /// <summary>
        /// Sets every count to zero.
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref ownRowsScanned, 0);
            Interlocked.Exchange(ref ancestorRowsScanned, 0);
            Interlocked.Exchange(ref ancestorRefinedRowsScanned, 0);
            Interlocked.Exchange(ref ownIndexRows, 0);
            Interlocked.Exchange(ref ancestorIndexRows, 0);
            Interlocked.Exchange(ref ancestorRefinedIndexRows, 0);
            Interlocked.Exchange(ref cachedPieces, 0);
            Interlocked.Exchange(ref ancestorCachedPieces, 0);
            Interlocked.Exchange(ref layeredProbes, 0);
            Interlocked.Exchange(ref rules, 0);
            for (var i = 0; i < clones.Length; i++)
                Interlocked.Exchange(ref clones[i], 0);

            Interlocked.Exchange(ref buffersCreated, 0);
            Interlocked.Exchange(ref buffersInherited, 0);
            Interlocked.Exchange(ref bufferMapAllocations, 0);
            Interlocked.Exchange(ref bufferMapGrowths, 0);
        }

        /// <inheritdoc />
        public override string ToString() =>
            $"rules {Rules}; scanned own {OwnRowsScanned}, ancestor {AncestorRowsScanned} (refined {AncestorRefinedRowsScanned}); " +
            $"dynamic-index rows own {OwnDynamicIndexRows}, ancestor {AncestorDynamicIndexRows} (refined {AncestorRefinedIndexRows}); " +
            $"cached pieces {CachedIndexPieces} (ancestor {AncestorCachedIndexPieces}); layered probes {LayeredProbes}; " +
            $"clones {TotalClones}{ClonesBySite()}; buffers created {BuffersCreated}, inherited {BuffersInherited}; " +
            $"buffer maps allocated {BufferMapAllocations}, grown {BufferMapGrowths}";

        /// <summary>
        /// Lists the clone counts of the sites with any, as <c>site count</c> pairs in parentheses after a space, or
        /// nothing when no site has any.
        /// </summary>
        string ClonesBySite()
        {
            var parts = new List<string>(CloneSiteCount);
            for (var i = 0; i < clones.Length; i++)
            {
                var count = Interlocked.Read(ref clones[i]);
                if (count > 0)
                    parts.Add($"{(ExecutionStateCloneSite)i} {count}");
            }

            return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
        }

        /// <summary>
        /// Reads the environment variable's value.
        /// </summary>
        static bool IsOn(string? value) =>
            string.IsNullOrWhiteSpace(value) == false && value.Trim() != "0" && value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) == false;

    }

}
