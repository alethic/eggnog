using System;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// What the layered executor did to the rows of each kind of layer (colored-contexts spec §14.2, §17 M10.2): rows its
    /// cover scans, drivers and existence checks enumerated, rows it put into indexes built for one stage or node (sparse,
    /// per-node and per-stage tuple indexes), cached-index pieces, and probes joined across layers, split between the
    /// context's own layer and its ancestors' layers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-child cost must not grow with the root (§14.2): a child's run may touch an ancestor's rows only through shared
    /// cached indexes (free), through the rows its own work reaches (probe results and their refinements, at most what it
    /// probed), and through the rows of a semi-naive variant's new side (<c>ts ≥ W</c>). So ancestor rows are counted in
    /// two kinds. <see cref="AncestorRowsScanned"/> and <see cref="AncestorDynamicIndexRows"/> count ancestor rows of an
    /// atom's root node (the layer's rows narrowed by the headers, before any refinement), which is the work that would
    /// grow with the root: once a rule's watermark is at or above every root stamp, both are 0 for a child's run (the done
    /// condition of M10.2). <see cref="AncestorRefinedRowsScanned"/> and <see cref="AncestorRefinedIndexRows"/> count
    /// ancestor rows reached through a refinement, bounded by the run's probe results.
    /// </para>
    /// <para>
    /// Off by default and free when off: every counting site tests <see cref="Enabled"/>, a static read-only field the
    /// JIT folds, so a run without <c>EGGNOG_COUNTERS</c> carries no counting code. With the environment variable set (to
    /// anything but <c>0</c> or <c>false</c>) each <see cref="Database"/> keeps one instance
    /// (<see cref="Database.ExecutorCounts"/>) that its runs add to, with interlocked adds since a parallel run's tasks
    /// count at once. The counts are per stage call or per index build, never per row.
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
        }

        /// <inheritdoc />
        public override string ToString() =>
            $"rules {Rules}; scanned own {OwnRowsScanned}, ancestor {AncestorRowsScanned} (refined {AncestorRefinedRowsScanned}); " +
            $"dynamic-index rows own {OwnDynamicIndexRows}, ancestor {AncestorDynamicIndexRows} (refined {AncestorRefinedIndexRows}); " +
            $"cached pieces {CachedIndexPieces} (ancestor {AncestorCachedIndexPieces}); layered probes {LayeredProbes}";

        /// <summary>
        /// Reads the environment variable's value.
        /// </summary>
        static bool IsOn(string? value) =>
            string.IsNullOrWhiteSpace(value) == false && value.Trim() != "0" && value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) == false;

    }

}
