using System;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Terms
{

    /// <summary>
    /// A variadic primitive applied to a number of arguments keeps one repeated parameter type, not one per argument, so
    /// the calls it caches per arity cost the same whatever their arity.
    /// </summary>
    public sealed class VariadicPrimitiveTests
    {

        [Fact]
        public void ACallsParameterTypesRepeatTheElementType()
        {
            var graph = new EGraph();
            graph.Values.RegisterVec<long>();
            var f = graph.DefineFunction<Vec<long>>("f");
            var arguments = Enumerable.Range(0, 300).Select(i => (Term<long>)(long)i).ToArray();
            graph.Root.Assume(f.Of().Is(Primitives.VecOf<long>().Of(arguments)));

            Assert.Equal(300, graph.Root.Get(f.Of()).Count);
            var call = Primitives.VecOf<long>().Call(300);
            Assert.Equal(300, call.ParameterTypes.Count);
            Assert.All(call.ParameterTypes, t => Assert.Equal(typeof(long), t));
            Assert.IsType<RepeatedTypes>(call.ParameterTypes);
            Assert.Throws<ArgumentOutOfRangeException>(() => call.ParameterTypes[300]);
        }

    }

}
