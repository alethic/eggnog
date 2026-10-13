using System;
using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ExternalFunctionRegistry"/> (egglog's <c>DenseIdMapWithReuse</c>) and
    /// <see cref="ExternalFunction"/>.
    /// </summary>
    public class ExternalFunctionRegistryTests
    {

        static ExternalFunction Constant(uint value) => ExternalFunction.Create((ExecutionState _, ReadOnlySpan<uint> _, out uint result) =>
        {
            result = value;
            return true;
        });

        [Fact]
        public void FreedIdsAreReusedMostRecentFirst()
        {
            var registry = new ExternalFunctionRegistry();
            var f0 = Constant(0);
            var a = registry.Add(f0);
            var b = registry.Add(Constant(1));
            var c = registry.Add(Constant(2));
            Assert.Equal(new[] { 0, 1, 2 }, new[] { a.Rep, b.Rep, c.Rep });
            Assert.Same(f0, registry[a]);

            Assert.True(registry.Remove(a, out var removed));
            Assert.Same(f0, removed);
            Assert.True(registry.Remove(c, out _));
            Assert.False(registry.Remove(c, out _));
            Assert.False(registry.TryGet(c, out _));
            Assert.Throws<KeyNotFoundException>(() => registry[a]);
            Assert.Equal(1, registry.Count);

            // egglog pops its free list: the last id freed comes back first.
            Assert.Equal(c, registry.Add(Constant(3)));
            Assert.Equal(a, registry.Add(Constant(4)));
            Assert.Equal(new ExternalFunctionId(3), registry.Add(Constant(5)));
        }

        [Fact]
        public void ClonesShareFunctionsButNotIds()
        {
            var registry = new ExternalFunctionRegistry();
            var f = Constant(7);
            var id = registry.Add(f);
            registry.Remove(registry.Add(Constant(8)), out _);
            var clone = registry.Clone();

            Assert.Same(f, clone[id]);
            Assert.Equal(new ExternalFunctionId(1), clone.Add(Constant(9)));
            Assert.Equal(new ExternalFunctionId(1), registry.Add(Constant(10)));
            clone.Remove(id, out _);
            Assert.True(registry.TryGet(id, out _));
        }

        [Fact]
        public void ArrayOverloadInvokesOnTheSlice()
        {
            var sum = ExternalFunction.Create((ExecutionState _, ReadOnlySpan<uint> args, out uint result) =>
            {
                result = 0;
                foreach (var arg in args)
                    result += arg;

                return args.Length > 0;
            });

            var state = TableTestUtil.EmptyState();
            Assert.True(sum.TryInvoke(state, new uint[] { 9, 1, 2, 3, 9 }, 1, 3, out var total));
            Assert.Equal(6u, total);
            Assert.False(sum.TryInvoke(state, new uint[] { 1 }, 1, 0, out _));
            Assert.Throws<ArgumentNullException>(() => ExternalFunction.Create(null!));
        }

    }

}
