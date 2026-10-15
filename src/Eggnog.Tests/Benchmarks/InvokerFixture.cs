using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Tests.Benchmarks
{

    /// <summary>
    /// A benchmark class for <see cref="BenchmarkInvokerTests"/>: three benchmark methods, life-cycle methods for one of
    /// them and for all, and two parameters. Every call is recorded, and <see cref="A"/> and <see cref="B"/> return the
    /// record.
    /// </summary>
    public sealed class InvokerFixture
    {

        readonly List<string> calls = [];

        /// <summary>
        /// Gets or sets a parameter whose first value is 3.
        /// </summary>
        [Params(3, 5)]
        public int Size { get; set; }

        /// <summary>
        /// Gets or sets a parameter whose first value is <c>false</c>.
        /// </summary>
        [ParamsAllValues]
        public bool Flag { get; set; }

        /// <summary>
        /// The global setup of every method.
        /// </summary>
        [GlobalSetup]
        public void SharedSetup() => calls.Add(nameof(SharedSetup));

        /// <summary>
        /// The global setup of <see cref="A"/>.
        /// </summary>
        [GlobalSetup(Target = nameof(A))]
        public void SetupA() => calls.Add(nameof(SetupA));

        /// <summary>
        /// The global setup of <see cref="B"/>.
        /// </summary>
        [GlobalSetup(Target = nameof(B))]
        public void SetupB() => calls.Add(nameof(SetupB));

        /// <summary>
        /// The iteration setup of <see cref="A"/>.
        /// </summary>
        [IterationSetup(Target = nameof(A))]
        public void IterationSetupA() => calls.Add(nameof(IterationSetupA));

        /// <summary>
        /// The iteration setup of <see cref="B"/>.
        /// </summary>
        [IterationSetup(Target = nameof(B))]
        public void IterationSetupB() => calls.Add(nameof(IterationSetupB));

        /// <summary>
        /// The iteration cleanup of every method.
        /// </summary>
        [IterationCleanup]
        public void SharedIterationCleanup() => calls.Add(nameof(SharedIterationCleanup));

        /// <summary>
        /// The global cleanup of <see cref="B"/>.
        /// </summary>
        [GlobalCleanup(Target = nameof(B))]
        public void CleanupB() => calls.Add(nameof(CleanupB));

        /// <summary>
        /// The global cleanup of every method.
        /// </summary>
        [GlobalCleanup]
        public void SharedCleanup() => calls.Add(nameof(SharedCleanup));

        /// <summary>
        /// A benchmark method that returns the calls so far and the parameters.
        /// </summary>
        /// <returns>The calls, separated by commas, then the parameters.</returns>
        [Benchmark]
        public string A()
        {
            calls.Add(nameof(A));
            return string.Join(",", calls) + $" Size={Size} Flag={Flag}";
        }

        /// <summary>
        /// A benchmark method that returns the calls so far.
        /// </summary>
        /// <returns>The calls, separated by commas.</returns>
        [Benchmark]
        public string B()
        {
            calls.Add(nameof(B));
            return string.Join(",", calls);
        }

        /// <summary>
        /// A benchmark method that returns <see cref="Size"/>, a value of a value type.
        /// </summary>
        /// <returns>The size.</returns>
        [Benchmark(OperationsPerInvoke = 4)]
        public int C() => Size;

    }

}
