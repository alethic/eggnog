using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The container catch-up differential, the sibling of <see cref="ContainerPassDifferentialTests"/> in which the
    /// ancestors write while their descendants live: random programs (<see cref="AncestorContainerProgram"/>) over the
    /// world of a <see cref="ContainerPassWorld"/>, in which a writable root, or a writable parent of a frozen root,
    /// interns vectors and sets of classes (the root only), unions classes, unions container ids, which renames one
    /// container onto another's id, writes rows, and rebuilds, while a child and a grandchild exist, interleaved with
    /// the child's and the grandchild's own unions, renames and writes. The child and the grandchild are checked, at
    /// random points and at the end, against two oracles each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// egglog has no writable parent under a live <c>(push)</c>, so the first oracle is a fresh root replaying, in the
    /// same order, the steps of the levels the context sees: the ancestor's and the child's for the child, and the
    /// grandchild's as well for the grandchild, each followed by a rebuild. An intern evaluates its contents canonical
    /// in the replaying root, as egglog's frontend does; when that content exists there already, the replay mints the
    /// id the writable root minted and unions it with the existing one, where egglog would return the existing id, so
    /// that ids stay aligned, the two differing by an id merged away. The context and the replay must agree exactly:
    /// the canonical id of every id; the containers the context's view holds, enumerated level by level and compared
    /// both ways with the replay's, each content once and several under one id where a rename put them there; the
    /// container under every id and the id of every content; and every row. The second oracle is the naive partition
    /// closed under congruence of the containers and of the constructor (<see cref="ContainerPassModel"/>), which
    /// shares nothing with the engine's rebuild loops. The replays rebuild after every step, also after a step whose
    /// descendant leaves out its rebuild (<see cref="AncestorContainerProgram"/>), so that its unions reach its
    /// container pass together with what the ancestor wrote in between: vectors and sets rebuild alike in either order,
    /// so the views must still agree exactly.
    /// </para>
    /// <para>
    /// Every fifth seed builds a large world, whose first step makes the child claim more than 1000 vectors, so its
    /// later rebuilds take egglog's incremental path. A second theory runs programs over small worlds, a few classes
    /// and a few containers of one or two elements, with longer programs in which the ancestor repeats its
    /// descendants' unions, and a check after every step that is not deferred (<see cref="AncestorContainerProgram"/>):
    /// there the levels' claims and the ancestor's unions and renames meet often, a child and a grandchild that claim
    /// two containers whose ids the ancestor then unions, after it made one of them equal to the child's copy, for
    /// one. Such a program is cheap, and a given meeting still rare, so the theory runs four times as many seeds. Every
    /// rebuild runs the engine's invariant checker in DEBUG builds, and the test runs it on the child and the
    /// grandchild at every comparison. <c>EGGNOG_ANCESTOR_CONTAINER_SEEDS</c> sets the number of seeds (150 by
    /// default, and 600 over small worlds). A failing program prints itself.
    /// </para>
    /// </remarks>
    public class AncestorContainerDifferentialTests
    {

        /// <summary>
        /// The seeds to run: 150 by default, or as many as <c>EGGNOG_ANCESTOR_CONTAINER_SEEDS</c> says.
        /// </summary>
        public static TheoryData<int> Seeds() => SeedsUpTo(SeedCount());

        /// <summary>
        /// The seeds to run over small worlds: four times <see cref="Seeds"/>'s.
        /// </summary>
        public static TheoryData<int> SmallWorldSeeds() => SeedsUpTo(4 * SeedCount());

        /// <summary>
        /// Gets the number of seeds: 150 by default, or as many as <c>EGGNOG_ANCESTOR_CONTAINER_SEEDS</c> says.
        /// </summary>
        static int SeedCount() =>
            int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_ANCESTOR_CONTAINER_SEEDS"), out var n) && n > 0 ? n : 150;

        /// <summary>
        /// Gets the seeds from zero to <paramref name="count"/>, exclusive.
        /// </summary>
        static TheoryData<int> SeedsUpTo(int count)
        {
            var data = new TheoryData<int>();
            for (var seed = 0; seed < count; seed++)
                data.Add(seed);

            return data;
        }

        /// <summary>
        /// One seed's program: the child and the grandchild agree with their replaying roots and with their naive
        /// models whenever they are compared.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void AChildAndAGrandchildAgreeWithAFreshRootWhileTheirAncestorWrites(int seed) =>
            Run(new AncestorContainerProgram(seed));

        /// <summary>
        /// One seed's program over a small world (<see cref="ContainerPassProgram.Small"/>): the child and the
        /// grandchild agree with their replaying roots and with their naive models whenever they are compared.
        /// </summary>
        [Theory]
        [MemberData(nameof(SmallWorldSeeds))]
        public void AChildAndAGrandchildAgreeWithAFreshRootWhileTheirAncestorWritesInASmallWorld(int seed) =>
            Run(new AncestorContainerProgram(seed, small: true));

        /// <summary>
        /// Runs <paramref name="program"/> (<see cref="RunChain"/>), and on a failure prints it.
        /// </summary>
        static void Run(AncestorContainerProgram program)
        {
            try
            {
                RunChain(program);
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"seed {program.Seed}: {e.Message}{Environment.NewLine}Program:{Environment.NewLine}{program.Describe()}", e);
            }
        }

        /// <summary>
        /// Runs <paramref name="program"/>: the chain over one world, a replaying root and a model for each of the
        /// child and the grandchild, compared at the program's checks and at the end.
        /// </summary>
        static void RunChain(AncestorContainerProgram program)
        {
            var engine = new ContainerPassWorld(program.World);
            var childReplay = new ContainerPassWorld(program.World);
            var grandchildReplay = new ContainerPassWorld(program.World);
            var childModel = new ContainerPassModel(engine, program.Interns);
            var grandchildModel = new ContainerPassModel(engine, program.Interns);
            var vectors = engine.VectorIds.Distinct().ToList();
            var sets = engine.SetIds.Distinct().ToList();
            var root = engine.Store.Root;
            Context ancestor;
            if (program.WritableRoot)
            {
                ancestor = root;
            }
            else
            {
                root.Freeze();
                ancestor = root.CreateChild();
            }

            var child = ancestor.CreateChild();
            var grandchild = child.CreateChild();
            var unions = new List<(uint X, uint Y)>();
            foreach (var (actor, kind, a, b, c, check, defer) in program.Steps)
            {
                var context = actor switch
                {
                    'a' => ancestor,
                    'c' => child,
                    _ => grandchild,
                };

                // The replays and models of the levels that see the step: an ancestor's step is seen by both.
                var replays = actor == 'g' ? new[] { grandchildReplay } : new[] { childReplay, grandchildReplay };
                var models = actor == 'g' ? new[] { grandchildModel } : new[] { childModel, grandchildModel };
                context.Rebuild();
                switch (kind)
                {
                    case 'u':
                        Union(context, replays, models, unions, actor, (uint)(a % engine.Classes), (uint)(b % engine.Classes));
                        break;
                    case 'v':
                    {
                        var ids = a % 2 == 0 ? vectors : sets;
                        if (ids.Count > 0)
                            Union(context, replays, models, unions, actor, ids[b % ids.Count], ids[c % ids.Count]);

                        break;
                    }
                    case 'r':
                        if (unions.Count > 0)
                            Union(context, replays, models, unions, actor, unions[a % unions.Count].X, unions[a % unions.Count].Y);

                        break;
                    case 'i':
                    case 's':
                        InternAtRoot(engine, replays, models, kind, a, b, vectors, kind == 'i' ? vectors : sets);
                        break;
                    case 'f':
                    {
                        var index = a % (engine.Classes + vectors.Count + sets.Count);
                        var key = index < engine.Classes ? (uint)index : index < engine.Classes + vectors.Count ? vectors[index - engine.Classes] : sets[index - engine.Classes - vectors.Count];
                        var value = (uint)(b % 10);
                        Insert(context, engine.F, context.Find(key), value, 0);
                        foreach (var replay in replays)
                            Insert(replay.Store.Root, replay.F, replay.Store.Root.Find(key), value, 0);

                        break;
                    }
                    case 'w':
                    {
                        var x = (uint)(a % engine.Classes);
                        var y = (uint)(b % engine.Classes);
                        var (key, value) = (context.Find(x), context.Find(y));
                        Insert(context, engine.C, key, value, 0);
                        foreach (var replay in replays)
                            Insert(replay.Store.Root, replay.C, replay.Store.Root.Find(x), replay.Store.Root.Find(y), 0);

                        foreach (var model in models)
                            model.WriteConstructor(key, value);

                        break;
                    }
                }

                // A deferred step leaves the descendant's unions ahead of its container pass; the replays rebuild.
                if (defer == false)
                    context.Rebuild();

                foreach (var replay in replays)
                    replay.Store.Root.Rebuild();

                if (check)
                    CompareBoth(engine, child, grandchild, childReplay, grandchildReplay, childModel, grandchildModel, vectors, sets);
            }

            CompareBoth(engine, child, grandchild, childReplay, grandchildReplay, childModel, grandchildModel, vectors, sets);
            grandchild.Dispose();
            child.CheckInvariants();
            child.Dispose();
            if (program.WritableRoot == false)
            {
                ancestor.CheckInvariants();
                ancestor.Dispose();
            }
        }

        /// <summary>
        /// Unions <paramref name="x"/> and <paramref name="y"/> in <paramref name="context"/>, the step's actor, and in
        /// the replays and models of the levels that see the step, and records a descendant's union in
        /// <paramref name="unions"/>, which the ancestor's repeats draw from.
        /// </summary>
        static void Union(Context context, ContainerPassWorld[] replays, ContainerPassModel[] models, List<(uint X, uint Y)> unions, char actor, uint x, uint y)
        {
            context.Union(x, y);
            foreach (var replay in replays)
                replay.Store.Root.Union(x, y);

            foreach (var model in models)
                model.Union(x, y);

            if (actor != 'a')
                unions.Add((x, y));
        }

        /// <summary>
        /// Interns at the writable root a vector (<paramref name="kind"/> <c>i</c>) or a set of <c>1 + a % 3</c>
        /// elements, classes or earlier vectors drawn from a generator seeded with <paramref name="seed"/>, canonical at
        /// the root, and replays the intern at each of <paramref name="replays"/> with the elements canonical there,
        /// keeping the replays' ids aligned with the engine's: when the replay holds the content already, it mints the
        /// engine's new id and unions it with the content's. A new container joins <paramref name="ids"/> and the
        /// models.
        /// </summary>
        static void InternAtRoot(ContainerPassWorld engine, ContainerPassWorld[] replays, ContainerPassModel[] models, char kind, int a, int seed, List<uint> vectors, List<uint> ids)
        {
            var random = new Random(seed);
            var elements = new uint[1 + a % 3];
            for (var i = 0; i < elements.Length; i++)
                elements[i] = vectors.Count > 0 && random.Next(4) == 0 ? vectors[random.Next(vectors.Count)] : (uint)random.Next(engine.Classes);

            var root = engine.Store.Root;
            var before = Minted(engine);
            var atRoot = elements.Select(root.Find).ToArray();
            var id = kind == 'i' ? engine.Intern(new VecContainer(atRoot)) : engine.Intern(new SetContainer(atRoot));
            var minted = Minted(engine) != before;
            foreach (var replay in replays)
            {
                var replayRoot = replay.Store.Root;
                if (Minted(replay) != before)
                    throw new Xunit.Sdk.XunitException($"the replaying root minted {Minted(replay)} ids before the intern, the engine {before}");

                var canonical = elements.Select(replayRoot.Find).ToArray();
                var existing = kind == 'i' ? replay.Intern(new VecContainer(canonical)) : replay.Intern(new SetContainer(canonical));
                if (minted && Minted(replay) == before)
                {
                    var aligned = replayRoot.NewId();
                    if (aligned != id)
                        throw new Xunit.Sdk.XunitException($"the replaying root minted {aligned} for the engine's {id}");

                    replayRoot.Union(existing, aligned);
                    replayRoot.Rebuild();
                }
                else if (minted && existing != id)
                {
                    throw new Xunit.Sdk.XunitException($"the replaying root interned [{string.Join(" ", canonical)}] under {existing}, the engine under {id}");
                }
                else if (minted == false && Minted(replay) != before)
                {
                    throw new Xunit.Sdk.XunitException($"the replaying root minted {existing} for [{string.Join(" ", canonical)}], which the engine's root held");
                }
            }

            if (minted == false)
                return;

            ids.Add(id);
            foreach (var model in models)
                model.AddContainer(kind == 'i' ? 'v' : 's', atRoot, id);
        }

        /// <summary>
        /// Gets the number of ids <paramref name="world"/>'s store has minted.
        /// </summary>
        static long Minted(ContainerPassWorld world) => world.Store.Counters.Read(world.Store.IdCounter);

        /// <summary>
        /// Rebuilds the grandchild, which rebuilds the child and the ancestor first, and compares both descendants with
        /// their replaying roots and models, checking their invariants.
        /// </summary>
        static void CompareBoth(ContainerPassWorld engine, Context child, Context grandchild, ContainerPassWorld childReplay, ContainerPassWorld grandchildReplay, ContainerPassModel childModel, ContainerPassModel grandchildModel, List<uint> vectors, List<uint> sets)
        {
            grandchild.Rebuild();
            Compare(child, engine, childReplay, childModel, vectors, sets, "child");
            Compare(grandchild, engine, grandchildReplay, grandchildModel, vectors, sets, "grandchild");
            child.CheckInvariants();
            grandchild.CheckInvariants();
        }

        /// <summary>
        /// Compares what <paramref name="context"/> sees with what <paramref name="replay"/>'s root holds and with what
        /// <paramref name="model"/> says, once closed.
        /// </summary>
        static void Compare(Context context, ContainerPassWorld engine, ContainerPassWorld replay, ContainerPassModel model, List<uint> vectors, List<uint> sets, string name)
        {
            var root = replay.Store.Root;
            var view = ContainerView.Of(context.Database);
            model.Close();
            var max = (uint)(Minted(engine) - 1);
            for (var id = 0u; id <= max; id++)
            {
                if (context.Find(id) != root.Find(id))
                    throw new Xunit.Sdk.XunitException($"id {id} is {context.Find(id)} in the {name} and {root.Find(id)} at its replaying root");

                if (context.Find(id) != model.Canonical(id))
                    throw new Xunit.Sdk.XunitException($"id {id} is {context.Find(id)} in the {name} and {model.Canonical(id)} in its model");
            }

            CompareContainers(context, view, engine.Vectors, replay.Vectors, root, vectors, model.Containers('v'), v => v.Values.ToArray(), name, "vector");
            CompareContainers(context, view, engine.Sets, replay.Sets, root, sets, model.Containers('s'), s => s.Values.ToArray(), name, "set");
            foreach (var table in new[] { engine.F, engine.C })
            {
                var seen = ContainerPassDifferentialTests.Visible(context, table);
                var expected = ContainerPassDifferentialTests.Visible(root, table);
                if (seen.SequenceEqual(expected) == false)
                    throw new Xunit.Sdk.XunitException($"table {table}: the {name} sees [{string.Join(" ", seen)}] and its replaying root holds [{string.Join(" ", expected)}]");
            }
        }

        /// <summary>
        /// Compares the containers of one type, several under one id allowed. Every entry the replaying root holds is
        /// seen by content under its id, and every id it holds entries under is seen holding one of them; the entries
        /// the view holds, enumerated level by level, are the root's, each content once, and the model's; and every
        /// container id that is not canonical is seen under no container.
        /// </summary>
        static void CompareContainers<TContainer>(Context context, ContainerView view, ContainerEnv<TContainer> engineEnv, ContainerEnv<TContainer> replayEnv, Context root, List<uint> ids, Dictionary<string, (uint[] Contents, uint Id)> modelled, Func<TContainer, uint[]> elements, string name, string kind)
            where TContainer : struct, IContainerValue<TContainer>
        {
            var entries = new List<ContainerEntry<TContainer>>();
            var collector = new ContainerEntryCollector<TContainer>(entries);
            replayEnv.ForEach(ref collector);
            foreach (var entry in entries)
            {
                if (view.TryGetId(engineEnv, entry.Container, out var id) == false || id != entry.Id)
                    throw new Xunit.Sdk.XunitException($"the {kind} {entry.Container} is {entry.Id} at the {name}'s replaying root and {(view.TryGetId(engineEnv, entry.Container, out var other) ? other.ToString() : "nothing")} in the {name}");
            }

            foreach (var group in entries.GroupBy(entry => entry.Id))
            {
                if (view.TryGetContainer(engineEnv, group.Key, out var container) == false || group.All(entry => entry.Container.Equals(container) == false))
                    throw new Xunit.Sdk.XunitException($"the {kind} id {group.Key} holds [{string.Join(" ", group.Select(entry => entry.Container))}] at the {name}'s replaying root and {(view.TryGetContainer(engineEnv, group.Key, out var seen) ? seen.ToString() : "nothing")} in the {name}");
            }

            var visible = ContainerPassDifferentialTests.VisibleEntries(context, engineEnv);
            var byContent = new HashSet<string>();
            foreach (var (container, id) in visible)
            {
                var key = string.Join(",", elements(container));
                if (byContent.Add(key) == false)
                    throw new Xunit.Sdk.XunitException($"the {name}'s view holds the {kind} {container} twice, once under {id}");

                if (replayEnv.TryGetId(container, out var expected) == false || expected != id)
                    throw new Xunit.Sdk.XunitException($"the {name}'s view holds the {kind} {container} under {id}, and its replaying root {(replayEnv.TryGetId(container, out var other) ? $"under {other}" : "not at all")}");

                if (modelled.TryGetValue(key, out var model) == false || model.Id != id)
                    throw new Xunit.Sdk.XunitException($"the {name}'s view holds the {kind} {container} under {id}, and its model {(modelled.TryGetValue(key, out var m) ? $"under {m.Id}" : "not at all")}");
            }

            if (visible.Count != entries.Count)
                throw new Xunit.Sdk.XunitException($"the {name}'s view holds {visible.Count} {kind}s and its replaying root {entries.Count}");

            if (visible.Count != modelled.Count)
                throw new Xunit.Sdk.XunitException($"the {name}'s view holds {visible.Count} {kind}s and its model {modelled.Count}");

            foreach (var id in ids)
                if (root.Find(id) != id && view.TryGetContainer(engineEnv, id, out var stale))
                    throw new Xunit.Sdk.XunitException($"the {kind} id {id} is merged into {root.Find(id)} at the {name}'s replaying root, but the {name} still sees {stale} under it");
        }

    }

}
