using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Rule-layer decision D10's order on the nodes of a <see cref="RandomExtractionGraph"/>, computed independently of
    /// the extractor: function id, then the columns left to right, the <c>Num</c> literal by its integer value and every
    /// id column by id.
    /// </summary>
    sealed class D10Order : IComparer<(int Function, Value[] Row)>
    {

        readonly RandomExtractionGraph graph;

        public D10Order(RandomExtractionGraph graph)
        {
            this.graph = graph;
        }

        public int Compare((int Function, Value[] Row) x, (int Function, Value[] Row) y)
        {
            var byFunction = graph.Functions[x.Function].Rep.CompareTo(graph.Functions[y.Function].Rep);
            if (byFunction != 0)
                return byFunction;

            for (var column = 0; column < x.Row.Length; column++)
            {
                if (x.Row[column] == y.Row[column])
                    continue;

                if (x.Function == 2 && column == 0)
                {
                    var values = graph.Fx.EGraph.BaseValues;
                    return values.Unwrap<Int64Codec, long>(x.Row[column]).CompareTo(values.Unwrap<Int64Codec, long>(y.Row[column]));
                }

                return x.Row[column].Rep.CompareTo(y.Row[column].Rep);
            }

            return 0;
        }

    }

}
