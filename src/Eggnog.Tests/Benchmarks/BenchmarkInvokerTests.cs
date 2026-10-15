using System;
using System.Collections.Generic;

using Eggnog.Benchmarks.Probes;

using Xunit;

namespace Eggnog.Tests.Benchmarks
{

    /// <summary>
    /// <see cref="BenchmarkInvoker"/>, the allocation probe's driver of one benchmark case, over
    /// <see cref="InvokerFixture"/>: which life-cycle methods run for which benchmark method, and the parameters set.
    /// </summary>
    public sealed class BenchmarkInvokerTests
    {

        static BenchmarkInvoker Create(string name, Dictionary<string, string>? values = null) =>
            BenchmarkInvoker.Create(typeof(InvokerFixture).Assembly, name, values ?? []);

        [Fact]
        public void RunsTheLifeCycleMethodsOfTheNamedMethodOnly()
        {
            var invoker = Create("InvokerFixture.A");
            invoker.GlobalSetup();
            invoker.GlobalSetup();
            invoker.IterationSetup();
            Assert.Equal("SharedSetup,SetupA,IterationSetupA,A Size=3 Flag=False", invoker.Invoke());

            invoker.IterationCleanup();
            invoker.GlobalCleanup();
            Assert.Equal("SharedSetup,SetupA,IterationSetupA,A,SharedIterationCleanup,SharedCleanup,A Size=3 Flag=False", invoker.Invoke());
        }

        [Fact]
        public void AnotherMethodRunsItsOwn()
        {
            var invoker = Create("InvokerFixture.B");
            Assert.Equal("InvokerFixture.B", invoker.Name);
            invoker.GlobalSetup();
            invoker.IterationSetup();
            Assert.Equal("SharedSetup,SetupB,IterationSetupB,B", invoker.Invoke());

            invoker.IterationCleanup();
            invoker.GlobalCleanup();
            Assert.Equal("SharedSetup,SetupB,IterationSetupB,B,SharedIterationCleanup,CleanupB,SharedCleanup,B", invoker.Invoke());
        }

        [Fact]
        public void AValueTypeResultIsBoxedAndOperationsPerInvokeRead()
        {
            var invoker = Create("InvokerFixture.C", new() { ["Size"] = "5" });
            Assert.Equal(4, invoker.OperationsPerInvoke);
            Assert.Equal(5, invoker.Invoke());
        }

        [Fact]
        public void GivenParametersAreSetAndTheRestTakeTheirFirstValue()
        {
            var invoker = Create("InvokerFixture.A", new() { ["Flag"] = "true" });
            Assert.Equal(new KeyValuePair<string, object?>[] { new("Size", 3), new("Flag", true) }, invoker.Parameters);
            Assert.Equal("A Size=3 Flag=True", invoker.Invoke());
        }

        [Theory]
        [InlineData("InvokerFixture")]
        [InlineData("InvokerFixture.D")]
        [InlineData("NoFixture.A")]
        public void AnUnknownOrAmbiguousNameIsRejected(string name)
        {
            Assert.Throws<ArgumentException>(() => Create(name));
        }

        [Theory]
        [InlineData("Rows", "1")]
        [InlineData("Size", "many")]
        public void AnUnknownParameterOrBadValueIsRejected(string parameter, string value)
        {
            Assert.Throws<ArgumentException>(() => Create("InvokerFixture.A", new() { [parameter] = value }));
        }

    }

}
