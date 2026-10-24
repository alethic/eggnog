using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The container pass differential: random programs of containers of classes run in a child, checked against two
    /// oracles. A root of classes, vectors and sets of classes, vectors and sets of those containers, rows of a max
    /// function over classes and containers and of a constructor over classes; then batches, each of a few writes of
    /// either function on some seeds, a few unions of classes and a rebuild, in a child of the root (frozen, or
    /// writable and quiet), and on some seeds more in a grandchild (under a frozen or a writable child). The writes of
    /// the constructor union classes in the child's own merges, as a rule's would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first oracle replays the batches at a fresh root built the same way, the copy egglog's push makes. The child
    /// and the replaying root must agree exactly, since a child mints no id and both keep the smaller of two ids: the
    /// canonical id of every id; the containers the child's view holds, enumerated level by level (its own layers' and
    /// its ancestors' entries under ids no level above them shadows) and compared both ways with the root's, each
    /// content once; the container under every id and the id of every content; and every row. The replay shares the
    /// engine's container rebuild loops with the child's pass, so the second oracle shares nothing with them: a naive
    /// partition of every id closed under congruence of the containers and of the constructor
    /// (<see cref="ContainerPassModel"/>), which the child's canonical ids and the containers its view holds must
    /// match.
    /// </para>
    /// <para>
    /// Every fifth seed builds 1300 vectors, nearly all holding one class, which the child's first union displaces, so
    /// the child claims more than 1000 of them and its later rebuilds take egglog's incremental path; the others stay
    /// small and rebuild in full (<see cref="ContainerPassProgram"/>). Every rebuild runs the engine's invariant
    /// checker in DEBUG builds, and the test runs it again after the last.
    /// </para>
    /// <para>
    /// <c>EGGNOG_CONTAINER_PASS_SEEDS</c> sets the number of seeds (200 by default). A failing program prints itself.
    /// </para>
    /// </remarks>
    public class ContainerPassDifferentialTests
    {

        /// <summary>
        /// The seeds to run: 200 by default, or as many as <c>EGGNOG_CONTAINER_PASS_SEEDS</c> says.
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var count = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_CONTAINER_PASS_SEEDS"), out var n) && n > 0 ? n : 200;
            var data = new TheoryData<int>();
            for (var seed = 0; seed < count; seed++)
                data.Add(seed);

            return data;
        }

        /// <summary>
        /// One seed's program: the child, and the grandchild when there is one, agree with the replaying root and with
        /// the naive model.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void AChildsContainersAgreeWithARootReplayingItsUnions(int seed)
        {
            var program = new ContainerPassProgram(seed);
            try
            {
                Run(program);
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.Message}{Environment.NewLine}Program:{Environment.NewLine}{program.Describe()}", e);
            }
        }

        /// <summary>
        /// Runs <paramref name="program"/> in a child (and grandchild), at a replaying root and in the model, comparing
        /// them after the child's batches and again after the grandchild's.
        /// </summary>
        static void Run(ContainerPassProgram program)
        {
            var engine = new ContainerPassWorld(program);
            var replay = new ContainerPassWorld(program);
            var model = new ContainerPassModel(engine);
            if (program.FreezeRoot)
                engine.Store.Root.Freeze();

            var child = engine.Store.Root.CreateChild();
            foreach (var batch in program.ChildBatches)
                Apply(child, engine, batch, model);

            replay.Replay(program.ChildBatches);
            Compare(child, engine, replay, model);
            child.CheckInvariants();
            if (program.GrandchildBatches.Count == 0)
                return;

            if (program.FreezeChild && program.FreezeRoot)
                child.Freeze();

            var grandchild = child.CreateChild();
            foreach (var batch in program.GrandchildBatches)
                Apply(grandchild, engine, batch, model);

            replay.Replay(program.GrandchildBatches);
            Compare(grandchild, engine, replay, model);
            grandchild.CheckInvariants();
            grandchild.Dispose();
            if (program.FreezeChild == false || program.FreezeRoot == false)
                child.CheckInvariants();
        }

        /// <summary>
        /// Applies one batch to <paramref name="context"/>, a context of <paramref name="world"/>, and rebuilds it: the
        /// writes first, on ids canonical in the context when the batch starts (a write of <c>c</c> may union classes,
        /// after which the context answers no find until it rebuilds), then the unions. Records the batch in
        /// <paramref name="model"/> when given.
        /// </summary>
        internal static void Apply(Context context, ContainerPassWorld world, List<(char Kind, uint A, uint B)> batch, ContainerPassModel? model = null)
        {
            var writes = new List<(TableId Table, uint Key, uint Value)>();
            foreach (var (kind, a, b) in batch)
            {
                if (kind == 'f')
                    writes.Add((world.F, context.Find(world.Resolve(a)), b));
                else if (kind == 'c')
                    writes.Add((world.C, context.Find(a), context.Find(b)));
            }

            foreach (var (table, key, value) in writes)
            {
                Insert(context, table, key, value, 0);
                if (table == world.C)
                    model?.WriteConstructor(key, value);
            }

            foreach (var (kind, a, b) in batch)
            {
                if (kind != 'u')
                    continue;

                context.Union(a, b);
                model?.Union(a, b);
            }

            context.Rebuild();
        }

        /// <summary>
        /// Compares what <paramref name="context"/>, a descendant of <paramref name="engine"/>'s root, sees with what
        /// <paramref name="replay"/>'s root holds and with what <paramref name="model"/> says, once closed.
        /// </summary>
        static void Compare(Context context, ContainerPassWorld engine, ContainerPassWorld replay, ContainerPassModel model)
        {
            var root = replay.Store.Root;
            var view = ContainerView.Of(context.Database);
            model.Close();
            var max = replay.MaxId;
            for (var id = 0u; id <= max; id++)
            {
                if (context.Find(id) != root.Find(id))
                    throw new Xunit.Sdk.XunitException($"id {id} is {context.Find(id)} in the context and {root.Find(id)} at the replaying root");

                if (context.Find(id) != model.Canonical(id))
                    throw new Xunit.Sdk.XunitException($"id {id} is {context.Find(id)} in the context and {model.Canonical(id)} in the model");
            }

            CompareContainers(context, view, engine.Vectors, replay.Vectors, root, engine.VectorIds, model.Containers('v'), v => v.Values.ToArray(), "vector");
            CompareContainers(context, view, engine.Sets, replay.Sets, root, engine.SetIds, model.Containers('s'), s => s.Values.ToArray(), "set");

            foreach (var table in new[] { engine.F, engine.C })
            {
                var seen = Visible(context, table);
                var expected = Visible(root, table);
                if (seen.SequenceEqual(expected) == false)
                    throw new Xunit.Sdk.XunitException($"table {table}: the context sees [{string.Join(" ", seen)}] and the replaying root holds [{string.Join(" ", expected)}]");
            }
        }

        /// <summary>
        /// Compares the containers of one type. Every entry the replaying root holds is seen by id and by content in
        /// the view; the entries the view holds, enumerated (<see cref="VisibleEntries"/>), are the root's, each
        /// content and each id once, and the model's; and every container id that is not canonical is seen under no
        /// container.
        /// </summary>
        static void CompareContainers<TContainer>(Context context, ContainerView view, ContainerEnv<TContainer> engineEnv, ContainerEnv<TContainer> replayEnv, Context root, IEnumerable<uint> ids, Dictionary<string, (uint[] Contents, uint Id)> modelled, Func<TContainer, uint[]> elements, string kind)
            where TContainer : struct, IContainerValue<TContainer>
        {
            var entries = new List<ContainerEntry<TContainer>>();
            var collector = new ContainerEntryCollector<TContainer>(entries);
            replayEnv.ForEach(ref collector);
            foreach (var entry in entries)
            {
                if (view.TryGetId(engineEnv, entry.Container, out var id) == false || id != entry.Id)
                    throw new Xunit.Sdk.XunitException($"the {kind} {entry.Container} is {entry.Id} at the replaying root and {(view.TryGetId(engineEnv, entry.Container, out var other) ? other.ToString() : "nothing")} in the context");

                if (view.TryGetContainer(engineEnv, entry.Id, out var container) == false || container.Equals(entry.Container) == false)
                    throw new Xunit.Sdk.XunitException($"the {kind} {entry.Id} is {entry.Container} at the replaying root and {(view.TryGetContainer(engineEnv, entry.Id, out var seen) ? seen.ToString() : "nothing")} in the context");
            }

            // The other way: what the view holds, level by level, is what the root holds and what the model says.
            var visible = VisibleEntries(context, engineEnv);
            var byContent = new HashSet<string>();
            var byId = new HashSet<uint>();
            foreach (var (container, id) in visible)
            {
                var key = string.Join(",", elements(container));
                if (byContent.Add(key) == false)
                    throw new Xunit.Sdk.XunitException($"the context's view holds the {kind} {container} twice, once under {id}");

                if (byId.Add(id) == false)
                    throw new Xunit.Sdk.XunitException($"the context's view holds two {kind}s under {id}, one of them {container}");

                if (replayEnv.TryGetId(container, out var expected) == false || expected != id)
                    throw new Xunit.Sdk.XunitException($"the context's view holds the {kind} {container} under {id}, and the replaying root {(replayEnv.TryGetId(container, out var other) ? $"under {other}" : "not at all")}");

                if (modelled.TryGetValue(key, out var model) == false || model.Id != id)
                    throw new Xunit.Sdk.XunitException($"the context's view holds the {kind} {container} under {id}, and the model {(modelled.TryGetValue(key, out var m) ? $"under {m.Id}" : "not at all")}");
            }

            if (visible.Count != entries.Count)
                throw new Xunit.Sdk.XunitException($"the context's view holds {visible.Count} {kind}s and the replaying root {entries.Count}");

            if (visible.Count != modelled.Count)
                throw new Xunit.Sdk.XunitException($"the context's view holds {visible.Count} {kind}s and the model {modelled.Count}");

            foreach (var id in ids)
                if (root.Find(id) != id && view.TryGetContainer(engineEnv, id, out var stale))
                    throw new Xunit.Sdk.XunitException($"the {kind} id {id} is merged into {root.Find(id)} at the replaying root, but the context still sees {stale} under it");
        }

        /// <summary>
        /// Enumerates the containers of the type <paramref name="root"/> holds that <paramref name="context"/>'s view
        /// holds: every entry of the own environment of each level's layer of the type, from the context down, and of
        /// the root's environment, under an id no level above it, up to the context, shadows.
        /// </summary>
        static List<(TContainer Container, uint Id)> VisibleEntries<TContainer>(Context context, ContainerEnv<TContainer> root)
            where TContainer : struct, IContainerValue<TContainer>
        {
            var levels = new List<ContainerLayers>();
            for (var level = context.ContainerLayers; level is not null; level = level.Parent)
                levels.Add(level);

            var result = new List<(TContainer, uint)>();
            for (var i = 0; i <= levels.Count; i++)
            {
                ContainerEnv<TContainer> env;
                if (i == levels.Count)
                    env = root;
                else if (levels[i].LayerAt(root.TypeId) is ContainerLayer<TContainer> layer)
                    env = layer.Own;
                else
                    continue;

                var entries = new List<ContainerEntry<TContainer>>();
                var collector = new ContainerEntryCollector<TContainer>(entries);
                env.ForEach(ref collector);
                foreach (var entry in entries)
                    if (levels.Take(i).Any(above => above.LayerAt(root.TypeId)?.Shadowed.Contains(entry.Id) == true) == false)
                        result.Add((entry.Container, entry.Id));
            }

            return result;
        }

        /// <summary>
        /// Gets the visible rows of <paramref name="table"/> in <paramref name="context"/>, without their timestamps,
        /// sorted.
        /// </summary>
        static List<string> Visible(Context context, TableId table)
        {
            var width = context.Store.Schema[table]!.SortColumn;
            return ScanVisible(context.GetView(table))
                .Select(r => string.Join(",", r.Cells.Take(width)))
                .Order(StringComparer.Ordinal)
                .ToList();
        }

    }

}
