using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests that container type registration in a <see cref="ContainerValues"/> is safe while other threads read it:
    /// types registered while other threads look environments up, enumerate them and intern are seen whole or not at
    /// all; registrations racing each other get one id and one environment per type; and a type that comes to hold ids
    /// while a new type registers keeps both. Each test runs many trials on dedicated threads that meet at a barrier
    /// before each trial (<see cref="RunTrials"/>), so the calls of a trial overlap.
    /// </summary>
    public class ContainerValuesConcurrencyTests
    {

        /// <summary>
        /// Registers <see cref="TaggedContainer{TTag}"/> of each tag, from <c>NoMergeContainerMerge</c> and the given
        /// counter, holding ids or not.
        /// </summary>
        static readonly Func<ContainerValues, CounterId, bool, ContainerTypeId>[] Registrations =
        [
            Register<byte>, Register<sbyte>, Register<short>, Register<ushort>,
            Register<int>, Register<uint>, Register<long>, Register<ulong>,
            Register<float>, Register<double>, Register<decimal>, Register<char>,
            Register<bool>, Register<string>, Register<object>, Register<Guid>,
        ];

        /// <summary>
        /// Gets the environment of <see cref="TaggedContainer{TTag}"/> of each tag, or <c>null</c> if it is not
        /// registered.
        /// </summary>
        static readonly Func<ContainerValues, ContainerEnvBase?>[] Environments =
        [
            Environment<byte>, Environment<sbyte>, Environment<short>, Environment<ushort>,
            Environment<int>, Environment<uint>, Environment<long>, Environment<ulong>,
            Environment<float>, Environment<double>, Environment<decimal>, Environment<char>,
            Environment<bool>, Environment<string>, Environment<object>, Environment<Guid>,
        ];

        /// <summary>
        /// Reads the registration of <see cref="TaggedContainer{TTag}"/> of each tag as a reader on another thread does
        /// (<see cref="Read{TTag}"/>), and describes what is wrong with it, or returns <c>null</c>.
        /// </summary>
        static readonly Func<ContainerValues, CounterSet, uint, string?>[] Reads =
        [
            Read<byte>, Read<sbyte>, Read<short>, Read<ushort>,
            Read<int>, Read<uint>, Read<long>, Read<ulong>,
            Read<float>, Read<double>, Read<decimal>, Read<char>,
            Read<bool>, Read<string>, Read<object>, Read<Guid>,
        ];

        static ContainerTypeId Register<TTag>(ContainerValues values, CounterId ids, bool holdsIds) =>
            values.RegisterType<TaggedContainer<TTag>>(ids, NoMergeContainerMerge.Instance, holdsIds);

        static ContainerEnvBase? Environment<TTag>(ContainerValues values) =>
            values.TryGetEnvironment<TaggedContainer<TTag>>(out var env) ? env : null;

        /// <summary>
        /// Looks <see cref="TaggedContainer{TTag}"/> up by its slot, its id and its environment, which must agree once
        /// the type is found, and interns a container of <paramref name="value"/> of it and reads it back.
        /// </summary>
        /// <returns>What is wrong, or <c>null</c>; <c>null</c> too when the type is not registered yet.</returns>
        static string? Read<TTag>(ContainerValues values, CounterSet counters, uint value)
        {
            var name = typeof(TTag).Name;
            if (values.TryGetTypeId<TaggedContainer<TTag>>(out var id) == false)
                return null;

            if (values.TryGetEnvironment<TaggedContainer<TTag>>(out var env) == false)
                return $"{name} has type id {id} but no environment";

            if (env.TypeId != id)
                return $"{name} has type id {id} but its environment has {env.TypeId}";

            if (ReferenceEquals(values.GetEnvironment(id), env) == false)
                return $"{name}'s type id {id} names another environment";

            if (id.Index >= values.TypeCount)
                return $"{name} has type id {id} but only {values.TypeCount} types are registered";

            var container = new TaggedContainer<TTag>(value);
            var interned = env.GetOrInsertShared(container, counters);
            if (values.TryGetValue<TaggedContainer<TTag>>(interned, out var back) == false || back.Equals(container) == false)
                return $"{name}'s container {interned} does not read back as {container}";

            return null;
        }

        /// <summary>
        /// Runs <paramref name="body"/> for each of <paramref name="trials"/> trials on each of
        /// <paramref name="threads"/> dedicated threads, given the thread's number and the trial's; the threads meet at
        /// a barrier before each trial, so the bodies of a trial overlap, and they are created once, so a trial costs no
        /// thread start. What a body throws is added to <paramref name="failures"/>, and the threads go on.
        /// </summary>
        static void RunTrials(int threads, int trials, Action<int, int> body, ConcurrentQueue<string> failures)
        {
            using var start = new Barrier(threads);
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                for (var trial = 0; trial < trials; trial++)
                {
                    start.SignalAndWait();
                    try
                    {
                        body(t, trial);
                    }
                    catch (Exception exception)
                    {
                        failures.Enqueue($"trial {trial}, thread {t}: {exception.GetType().Name}: {exception.Message}");
                    }
                }
            })
            { IsBackground = true }).ToList();

            foreach (var worker in workers)
                worker.Start();

            foreach (var worker in workers)
                Assert.True(worker.Join(TimeSpan.FromMinutes(2)));
        }

        /// <summary>
        /// Fails with the first of <paramref name="failures"/>, if any.
        /// </summary>
        static void AssertNoFailures(ConcurrentQueue<string> failures)
        {
            if (failures.TryPeek(out var failure))
                Assert.Fail($"{failures.Count} failures, the first: {failure}");
        }

        /// <summary>
        /// A writer registers sixteen types, alternately holding ids and not, while four readers look every type up by
        /// its slot, its id and its environment, intern containers of the types registered so far and read them back,
        /// and enumerate the environments (<see cref="ContainerValues.AnyEqContainerMentions"/>, which the library's
        /// check of a child's host merge runs, <c>HostBatch.CheckChildMerge</c>): a reader sees each type whole or not
        /// at all, and the environments' arrays grow under the enumerations several times a trial.
        /// </summary>
        [Fact]
        public void RegistrationRacesSafelyWithLookupsEnumerationAndInterning()
        {
            const int Readers = 4;
            const int Trials = 200;
            var n = Registrations.Length;
            var dbs = Enumerable.Range(0, Trials).Select(_ => new Database(ParallelHeuristics.Serial)).ToArray();
            var counterIds = dbs.Select(db => db.AddCounter()).ToArray();
            var registered = new ContainerTypeId[Trials][];
            var done = new int[Trials];
            var failures = new ConcurrentQueue<string>();
            RunTrials(Readers + 1, Trials, (t, trial) =>
            {
                var values = dbs[trial].Containers;
                if (t == 0)
                {
                    // The writer.
                    try
                    {
                        var random = new Random(trial);
                        var mine = new ContainerTypeId[n];
                        for (var i = 0; i < n; i++)
                        {
                            mine[i] = Registrations[i](values, counterIds[trial], i % 2 == 0);
                            Thread.SpinWait(random.Next(200));
                        }

                        registered[trial] = mine;
                    }
                    finally
                    {
                        Volatile.Write(ref done[trial], 1);
                    }

                    return;
                }

                var counters = dbs[trial].Counters;
                var value = (uint)t << 24;
                int finished;
                do
                {
                    // Read the flag first, so the last pass runs after every registration.
                    finished = Volatile.Read(ref done[trial]);
                    foreach (var read in Reads)
                        if (read(values, counters, value++) is { } wrong)
                            failures.Enqueue($"trial {trial}: {wrong}");

                    var count = values.TypeCount;
                    if (count > 0 && values.GetEnvironment(new ContainerTypeId(count - 1)).TypeId != new ContainerTypeId(count - 1))
                        failures.Enqueue($"trial {trial}: the environment of type {count - 1} has another id");

                    values.AnyEqContainerMentions(value);
                }
                while (finished == 0);
            }, failures);

            AssertNoFailures(failures);
            for (var trial = 0; trial < Trials; trial++)
            {
                var values = dbs[trial].Containers;
                Assert.Equal(Enumerable.Range(0, n).Select(i => new ContainerTypeId(i)), registered[trial]);
                Assert.Equal(n, values.TypeCount);
                Assert.True(values.HasEqContainers);
                for (var i = 0; i < n; i++)
                {
                    Assert.Same(values.GetEnvironment(registered[trial][i]), Environments[i](values));
                    Assert.Equal(i % 2 == 0, values.GetEnvironment(registered[trial][i]).HoldsIds);
                }

                values.CheckInvariants();
            }
        }

        /// <summary>
        /// Two threads register all sixteen types at once, one in ascending order and one in descending, so that each
        /// type is registered by both, sometimes at the same moment, and new types race each other: each type gets one
        /// id and one environment, which both threads see, and the ids are dense.
        /// </summary>
        [Fact]
        public void TwoThreadsRegisteringAtOnceGetOneIdAndOneEnvironmentPerType()
        {
            const int Trials = 300;
            var n = Registrations.Length;
            var instances = Enumerable.Range(0, Trials).Select(_ => new ContainerValues(ParallelHeuristics.Serial)).ToArray();
            var ids = new ContainerTypeId[Trials, 2][];
            var envs = new ContainerEnvBase?[Trials, 2][];
            var failures = new ConcurrentQueue<string>();
            RunTrials(2, Trials, (t, trial) =>
            {
                var mine = new ContainerTypeId[n];
                var seen = new ContainerEnvBase?[n];
                for (var k = 0; k < n; k++)
                {
                    var i = t == 0 ? k : n - 1 - k;
                    mine[i] = Registrations[i](instances[trial], new CounterId(0), true);
                    seen[i] = Environments[i](instances[trial]);
                }

                ids[trial, t] = mine;
                envs[trial, t] = seen;
            }, failures);

            AssertNoFailures(failures);
            for (var trial = 0; trial < Trials; trial++)
            {
                var values = instances[trial];
                Assert.Equal(ids[trial, 0], ids[trial, 1]);
                Assert.Equal(Enumerable.Range(0, n).Select(i => new ContainerTypeId(i)), ids[trial, 0].OrderBy(id => id.Index));
                Assert.Equal(n, values.TypeCount);
                for (var i = 0; i < n; i++)
                {
                    var env = values.GetEnvironment(ids[trial, 0][i]);
                    Assert.Same(env, envs[trial, 0][i]);
                    Assert.Same(env, envs[trial, 1][i]);
                    Assert.Same(env, Environments[i](values));
                    Assert.Equal(ids[trial, 0][i], env.TypeId);
                }

                values.CheckInvariants();
            }
        }

        /// <summary>
        /// A type registered over base values comes to hold ids (<see cref="ContainerEnvBase.MarkHoldsIds"/>) on one
        /// thread while a new type registers on another, and a third thread watches the first type's environment: it
        /// never sees the environment hold ids without the container log it records into. Afterwards both types are
        /// registered, the first holds ids, and, with the log active, a new container of classes in either environment
        /// that holds ids is recorded in the one log, once.
        /// </summary>
        [Fact]
        public void ATypeComingToHoldIdsRacesANewType()
        {
            const int Trials = 300;
            var dbs = Enumerable.Range(0, Trials).Select(_ => new Database(ParallelHeuristics.Serial)).ToArray();
            var counterIds = dbs.Select(db => db.AddCounter()).ToArray();
            var first = dbs.Select((db, trial) => Register<byte>(db.Containers, counterIds[trial], false)).ToArray();
            var marked = new ContainerTypeId[Trials];
            var added = new ContainerTypeId[Trials];
            var pending = Enumerable.Repeat(2, Trials).ToArray();
            var failures = new ConcurrentQueue<string>();
            RunTrials(3, Trials, (t, trial) =>
            {
                var values = dbs[trial].Containers;
                switch (t)
                {
                    case 0:
                        try
                        {
                            marked[trial] = Register<byte>(values, counterIds[trial], true);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref pending[trial]);
                        }

                        break;
                    case 1:
                        try
                        {
                            added[trial] = Register<sbyte>(values, counterIds[trial], trial % 2 == 0);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref pending[trial]);
                        }

                        break;
                    default:
                        var env = values.GetEnvironment(first[trial]);
                        int left;
                        do
                        {
                            left = Volatile.Read(ref pending[trial]);
                            if (env.HoldsIds && ReferenceEquals(env.Log, values.Log) == false)
                                failures.Enqueue($"trial {trial}: the environment holds ids before it has the container log");

                            if (Read<sbyte>(values, dbs[trial].Counters, 7) is { } wrong)
                                failures.Enqueue($"trial {trial}: {wrong}");
                        }
                        while (left > 0);

                        break;
                }
            }, failures);

            AssertNoFailures(failures);
            for (var trial = 0; trial < Trials; trial++)
            {
                var values = dbs[trial].Containers;
                var newHoldsIds = trial % 2 == 0;
                Assert.Equal(first[trial], marked[trial]);
                Assert.Equal(new ContainerTypeId(1), added[trial]);
                Assert.Equal(2, values.TypeCount);
                Assert.True(values.HasEqContainers);
                var env = values.GetEnvironment(first[trial]);
                Assert.True(env.HoldsIds);
                var other = values.GetEnvironment(added[trial]);
                Assert.Equal(newHoldsIds, other.HoldsIds);

                var log = values.Log;
                log.Activate();
                ((ContainerEnv<TaggedContainer<byte>>)env).GetOrInsertAtRoot(new TaggedContainer<byte>(1, holdsIds: true), dbs[trial].Counters);
                log.Drain();
                Assert.Equal(1, log.AddedCount);
                if (newHoldsIds)
                {
                    ((ContainerEnv<TaggedContainer<sbyte>>)other).GetOrInsertAtRoot(new TaggedContainer<sbyte>(1, holdsIds: true), dbs[trial].Counters);
                    log.Drain();
                    Assert.Equal(2, log.AddedCount);
                }

                Assert.Same(newHoldsIds ? log : null, other.Log);
                log.Clear();

                values.CheckInvariants();
            }
        }

    }

}
