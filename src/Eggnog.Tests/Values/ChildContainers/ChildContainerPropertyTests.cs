using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// A property of containers of classes in child scopes: random programs (<see cref="ContainerProgramGenerator"/>) of
    /// vectors, sets, maps, multisets, pairs and function values of classes and vectors of vectors, written, unioned,
    /// made by rules with container primitives and host batches, under writable and frozen roots, writable parents that
    /// keep writing after their children exist, and grandchildren, agree in every child, modulo ids, with a fresh root
    /// replaying the same commands: the rows of every table, the outcomes of reads, queries and extractions, and the
    /// costs of every class (<see cref="ContainerProgramRunner"/>). The invariants of every live context are checked
    /// after every step. A few hand-written programs (<see cref="AWritableAncestorsEchoOfAChildsContainerWriteAgreesWithAFreshRootReplayingIt"/>)
    /// pin the collisions of a writable ancestor's later writes with a child's that the random ones reach only now and
    /// then.
    /// </summary>
    /// <remarks>
    /// The expectation is a fresh root's replay, not egglog's binary: egglog has no writable parent under a live push,
    /// and the replay is the reference decisions row 9 states for a child. Environment variables:
    /// <c>EGGNOG_CHILD_CONTAINERS_SOAK</c> runs <see cref="Soak"/> for that many seconds over fresh seeds, from
    /// <c>EGGNOG_CHILD_CONTAINERS_SEED_BASE</c> if set (otherwise a random base, printed); <c>EGGNOG_CHILD_CONTAINERS_SEED</c>
    /// replays one seed in <see cref="Replay"/> and prints its program.
    /// </remarks>
    public sealed class ChildContainerPropertyTests
    {

        /// <summary>
        /// The seeds every run checks.
        /// </summary>
        static readonly int[] Seeds = Enumerable.Range(1, 12).ToArray();

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public ChildContainerPropertyTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// The fixed seeds' programs agree with their replays, and between them reach children's operations and
        /// snapshots of every shape.
        /// </summary>
        [Fact]
        public void ChildrensContainersAgreeWithAFreshRootReplayingTheSameCommands()
        {
            var snapshots = 0;
            var childOperations = 0;
            foreach (var seed in Seeds)
            {
                var runner = RunOne(seed);
                snapshots += runner.Snapshots;
                childOperations += runner.ChildOperations;
            }

            output.WriteLine($"seeds {Seeds[0]}..{Seeds[^1]}: {snapshots} snapshots, {childOperations} operations in children");
            Assert.True(childOperations > 10 * Seeds.Length, $"too few operations in children: {childOperations}");
        }

        /// <summary>
        /// A writable ancestor writes, at a child's key, the container the child wrote there before its own union made
        /// two classes one, written with the other class, so that the child's catch-up meets the ancestor's row beside
        /// its own and must merge their containers before the rows (decisions row 9): a vector, a pair or a set at the
        /// root under a child, a vector of vectors at the root under a child or a grandchild, and a vector of vectors at
        /// a writable child of a frozen root under its own child. Each agrees with a fresh root replaying it, as
        /// <see cref="ContainerProgramRunner"/> checks.
        /// </summary>
        [Theory]
        [InlineData("vector")]
        [InlineData("pair")]
        [InlineData("set")]
        [InlineData("nested vector")]
        [InlineData("nested vector in a grandchild")]
        [InlineData("nested vector under a frozen root")]
        public void AWritableAncestorsEchoOfAChildsContainerWriteAgreesWithAFreshRootReplayingIt(string program)
        {
            static ContainerProgramStep Scope(ContainerProgramStepKind kind, int scope, int parent = 0) => new(kind, scope, parent, default, 0, 0, 0, 0, 0, 0);
            static ContainerProgramStep Op(int scope, ContainerProgramOp op, long a, long b, long c, long k) =>
                new(ContainerProgramStepKind.Operation, scope, 0, op, a, b, c, k, 0, 0);

            const ContainerProgramStepKind create = ContainerProgramStepKind.Create;
            const ContainerProgramStepKind snapshot = ContainerProgramStepKind.Snapshot;
            List<ContainerProgramStep> Echo(ContainerProgramOp op) =>
            [
                Scope(create, 1), Op(1, op, 1, 3, 1, 150), Op(1, ContainerProgramOp.Union, 1, 2, 0, 101), Op(0, op, 2, 3, 2, 150),
                Scope(snapshot, 1), Op(1, ContainerProgramOp.Extract, 1, 3, 0, 0), Scope(snapshot, 1), Scope(snapshot, 0),
            ];

            var steps = program switch
            {
                "vector" => Echo(ContainerProgramOp.Vector),
                "pair" => Echo(ContainerProgramOp.Pair),
                "set" => Echo(ContainerProgramOp.Set),
                "nested vector" => Echo(ContainerProgramOp.NestedVector),
                "nested vector in a grandchild" =>
                [
                    Scope(create, 1), Scope(create, 2, 1), Op(2, ContainerProgramOp.NestedVector, 1, 1, 1, 250), Op(2, ContainerProgramOp.Union, 1, 2, 0, 201),
                    Op(0, ContainerProgramOp.NestedVector, 2, 2, 2, 250), Scope(snapshot, 2), Scope(snapshot, 1),
                ],
                "nested vector under a frozen root" =>
                [
                    Scope(ContainerProgramStepKind.Freeze, 0), Scope(create, 1), Scope(create, 2, 1), Op(2, ContainerProgramOp.NestedVector, 1, 1, 1, 250),
                    Op(2, ContainerProgramOp.Union, 1, 2, 0, 201), Op(1, ContainerProgramOp.NestedVector, 2, 2, 2, 250), Scope(snapshot, 2), Scope(snapshot, 1),
                ],
                _ => throw new ArgumentOutOfRangeException(nameof(program)),
            };

            var runner = new ContainerProgramRunner(steps);
            runner.Run();
            Assert.True(runner.Failures.Count == 0, string.Join("\n", runner.Failures));
            Assert.Equal(steps.Count(step => step.Kind == snapshot), runner.Snapshots);
        }

        /// <summary>
        /// A time-boxed soak over fresh seeds, for <c>EGGNOG_CHILD_CONTAINERS_SOAK</c> seconds.
        /// </summary>
        [Fact]
        public void Soak()
        {
            var seconds = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_CHILD_CONTAINERS_SOAK"), out var s) && s > 0 ? s : 0;
            Assert.SkipUnless(seconds > 0, "Set EGGNOG_CHILD_CONTAINERS_SOAK to a number of seconds to soak the child containers property.");

            var start = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_CHILD_CONTAINERS_SEED_BASE"), out var b) ? b : Random.Shared.Next(1 << 30);
            output.WriteLine($"soaking from seed {start} for {seconds} s");
            var clock = Stopwatch.StartNew();
            var seed = start;
            while (clock.Elapsed.TotalSeconds < seconds)
                RunOne(seed++);

            output.WriteLine($"seeds {start}..{seed - 1} agree");
        }

        /// <summary>
        /// Replays the program of the seed <c>EGGNOG_CHILD_CONTAINERS_SEED</c> names and prints it.
        /// </summary>
        [Fact]
        public void Replay()
        {
            var spec = Environment.GetEnvironmentVariable("EGGNOG_CHILD_CONTAINERS_SEED");
            Assert.SkipUnless(int.TryParse(spec, out var seed), "Set EGGNOG_CHILD_CONTAINERS_SEED to a seed to replay one child containers program.");

            var steps = ContainerProgramGenerator.Generate(seed, out var shape);
            output.WriteLine($"seed {seed}, {shape}:");
            foreach (var step in steps)
                output.WriteLine("  " + step);

            RunOne(seed);
        }

        /// <summary>
        /// Runs the program of <paramref name="seed"/> and fails with its seed, shape and first failures if it does not
        /// agree with its replays.
        /// </summary>
        static ContainerProgramRunner RunOne(int seed)
        {
            var steps = ContainerProgramGenerator.Generate(seed, out var shape);
            var runner = new ContainerProgramRunner(steps);
            runner.Run();
            if (runner.Failures.Count > 0)
                Assert.Fail($"seed {seed} ({shape}, {steps.Count} steps; replay it with EGGNOG_CHILD_CONTAINERS_SEED={seed}): {runner.Failures.Count} failures, first:\n{string.Join("\n", runner.Failures.Take(3))}");

            return runner;
        }

    }

}
