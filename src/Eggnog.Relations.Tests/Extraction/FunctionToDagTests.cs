using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Extractor.FunctionToDag"/>, egglog's <c>function_to_dag</c> (src/extract.rs:858-915): a
    /// whole function's rows extracted as terms, as <c>print-function</c> shows them.
    /// </summary>
    public class FunctionToDagTests
    {

        [Fact]
        public void RowsExtractWithTheirKeysAndOutputs()
        {
            // (function height (Expr) i64 :no-merge) over (Num 1), (Add (Num 1) (Num 2)).
            var fx = new ExtractionFixture();
            var expr = new EqSort("Expr");
            var num = fx.Constructor("Num", expr, 1, fx.I64);
            var add = fx.Constructor("Add", expr, 1, expr, expr);
            var height = fx.Function("height", fx.I64, expr);
            var one = fx.Make(num, fx.Int(1));
            var sum = fx.Make(add, one, fx.Make(num, fx.Int(2)));
            fx.Set(height, [one], fx.Int(0));
            fx.Set(height, [sum], fx.Int(1));

            var result = Extractor.FunctionToDag(fx.Schema, height, 10, true);
            Assert.Equal(["(height (Num 1))", "(height (Add (Num 1) (Num 2)))"], result.Inputs.Select(t => result.Dag.ToString(t)).ToArray());
            Assert.NotNull(result.Outputs);
            Assert.Equal(["0", "1"], result.Outputs.Select(t => result.Dag.ToString(t)).ToArray());

            var withoutOutputs = Extractor.FunctionToDag(fx.Schema, height, 10, false);
            Assert.Null(withoutOutputs.Outputs);
            Assert.Equal(2, withoutOutputs.Inputs.Count);
        }

        [Fact]
        public void CountLimitsTheRowsAndSubsumedRowsAreIncluded()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var num = fx.Constructor("Num", e, 1, fx.I64);
            for (var i = 0; i < 5; i++)
                fx.Make(num, fx.Int(i));

            fx.Subsume(num, fx.Int(0));

            var result = Extractor.FunctionToDag(fx.Schema, num, 3, true);
            Assert.Equal(3, result.Inputs.Count);
            Assert.Equal(3, result.Outputs!.Count);
            Assert.All(result.Inputs, t => Assert.StartsWith("(Num ", result.Dag.ToString(t)));

            var all = Extractor.FunctionToDag(fx.Schema, num, 100, true);
            Assert.Equal(5, all.Inputs.Count);
            var texts = all.Inputs.Select(t => all.Dag.ToString(t)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(["(Num 0)", "(Num 1)", "(Num 2)", "(Num 3)", "(Num 4)"], texts);

            // The subsumed row's output has no extractable node left.
            var outputs = all.Outputs!.Select(t => all.Dag.ToString(t)).ToArray();
            Assert.Single(outputs, o => o == "Unextractable");
            Assert.Empty(Extractor.FunctionToDag(fx.Schema, num, 0, true).Inputs);
        }

        [Fact]
        public void KeysWithNoTermBecomeUnextractable()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var tag = fx.Function("tag", fx.String, e);
            fx.Set(tag, [fx.EGraph.FreshId()], fx.Str("x"));

            var result = Extractor.FunctionToDag(fx.Schema, tag, 1, true);
            Assert.Equal("(tag Unextractable)", result.Dag.ToString(result.Inputs[0]));
            Assert.Equal("\"x\"", result.Dag.ToString(result.Outputs![0]));
            Assert.Equal(TermKind.Var, result.Dag[result.Dag[result.Inputs[0]].Children[0]].Kind);
        }

        [Fact]
        public void UndeclaredFunctionsAreRejected()
        {
            var fx = new ExtractionFixture();
            var undeclared = fx.EGraph.AddTable(new FunctionConfig { Schema = [ColumnTy.Id], Default = DefaultVal.FreshId, Merge = MergeFn.UnionId, Name = "u" });
            Assert.Throws<ArgumentException>(() => Extractor.FunctionToDag(fx.Schema, undeclared, 1, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => Extractor.FunctionToDag(fx.Schema, undeclared, -1, false));
        }

    }

}
