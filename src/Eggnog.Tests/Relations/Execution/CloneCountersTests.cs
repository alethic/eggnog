using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The executor's clone counts (<see cref="ExecutorCounters"/>, which the suite turns on,
    /// <see cref="ExecutorCountersSetup"/>): every <see cref="ExecutionState"/> clone over a database is counted under its
    /// site, every mutation buffer a state makes on first write is counted, with the first allocation and the growths of
    /// the state's buffer map, and every buffer a clone takes from its parent is counted as inherited.
    /// </summary>
    public class CloneCountersTests
    {

        /// <summary>
        /// The matches of the math rule in <see cref="RunMath"/>: two full batches and a partial one.
        /// </summary>
        const int Matches = 2 * Bindings.DefaultBatchSize + 44;

        /// <summary>
        /// The rows above <see cref="ParallelHeuristics.TableOpThreshold"/> and
        /// <see cref="ParallelHeuristics.RebuildThreshold"/> that make a table's merge and rebuild parallel.
        /// </summary>
        const int Large = 30_000;

        /// <summary>
        /// Loads egglog's <c>basic_query</c> e-graph with <see cref="Matches"/> <c>add</c> rows over as many values,
        /// resets the counters and executes <c>Add(x, y, z), Num(a, x), Num(b, y) =&gt; Num(a + b, z)</c> in
        /// <paramref name="mode"/>, without merging.
        /// </summary>
        static (ExecutionMathEgraph Graph, ExecutorCounters Counts) RunMath(string mode)
        {
            var g = new ExecutionMathEgraph(mode);
            var db = g.Db;
            var bases = new BaseValues();
            bases.RegisterType<Int64Codec, long>();
            var addInt = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                var x = bases.Unwrap<Int64Codec, long>(new Value(args[0]));
                var y = bases.Unwrap<Int64Codec, long>(new Value(args[1]));
                result = bases.Get<Int64Codec, long>(x + y).Rep;
                return true;
            }));

            var ids = new uint[Matches];
            var nums = db.NewBuffer(g.Num);
            for (var i = 0; i < Matches; i++)
            {
                ids[i] = (uint)db.IncrementCounter(g.IdCounter);
                nums.StageInsert([bases.Get<Int64Codec, long>(i).Rep, ids[i], 0]);
            }

            nums.Flush();
            db.MergeAll();
            var adds = db.NewBuffer(g.Add);
            for (var i = 0; i < Matches; i++)
                adds.StageInsert([ids[i], ids[(i + 1) % Matches], (uint)db.IncrementCounter(g.IdCounter), 0]);

            adds.Flush();
            db.MergeAll();
            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            var xv = q.NewVariable("x");
            var yv = q.NewVariable("y");
            var z = q.NewVariable("z");
            var av = q.NewVariable("a");
            var bv = q.NewVariable("b");
            q.AddAtom(g.Add, [xv, yv, z, QueryEntry.Wildcard()]);
            q.AddAtom(g.Num, [av, xv, QueryEntry.Wildcard()]);
            q.AddAtom(g.Num, [bv, yv, QueryEntry.Wildcard()]);
            var rb = q.Build();
            var sum = rb.CallExternal(addInt, [av, bv]);
            rb.Insert(g.Num, [sum, z, new Value(1)]);
            rb.Build("add");
            using var rs = rsb.Build();
            var counts = db.ExecutorCounts!;
            counts.Reset();
            var report = db.ExecuteRuleSet(rs);
            Assert.Equal(Matches, report.NumMatches("add"));
            return (g, counts);
        }

        [Fact]
        public void ASerialRunClonesOncePerFullBatchAndOnceForThePartialBatches()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var (_, counts) = RunMath("Serial");

            Assert.Equal(Matches / Bindings.DefaultBatchSize, counts.Clones(ExecutionStateCloneSite.InlineBatch));
            Assert.Equal(1, counts.Clones(ExecutionStateCloneSite.PartialBatches));
            Assert.Equal(3, counts.TotalClones);

            // The root state stages nothing, so each clone inherits nothing and makes one buffer for num, allocating its
            // empty map's first arrays, which then never grow.
            Assert.Equal(0, counts.BuffersInherited);
            Assert.Equal(3, counts.BuffersCreated);
            Assert.Equal(3, counts.BufferMapAllocations);
            Assert.Equal(0, counts.BufferMapGrowths);
        }

        [Theory]
        [InlineData("Parallel1")]
        [InlineData("Parallel4")]
        public void AParallelRunCountsEveryCloneAtABatchSite(string mode)
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var (_, counts) = RunMath(mode);

            // Full batches run inline or as tasks; every join task that pushed matches flushes its partial batches on a
            // clone of its own. Each clone writes num once.
            var full = counts.Clones(ExecutionStateCloneSite.InlineBatch) + counts.Clones(ExecutionStateCloneSite.SpawnedBatch);
            var partial = counts.Clones(ExecutionStateCloneSite.PartialBatches);
            Assert.InRange(full, 0, Matches / Bindings.DefaultBatchSize);
            Assert.True(partial >= 1, counts.ToString());
            Assert.Equal(full + partial, counts.TotalClones);
            Assert.Equal(counts.TotalClones, counts.BuffersCreated);
            Assert.Equal(counts.TotalClones, counts.BufferMapAllocations);
            Assert.Equal(0, counts.BufferMapGrowths);
            Assert.Equal(0, counts.BuffersInherited);
        }

        /// <summary>
        /// Loads <see cref="Matches"/> <c>num</c> rows, resets the counters and executes the single-atom rule
        /// <c>Num(a, x, _) =&gt; Num(a, x, 1)</c> in <paramref name="mode"/>, without merging.
        /// </summary>
        static ExecutorCounters RunCopy(string mode)
        {
            var g = new ExecutionMathEgraph(mode);
            var db = g.Db;
            var nums = db.NewBuffer(g.Num);
            for (uint i = 0; i < Matches; i++)
                nums.StageInsert([i, (uint)db.IncrementCounter(g.IdCounter), 0]);

            nums.Flush();
            db.MergeAll();
            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            var av = q.NewVariable("a");
            var xv = q.NewVariable("x");
            q.AddAtom(g.Num, [av, xv, QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.Insert(g.Num, [av, xv, new Value(1)]);
            rb.Build("copy");
            using var rs = rsb.Build();
            var counts = db.ExecutorCounts!;
            counts.Reset();
            var report = db.ExecuteRuleSet(rs);
            Assert.Equal(Matches, report.NumMatches("copy"));
            return counts;
        }

        [Theory]
        [InlineData("Parallel1")]
        [InlineData("Parallel4")]
        public void AParallelRunSpawnsItsFullBatchesWhenTheScopeIsNotBacklogged(string mode)
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var counts = RunCopy(mode);

            // One rule task scans the table and pushes every match: far fewer tasks than the backlog limit, so both full
            // batches are spawned, and the partial batch is flushed on one clone.
            Assert.Equal(Matches / Bindings.DefaultBatchSize, counts.Clones(ExecutionStateCloneSite.SpawnedBatch));
            Assert.Equal(0, counts.Clones(ExecutionStateCloneSite.InlineBatch));
            Assert.Equal(1, counts.Clones(ExecutionStateCloneSite.PartialBatches));
            Assert.Equal(3, counts.TotalClones);
        }

        [Fact]
        public void AFullBatchIsSpawnedIntoAScopeAndRunInlineWithoutOne()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var db = new Database(ParallelHeuristics.Serial);
            var table = db.AddTable(LayeredTestUtil.Relation(1), name: "r");
            var x = new Variable(0);
            ActionInfo[] actions = [new ActionInfo([new InsertInstr(table, [QueryEntry.Var(x), QueryEntry.Const(new Value(1))])], [x])];
            var counter = new MatchCounter(1);
            var root = db.CreateExecutionState();
            var counts = db.ExecutorCounts!;
            counts.Reset();

            // A fresh scope has nothing pending, so it is not backlogged and takes the full batch as a task.
            var scope = new WorkScope(4);
            Assert.False(scope.IsBacklogged);
            var spawning = new ActionBuffer(actions, counter, root, scope);
            for (uint i = 0; i < Bindings.DefaultBatchSize; i++)
                spawning.Push(0, [i], [x]);

            scope.Wait();
            Assert.Equal(1, counts.Clones(ExecutionStateCloneSite.SpawnedBatch));
            Assert.Equal(0, counts.Clones(ExecutionStateCloneSite.InlineBatch));
            Assert.Equal(1, counts.TotalClones);
            Assert.Equal(Bindings.DefaultBatchSize, counter.Read(0));

            // Without a scope the full batch runs inline.
            var inline = new ActionBuffer(actions, counter, root, null);
            for (uint i = 0; i < Bindings.DefaultBatchSize; i++)
                inline.Push(0, [i], [x]);

            Assert.Equal(1, counts.Clones(ExecutionStateCloneSite.SpawnedBatch));
            Assert.Equal(1, counts.Clones(ExecutionStateCloneSite.InlineBatch));
            Assert.Equal(2, counts.TotalClones);
            Assert.Equal(2, counts.BuffersCreated);
        }

        [Fact]
        public void BuffersAreCountedWhenMadeAndWhenAClonesInheritsThem()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var db = new Database(ParallelHeuristics.Serial);
            var tables = Enumerable.Range(0, 6).Select(i => db.AddTable(LayeredTestUtil.Relation(1), name: "t" + i)).ToArray();
            var counts = db.ExecutorCounts!;
            counts.Reset();

            // The first buffer allocates the empty map's arrays up to its table's slot; a lower table fits.
            var state = db.CreateExecutionState();
            state.GetBuffer(tables[5]);
            state.GetBuffer(tables[0]);
            state.GetBuffer(tables[5]);
            Assert.Equal(2, counts.BuffersCreated);
            Assert.Equal(1, counts.BufferMapAllocations);
            Assert.Equal(0, counts.BufferMapGrowths);
            Assert.Equal(0, counts.TotalClones);

            // A clone takes a fresh handle of each of its parent's buffers, into a map sized to its parent's.
            var clone = state.Clone(ExecutionStateCloneSite.InsertShard);
            Assert.Equal(1, counts.Clones(ExecutionStateCloneSite.InsertShard));
            Assert.Equal(2, counts.BuffersInherited);
            clone.GetBuffer(tables[0]);
            clone.GetBuffer(tables[3]);
            Assert.Equal(3, counts.BuffersCreated);
            Assert.Equal(1, counts.BufferMapAllocations);
            Assert.Equal(0, counts.BufferMapGrowths);

            // A clone that names no site counts as Other; it inherits the three buffers its parent holds.
            clone.Clone();
            Assert.Equal(1, counts.Clones(ExecutionStateCloneSite.Other));
            Assert.Equal(2, counts.TotalClones);
            Assert.Equal(5, counts.BuffersInherited);

            counts.Reset();
            Assert.Equal(0, counts.TotalClones);
            Assert.Equal(0, counts.BuffersCreated);
            Assert.Equal(0, counts.BuffersInherited);
            Assert.Equal(0, counts.BufferMapAllocations);
            Assert.Equal(0, counts.BufferMapGrowths);
            Assert.Contains("clones 0;", counts.ToString());
        }

        [Fact]
        public void AMapIsAllocatedOnceAndGrowsOnlyPastItsArrays()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var db = new Database(ParallelHeuristics.Serial);
            var tables = Enumerable.Range(0, 6).Select(i => db.AddTable(LayeredTestUtil.Relation(1), name: "t" + i)).ToArray();
            var counts = db.ExecutorCounts!;
            counts.Reset();

            // The first buffer allocates four slots; a later table within them fits, though the map's used slots grow.
            var state = db.CreateExecutionState();
            state.GetBuffer(tables[0]);
            state.GetBuffer(tables[2]);
            Assert.Equal(2, counts.BuffersCreated);
            Assert.Equal(1, counts.BufferMapAllocations);
            Assert.Equal(0, counts.BufferMapGrowths);

            // A table past the four slots grows the arrays.
            state.GetBuffer(tables[5]);
            Assert.Equal(3, counts.BuffersCreated);
            Assert.Equal(1, counts.BufferMapAllocations);
            Assert.Equal(1, counts.BufferMapGrowths);
            Assert.EndsWith("buffer maps allocated 1, grown 1", counts.ToString());
        }

        [Fact]
        public void ACloneOfAStateWithOneBufferInheritsIt()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var db = new Database(ParallelHeuristics.Serial);
            var table = db.AddTable(LayeredTestUtil.Relation(1), name: "t");
            var counts = db.ExecutorCounts!;
            counts.Reset();

            var state = db.CreateExecutionState();
            state.GetBuffer(table);
            state.Clone(ExecutionStateCloneSite.InsertShard);
            Assert.Equal(1, counts.BuffersInherited);
            Assert.Equal(1, counts.BuffersCreated);
        }

        [Fact]
        public void EverySiteIsCountedListedAndReset()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var db = new Database(ParallelHeuristics.Serial);
            var counts = db.ExecutorCounts!;
            counts.Reset();

            var state = db.CreateExecutionState();
            var sites = Enum.GetValues<ExecutionStateCloneSite>();
            foreach (var site in sites)
                state.Clone(site);

            foreach (var site in sites)
                Assert.Equal(1, counts.Clones(site));

            Assert.Equal(sites.Length, counts.TotalClones);
            Assert.Contains("clones 7 (Other 1, InlineBatch 1, SpawnedBatch 1, PartialBatches 1, InsertShard 1, TableRebuild 1, ContainerRebuild 1);", counts.ToString());

            counts.Reset();
            foreach (var site in sites)
                Assert.Equal(0, counts.Clones(site));

            Assert.Equal(0, counts.TotalClones);
        }

        [Fact]
        public void AStateOverABareCatalogCountsNothing()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var db = new Database(ParallelHeuristics.Serial);
            var table = db.AddTable(LayeredTestUtil.Relation(1));
            var counts = db.ExecutorCounts!;
            counts.Reset();

            var catalog = new TableCatalog();
            var bare = catalog.Add(new SortedWritesTable(1, 2, new ColumnId(1), ReadOnlySpan<ColumnId>.Empty, new KeepCurrentMerge(), ParallelHeuristics.Serial));
            var state = new ExecutionState(catalog);
            state.Clone(ExecutionStateCloneSite.InlineBatch).GetBuffer(bare);
            Assert.Equal(0, counts.TotalClones);
            Assert.Equal(0, counts.BuffersCreated);
            GC.KeepAlive(table);
        }

        [Fact]
        public void AParallelMergeClonesOncePerNonEmptyShard()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var parallelism = new ParallelHeuristics(4);
            var db = new Database(parallelism);
            var table = db.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), ReadOnlySpan<ColumnId>.Empty, new KeepCurrentMerge(), parallelism), name: "f");
            var counts = db.ExecutorCounts!;
            var buffer = db.NewBuffer(table);
            for (uint k = 0; k < Large; k++)
                buffer.StageInsert([k, k, 1]);

            buffer.Flush();
            counts.Reset();
            db.MergeAll();

            Assert.Equal(Large, db.GetTable(table).Count);
            Assert.InRange(counts.Clones(ExecutionStateCloneSite.InsertShard), 2, parallelism.ShardData.Count);
            Assert.Equal(counts.Clones(ExecutionStateCloneSite.InsertShard), counts.TotalClones);
        }

        /// <summary>
        /// Builds a parallel database with a table <c>f(a, b) -&gt; t</c> of <see cref="Large"/> rows whose columns
        /// <c>a</c> and <c>b</c> are rebuilt against a union-find, and merges <paramref name="unions"/> unions of
        /// <c>2j</c> into <c>2j + 1</c>.
        /// </summary>
        static (Database Db, TableId UnionFind, TableId Table) UnionedTable(int unions)
        {
            var parallelism = new ParallelHeuristics(4);
            var db = new Database(parallelism);
            var uf = db.AddTable(new DisplacedTable(), name: "uf");
            var table = db.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(0), new ColumnId(1)], new UnionIdMerge(new ColumnId(1), uf, new ColumnId(2)), parallelism), writeDependencies: [uf], name: "f");
            var rows = db.NewBuffer(table);
            for (uint k = 0; k < Large; k++)
                rows.StageInsert([k, k + 100_000, 1]);

            rows.Flush();
            db.MergeAll();
            var buffer = db.NewBuffer(uf);
            for (uint j = 0; j < unions; j++)
                buffer.StageInsert([2 * j, 2 * j + 1, 2]);

            buffer.Flush();
            db.MergeAll();
            return (db, uf, table);
        }

        [Fact]
        public void AParallelFullRebuildClonesPerTask()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");

            // Half the table's values displaced: too many for the incremental path, so the whole table is rewritten.
            var (db, uf, table) = UnionedTable(Large / 2);
            var counts = db.ExecutorCounts!;
            counts.Reset();

            Assert.True(db.ApplyRebuild(uf, [table], 3));
            Assert.False(((SortedWritesTable)db.GetTable(table)).LastRebuildWasIncremental);
            Assert.True(counts.Clones(ExecutionStateCloneSite.TableRebuild) >= 1, counts.ToString());
            Assert.Equal(counts.Clones(ExecutionStateCloneSite.TableRebuild), counts.TotalClones);
        }

        [Fact]
        public void AParallelSubsetRebuildClonesPerTask()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");

            // A few displaced values take the incremental path; on the table's first rebuild it gathers every row, more
            // than the rebuild threshold, so the subset is rewritten in parallel.
            var (db, uf, table) = UnionedTable(8);
            var counts = db.ExecutorCounts!;
            counts.Reset();

            Assert.True(db.ApplyRebuild(uf, [table], 3));
            Assert.True(((SortedWritesTable)db.GetTable(table)).LastRebuildWasIncremental);
            Assert.True(counts.Clones(ExecutionStateCloneSite.TableRebuild) >= 1, counts.ToString());
            Assert.Equal(counts.Clones(ExecutionStateCloneSite.TableRebuild), counts.TotalClones);
        }

        [Fact]
        public void AParallelContainerRebuildClonesPerEnvironment()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var db = new Database(new ParallelHeuristics(4));
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var ids = db.AddCounter();
            var clock = db.AddCounter();
            db.IncrementCounter(clock);
            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf, clock));
            db.Containers.RegisterType<SetContainer>(ids, new UnionContainerMerge(uf, clock));
            var counts = db.ExecutorCounts!;
            counts.Reset();

            db.RebuildContainers(uf);
            Assert.Equal(2, counts.Clones(ExecutionStateCloneSite.ContainerRebuild));
            Assert.Equal(2, counts.TotalClones);
            Assert.Contains("clones 2 (ContainerRebuild 2);", counts.ToString());
        }

    }

}
