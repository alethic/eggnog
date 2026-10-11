using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A term graph for <see cref="TermGraph"/>, a batch of unions over its leaves, and the congruence closure they
    /// produce, generated from a seed: the input and the reference model of <see cref="DisplacedRebuildBenchmarks"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For <c>rows</c> rows: a fifth of them as many leaf ids; three fifths <c>pair(x, y)</c> rows, each argument a
    /// random leaf or (with probability one half) any id made so far, so terms nest; a fifth <c>wrap(x)</c> rows over
    /// distinct ids; and a fifth <c>low(x) = v</c> rows over distinct ids. Every pair and wrap row gets a fresh output
    /// id, in generation order after the leaves; keys are distinct, so loading merges nothing. The batch is
    /// <see cref="UnionsFor"/> unions of distinct random leaves.
    /// </para>
    /// <para>
    /// The reference model is a naive congruence closure: a min-wins union-find over every id, applying the unions,
    /// then canonicalizing every constructor row and unioning the outputs of rows whose canonical keys collide, until
    /// a pass unions nothing. Both union-finds keep the smallest id of a class canonical, so the engine must end with
    /// exactly the reference's canonical id for every id, its row counts and <c>low</c>'s minimum per class, whatever
    /// order its passes and parallel merges take.
    /// </para>
    /// </remarks>
    internal sealed class TermGraphWorkload
    {

        /// <summary>
        /// The values <c>low</c> rows take, in [0, LowValueRange).
        /// </summary>
        const int LowValueRange = 1 << 20;

        readonly uint[] pairs;
        readonly uint[] wraps;
        readonly uint[] lows;
        readonly uint[] unions;
        readonly uint[] canonical;
        readonly Dictionary<uint, uint> expectedLow;

        /// <summary>
        /// Initializes a new instance over generated rows, and computes the reference closure.
        /// </summary>
        TermGraphWorkload(int idCount, uint[] pairs, uint[] wraps, uint[] lows, uint[] unions)
        {
            IdCount = idCount;
            this.pairs = pairs;
            this.wraps = wraps;
            this.lows = lows;
            this.unions = unions;
            canonical = new uint[idCount];
            expectedLow = new Dictionary<uint, uint>();
            Close();
        }

        /// <summary>
        /// Gets the number of ids: the ids are 0 to <c>IdCount - 1</c>.
        /// </summary>
        public int IdCount { get; }

        /// <summary>
        /// Gets the number of unions in the batch.
        /// </summary>
        public int UnionCount => unions.Length / 2;

        /// <summary>
        /// Gets the number of ids the closure displaces: the union-find's row count after the rebuild.
        /// </summary>
        public int ExpectedDisplaced { get; private set; }

        /// <summary>
        /// Gets the number of live <c>pair</c> rows after the rebuild.
        /// </summary>
        public int ExpectedPairs { get; private set; }

        /// <summary>
        /// Gets the number of live <c>wrap</c> rows after the rebuild.
        /// </summary>
        public int ExpectedWraps { get; private set; }

        /// <summary>
        /// Gets the number of live <c>low</c> rows after the rebuild.
        /// </summary>
        public int ExpectedLows => expectedLow.Count;

        /// <summary>
        /// Gets the number of unions in the batch for <paramref name="rows"/> rows: one per fifty rows, at least one.
        /// </summary>
        /// <param name="rows">The number of rows.</param>
        /// <returns>The union count.</returns>
        public static int UnionsFor(int rows) => Math.Max(1, rows / 50);

        /// <summary>
        /// Generates a term graph of about <paramref name="rows"/> rows and its union batch from
        /// <paramref name="seed"/>, and closes it. O(rows) expected, times the closure's passes.
        /// </summary>
        /// <param name="rows">The number of function rows; at least 20.</param>
        /// <param name="seed">The random seed.</param>
        /// <returns>The workload.</returns>
        public static TermGraphWorkload Generate(int rows, int seed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(rows, 20);

            var random = new Random(seed);
            var leaves = rows / 5;
            var pairCount = 3 * rows / 5;
            var wrapCount = rows / 5;
            var lowCount = rows - pairCount - wrapCount;
            var next = (uint)leaves;

            var pairs = new uint[pairCount * 3];
            var pairKeys = new HashSet<(uint, uint)>(pairCount);
            for (var i = 0; i < pairCount; i++)
            {
                uint x, y;
                do
                {
                    x = Pick(random, leaves, next);
                    y = Pick(random, leaves, next);
                }
                while (pairKeys.Add((x, y)) == false);

                pairs[3 * i] = x;
                pairs[3 * i + 1] = y;
                pairs[3 * i + 2] = next++;
            }

            var wraps = new uint[wrapCount * 2];
            var wrapKeys = new HashSet<uint>(wrapCount);
            for (var i = 0; i < wrapCount; i++)
            {
                uint x;
                do
                    x = (uint)random.Next((int)next);
                while (wrapKeys.Add(x) == false);

                wraps[2 * i] = x;
                wraps[2 * i + 1] = next++;
            }

            var lows = new uint[lowCount * 2];
            var lowKeys = new HashSet<uint>(lowCount);
            for (var i = 0; i < lowCount; i++)
            {
                uint x;
                do
                    x = (uint)random.Next((int)next);
                while (lowKeys.Add(x) == false);

                lows[2 * i] = x;
                lows[2 * i + 1] = (uint)random.Next(LowValueRange);
            }

            var unionCount = UnionsFor(rows);
            var unions = new uint[unionCount * 2];
            for (var i = 0; i < unionCount; i++)
            {
                uint a, b;
                do
                {
                    a = (uint)random.Next(leaves);
                    b = (uint)random.Next(leaves);
                }
                while (a == b);

                unions[2 * i] = a;
                unions[2 * i + 1] = b;
            }

            return new TermGraphWorkload((int)next, pairs, wraps, lows, unions);
        }

        /// <summary>
        /// Loads the term graph into an empty <paramref name="graph"/>: every row stamped with the clock (0), one
        /// merge, then the clock advances, as the bridge's <c>flush_updates</c> does.
        /// </summary>
        /// <param name="graph">An empty e-graph.</param>
        public void Load(TermGraph graph)
        {
            ArgumentNullException.ThrowIfNull(graph);

            var db = graph.Db;
            var ts = graph.Timestamp;
            var pairBuffer = db.NewBuffer(graph.Pair);
            for (var i = 0; i < pairs.Length; i += 3)
                pairBuffer.StageInsert([pairs[i], pairs[i + 1], pairs[i + 2], ts]);

            pairBuffer.Flush();
            var wrapBuffer = db.NewBuffer(graph.Wrap);
            for (var i = 0; i < wraps.Length; i += 2)
                wrapBuffer.StageInsert([wraps[i], wraps[i + 1], ts]);

            wrapBuffer.Flush();
            var lowBuffer = db.NewBuffer(graph.Low);
            for (var i = 0; i < lows.Length; i += 2)
                lowBuffer.StageInsert([lows[i], lows[i + 1], ts]);

            lowBuffer.Flush();
            db.MergeAll(ts);
            db.IncrementCounter(graph.Clock);
            if (graph.UnionFind.Count != 0)
                throw new InvalidOperationException("Loading the term graph unioned ids.");
        }

        /// <summary>
        /// Applies the union batch and rebuilds: stages every union stamped with the clock, merges (the union-find's
        /// <see cref="DisplacedTable.Merge"/>), advances the clock, and runs the bridge's native rebuild loop over the
        /// function tables (<see cref="Database.Rebuild"/>) until a pass changes nothing.
        /// </summary>
        /// <param name="graph">An e-graph <see cref="Load"/> filled.</param>
        /// <returns>The number of rebuild passes.</returns>
        public int UnionAndRebuild(TermGraph graph)
        {
            ArgumentNullException.ThrowIfNull(graph);

            var db = graph.Db;
            var ts = graph.Timestamp;
            var buffer = db.NewBuffer(graph.Uf);
            for (var i = 0; i < unions.Length; i += 2)
                buffer.StageInsert([unions[i], unions[i + 1], ts]);

            buffer.Flush();
            db.MergeAll(ts);
            db.IncrementCounter(graph.Clock);
            return db.Rebuild(graph.Uf, graph.FunctionTables, graph.Clock);
        }

        /// <summary>
        /// Checks <paramref name="graph"/> against the reference closure: the canonical id of every id, the union-find's
        /// size, and every table's live rows (count, canonical id cells, and <c>low</c>'s minimum per class).
        /// O(ids + rows).
        /// </summary>
        /// <param name="graph">The e-graph after <see cref="UnionAndRebuild"/>.</param>
        /// <exception cref="InvalidOperationException">The e-graph differs from the reference closure.</exception>
        public void Verify(TermGraph graph)
        {
            ArgumentNullException.ThrowIfNull(graph);

            var uf = graph.UnionFind;
            if (uf.Count != ExpectedDisplaced)
                throw new InvalidOperationException($"The union-find displaced {uf.Count} ids; the reference closure displaces {ExpectedDisplaced}.");

            for (var id = 0; id < IdCount; id++)
            {
                var found = uf.FindNaive((uint)id);
                if (found != canonical[id])
                    throw new InvalidOperationException($"Id {id} is in the class of {found}; the reference closure puts it in the class of {canonical[id]}.");
            }

            CheckRows(graph, graph.Pair, "pair", 3, ExpectedPairs);
            CheckRows(graph, graph.Wrap, "wrap", 2, ExpectedWraps);
            var lowRows = CheckRows(graph, graph.Low, "low", 1, ExpectedLows);
            foreach (var row in lowRows)
            {
                if (expectedLow.TryGetValue(row[0], out var min) == false)
                    throw new InvalidOperationException($"low({row[0]}) = {row[1]} has no class in the reference closure.");

                if (row[1] != min)
                    throw new InvalidOperationException($"low({row[0]}) = {row[1]}; the reference closure's minimum is {min}.");
            }
        }

        /// <summary>
        /// Checks that <paramref name="table"/> holds <paramref name="expected"/> live rows whose first
        /// <paramref name="idColumns"/> cells are canonical.
        /// </summary>
        /// <returns>The live rows.</returns>
        static List<uint[]> CheckRows(TermGraph graph, TableId table, string name, int idColumns, int expected)
        {
            var uf = graph.UnionFind;
            var rows = RowCollector.LiveRows(graph.Db.GetTable(table));
            if (rows.Count != expected)
                throw new InvalidOperationException($"{name} holds {rows.Count} live rows after the rebuild; the reference closure has {expected}.");

            foreach (var row in rows)
                for (var c = 0; c < idColumns; c++)
                    if (uf.FindNaive(row[c]) != row[c])
                        throw new InvalidOperationException(
                            $"The {name} row ({string.Join(", ", row)}) holds the non-canonical id {row[c]} after the rebuild.");

            return rows;
        }

        /// <summary>
        /// Picks a pair argument: a random leaf, or with probability one half any id made so far.
        /// </summary>
        static uint Pick(Random random, int leaves, uint made)
        {
            return random.Next(2) == 0 ? (uint)random.Next(leaves) : (uint)random.Next((int)made);
        }

        /// <summary>
        /// Computes the reference congruence closure: applies the unions, then repeats a pass over the constructors
        /// until it unions nothing, and folds <c>low</c> by minimum per class.
        /// </summary>
        void Close()
        {
            var parent = new uint[IdCount];
            for (var i = 0; i < parent.Length; i++)
                parent[i] = (uint)i;

            for (var i = 0; i < unions.Length; i += 2)
                Union(parent, unions[i], unions[i + 1]);

            var pairMap = new Dictionary<(uint, uint), uint>(pairs.Length / 3);
            var wrapMap = new Dictionary<uint, uint>(wraps.Length / 2);
            bool changed;
            do
            {
                changed = false;
                pairMap.Clear();
                for (var i = 0; i < pairs.Length; i += 3)
                {
                    var key = (Find(parent, pairs[i]), Find(parent, pairs[i + 1]));
                    var output = Find(parent, pairs[i + 2]);
                    if (pairMap.TryGetValue(key, out var existing))
                        changed |= Union(parent, existing, output);
                    else
                        pairMap[key] = output;
                }

                wrapMap.Clear();
                for (var i = 0; i < wraps.Length; i += 2)
                {
                    var key = Find(parent, wraps[i]);
                    var output = Find(parent, wraps[i + 1]);
                    if (wrapMap.TryGetValue(key, out var existing))
                        changed |= Union(parent, existing, output);
                    else
                        wrapMap[key] = output;
                }
            }
            while (changed);

            ExpectedPairs = pairMap.Count;
            ExpectedWraps = wrapMap.Count;
            for (var i = 0; i < lows.Length; i += 2)
            {
                var key = Find(parent, lows[i]);
                var value = lows[i + 1];
                expectedLow[key] = expectedLow.TryGetValue(key, out var min) ? Math.Min(min, value) : value;
            }

            var displaced = 0;
            for (var i = 0; i < IdCount; i++)
            {
                canonical[i] = Find(parent, (uint)i);
                if (canonical[i] != i)
                    displaced++;
            }

            ExpectedDisplaced = displaced;
        }

        /// <summary>
        /// Finds the root of <paramref name="id"/>, halving the path.
        /// </summary>
        static uint Find(uint[] parent, uint id)
        {
            while (parent[id] != id)
            {
                parent[id] = parent[parent[id]];
                id = parent[id];
            }

            return id;
        }

        /// <summary>
        /// Unions the classes of <paramref name="a"/> and <paramref name="b"/>, keeping the smaller root.
        /// </summary>
        /// <returns><c>true</c> if they were different classes.</returns>
        static bool Union(uint[] parent, uint a, uint b)
        {
            var ra = Find(parent, a);
            var rb = Find(parent, b);
            if (ra == rb)
                return false;

            if (ra < rb)
                parent[rb] = ra;
            else
                parent[ra] = rb;

            return true;
        }

    }

}
