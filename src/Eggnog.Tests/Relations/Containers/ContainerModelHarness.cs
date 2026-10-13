using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A database with containers driven the way egglog-bridge drives one, and a naive model of what it must hold, run
    /// in lockstep: the container part of the root congruence oracle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Schema: the union-find; vectors and sets interned with the bridge's merge and the shared id counter; a function
    /// <c>holder(container) -&gt; id</c> with the union-id merge. Containers are interned with canonical elements and
    /// rows staged with canonical keys, as the frontend does. Rebuilds run the bridge's loop pass by pass, checking
    /// each pass's dirty ids: closed upward over the live containers, and every row mentioning one re-stamped.
    /// </para>
    /// <para>
    /// The model is a partition of every id (<see cref="PartitionReference"/>, whose canonical id is the class minimum,
    /// as the union-find's) closed under congruence: containers of one kind with equal canonical contents have equal
    /// ids, and holder rows with equal canonical keys have equal values. After a rebuild the engine must agree exactly:
    /// the same canonical id for every id, one container per canonical content, every container and id canonical, and
    /// every structure exact (<see cref="ContainerValues.CheckInvariants"/>).
    /// </para>
    /// </remarks>
    sealed class ContainerModelHarness
    {

        const int MaxIds = 50_000;

        readonly Database db;
        readonly TableId uf;
        readonly TableId holder;
        readonly CounterId ids;
        readonly CounterId clock;
        readonly PartitionReference model;
        readonly List<(char Kind, uint[] Contents, uint Id)> containers = new();
        readonly List<(uint Key, uint Value)> holderRows = new();
        readonly List<uint> eclasses = new();
        readonly StringBuilder trace = new();
        uint minted;

        /// <summary>
        /// Initializes a new harness.
        /// </summary>
        /// <param name="parallelism">The parallelism of the database and its tables.</param>
        public ContainerModelHarness(ParallelHeuristics parallelism)
        {
            db = new Database(parallelism);
            uf = db.AddTable(new DisplacedTable(), name: "$uf");
            ids = db.AddCounter();
            clock = db.AddCounter();
            db.IncrementCounter(clock);
            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf, clock));
            db.Containers.RegisterType<SetContainer>(ids, new UnionContainerMerge(uf, clock));
            holder = db.AddTable(DatabaseTestUtil.Function(1, new UnionIdMerge(new ColumnId(1), uf, new ColumnId(2)), [new ColumnId(0), new ColumnId(1)], parallelism), [], [uf], "holder");
            model = new PartitionReference(MaxIds);
        }

        /// <summary>
        /// Gets the e-class ids minted so far.
        /// </summary>
        public IReadOnlyList<uint> EClasses => eclasses;

        /// <summary>
        /// Gets the container ids returned so far (repeats possible).
        /// </summary>
        public IEnumerable<uint> ContainerIds => containers.Select(c => c.Id);

        /// <summary>
        /// Gets the number of rebuild passes whose container rebuild changed something.
        /// </summary>
        public int ChangingPasses { get; private set; }

        /// <summary>
        /// Gets the number of dirty ids seen over all passes.
        /// </summary>
        public int DirtySeen { get; private set; }

        /// <summary>
        /// Gets the operations so far, for failure messages.
        /// </summary>
        public string Trace => trace.ToString();

        DisplacedTable UnionFind => (DisplacedTable)db.GetTable(uf);

        uint Timestamp => (uint)db.ReadCounter(clock);

        /// <summary>
        /// Mints a fresh e-class id.
        /// </summary>
        public uint NewEClass()
        {
            var id = Mint();
            eclasses.Add(id);
            return id;
        }

        /// <summary>
        /// Interns a container of kind <paramref name="kind"/> (<c>v</c> vector, <c>b</c> vector over a base sort,
        /// <c>s</c> set) whose elements are canonicalized first, unless it is over a base sort.
        /// </summary>
        public uint Intern(char kind, uint[] elements)
        {
            var find = UnionFind;
            var contents = kind == 'b' ? elements : elements.Select(find.FindNaive).ToArray();
            var id = db.WithExecutionState(es => kind == 's'
                ? es.Containers.RegisterValue(new SetContainer(contents), es)
                : es.Containers.RegisterValue(new VecContainer(kind == 'v', contents), es));
            if (id >= minted)
                minted = id + 1;

            Assert.True(id < MaxIds, "the model is too small");
            containers.Add((kind, contents, id));
            trace.AppendLine($"{kind}[{string.Join(",", contents)}] -> {id}");
            return id;
        }

        /// <summary>
        /// Stages <c>holder(container) = fresh id</c>, keyed by the container's canonical id.
        /// </summary>
        public void AddHolder(uint container)
        {
            var value = NewEClass();
            var key = UnionFind.FindNaive(container);
            DatabaseTestUtil.Stage(db, holder, [key, value, Timestamp]);
            holderRows.Add((key, value));
            trace.AppendLine($"holder({key}) = {value}");
        }

        /// <summary>
        /// Stages the union of two ids into the union-find and the model.
        /// </summary>
        public void Union(uint a, uint b)
        {
            DatabaseTestUtil.Stage(db, uf, [a, b, Timestamp]);
            model.Union(a, b, out _, out _);
            trace.AppendLine($"union {a} {b}");
        }

        /// <summary>
        /// Merges everything staged, then runs the bridge's rebuild loop pass by pass, checking each pass's dirty ids.
        /// </summary>
        /// <returns>The number of passes.</returns>
        public int Rebuild()
        {
            trace.AppendLine("rebuild");
            db.MergeAll(Timestamp);
            db.IncrementCounter(clock);
            var tables = new[] { holder };
            for (var pass = 0; ; pass++)
            {
                Require(pass < 100, () => "the rebuild loop does not converge");
                var ts = Timestamp;
                var summary = db.RebuildContainers(uf, ts);
                if (summary.Changed)
                    ChangingPasses++;

                DirtySeen += summary.DirtyCount;
                CheckClosedUpward(summary);
                var rebuilt = db.ApplyRebuild(uf, tables, ts);
                var refreshed = db.RefreshRowsForValues(tables, summary.DirtyIds, ts);
                CheckRestamped(summary, ts);
                db.IncrementCounter(clock);
                Assert.Null(db.Failure);
                if (summary.Changed == false && rebuilt == false && refreshed == false)
                    return pass + 1;
            }
        }

        /// <summary>
        /// Rebuilds, closes the model, and asserts that the engine agrees with it.
        /// </summary>
        /// <returns>The number of rebuild passes.</returns>
        public int Check()
        {
            var passes = Rebuild();
            CloseModel();
            trace.AppendLine("check");

            var find = UnionFind;
            for (var id = 0u; id < minted; id++)
                Require(model.Canonical(id) == find.FindNaive(id), () => $"id {id}: the model says {model.Canonical(id)}, the union-find {find.FindNaive(id)}");

            db.Containers.CheckInvariants();
            var vecs = new ContainerCollector<VecContainer>();
            db.Containers.ForEach<VecContainer, ContainerCollector<VecContainer>>(ref vecs);
            var sets = new ContainerCollector<SetContainer>();
            db.Containers.ForEach<SetContainer, ContainerCollector<SetContainer>>(ref sets);

            // Every stored container and id is canonical.
            foreach (var (vec, id) in vecs.Items)
            {
                Assert.Equal(find.FindNaive(id), id);
                if (vec.DoRebuild)
                    Require(vec.Values.ToArray().All(v => find.FindNaive(v) == v), () => $"{vec} under {id} is not canonical");
            }

            foreach (var (set, id) in sets.Items)
            {
                Assert.Equal(find.FindNaive(id), id);
                Require(set.Values.ToArray().All(v => find.FindNaive(v) == v), () => $"{set} under {id} is not canonical");
            }

            // One container per canonical content, under the class's canonical id.
            var expected = new Dictionary<string, uint>();
            foreach (var (kind, contents, id) in containers)
            {
                var canonical = Canonical(kind, contents);
                var key = kind + ":" + string.Join(",", canonical);
                var cid = model.Canonical(id);
                if (expected.TryGetValue(key, out var other))
                    Assert.Equal(other, cid);
                else
                    expected.Add(key, cid);

                uint found;
                if (kind == 's')
                {
                    Require(db.Containers.GetEnvironment<SetContainer>().TryGetId(new SetContainer(canonical), out found), () => $"{key} is missing");
                    Assert.True(db.Containers.TryGetValue<SetContainer>(cid, out var set));
                    Assert.Equal(new SetContainer(canonical), set);
                }
                else
                {
                    var vec = new VecContainer(kind == 'v', canonical);
                    Require(db.Containers.GetEnvironment<VecContainer>().TryGetId(vec, out found), () => $"{key} is missing");
                    Assert.True(db.Containers.TryGetValue<VecContainer>(cid, out var stored));
                    Assert.Equal(vec, stored);
                }

                Require(found == cid, () => $"{key} has id {found}, the model says {cid}");
            }

            Assert.Equal(expected.Count, vecs.Items.Count + sets.Items.Count);

            // The holder table is closed: one canonical row per canonical key.
            var rows = TableTestUtil.LiveRows(db.GetTable(holder));
            var keys = holderRows.Select(r => model.Canonical(r.Key)).Distinct().Order().ToArray();
            Assert.Equal(keys, rows.Select(r => r[0]).Order().ToArray());
            foreach (var row in rows)
                Assert.Equal(find.FindNaive(row[1]), row[1]);

            return passes;
        }

        /// <summary>
        /// Every live container that holds a dirty id is dirty itself.
        /// </summary>
        void CheckClosedUpward(ContainerRebuildSummary summary)
        {
            if (summary.DirtyCount == 0)
                return;

            var vecs = new ContainerCollector<VecContainer>();
            db.Containers.ForEach<VecContainer, ContainerCollector<VecContainer>>(ref vecs);
            foreach (var (vec, id) in vecs.Items)
                if (vec.Values.ToArray().Any(summary.IsDirty))
                    Require(summary.IsDirty(id), () => $"{vec} under {id} holds a dirty id but is not dirty");

            var sets = new ContainerCollector<SetContainer>();
            db.Containers.ForEach<SetContainer, ContainerCollector<SetContainer>>(ref sets);
            foreach (var (set, id) in sets.Items)
                if (set.Values.ToArray().Any(summary.IsDirty))
                    Require(summary.IsDirty(id), () => $"{set} under {id} holds a dirty id but is not dirty");
        }

        /// <summary>
        /// Every live holder row mentioning a dirty id carries the pass's timestamp.
        /// </summary>
        void CheckRestamped(ContainerRebuildSummary summary, uint ts)
        {
            if (summary.DirtyCount == 0)
                return;

            foreach (var row in TableTestUtil.LiveRows(db.GetTable(holder)))
                if (summary.IsDirty(row[0]) || summary.IsDirty(row[1]))
                    Require(row[2] == ts, () => $"holder row [{string.Join(",", row)}] mentions a dirty id but was not re-stamped at {ts}");
        }

        /// <summary>
        /// Closes the model under congruence of containers and of the holder function.
        /// </summary>
        void CloseModel()
        {
            bool changed;
            do
            {
                changed = false;
                var byContent = new Dictionary<string, uint>();
                foreach (var (kind, contents, id) in containers)
                {
                    var key = kind + ":" + string.Join(",", Canonical(kind, contents));
                    if (byContent.TryGetValue(key, out var other))
                        changed |= model.Union(other, id, out _, out _);
                    else
                        byContent.Add(key, id);
                }

                var byKey = new Dictionary<uint, uint>();
                foreach (var (key, value) in holderRows)
                {
                    var canonicalKey = model.Canonical(key);
                    if (byKey.TryGetValue(canonicalKey, out var other))
                        changed |= model.Union(other, value, out _, out _);
                    else
                        byKey.Add(canonicalKey, value);
                }
            }
            while (changed);
        }

        /// <summary>
        /// Gets the canonical contents of a container under the model.
        /// </summary>
        uint[] Canonical(char kind, uint[] contents)
        {
            if (kind == 'b')
                return contents;

            var mapped = contents.Select(model.Canonical);
            return kind == 's' ? mapped.Distinct().Order().ToArray() : mapped.ToArray();
        }

        /// <summary>
        /// Fails with <paramref name="message"/> and the trace unless <paramref name="condition"/> holds; the message
        /// is built only on failure.
        /// </summary>
        void Require(bool condition, Func<string> message)
        {
            if (condition == false)
                Assert.Fail(message() + Environment.NewLine + Trace);
        }

        uint Mint()
        {
            var id = (uint)db.IncrementCounter(ids);
            Assert.True(id < MaxIds, "the model is too small");
            minted = Math.Max(minted, id + 1);
            return id;
        }

    }

}
