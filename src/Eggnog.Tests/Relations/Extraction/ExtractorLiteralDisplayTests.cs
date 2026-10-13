using System;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Literal text in extracted terms: strings and floats render as egglog displays them, through <c>Literal</c>'s
    /// <c>Display</c>, not through the codecs' Rust <c>Debug</c> format.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class ExtractorLiteralDisplayTests
    {

        [Fact]
        public void StringLiteralsRenderAsEgglogDisplaysThem()
        {
            // egglog's base sorts reconstruct Literal::String / Literal::Float (src/sort/string.rs:38-47,
            // src/sort/f64.rs:54-63), and TermDag::to_string writes them with Literal's Display
            // (egglog-ast/src/generic_ast_helpers.rs:740-758): a string raw between quotes, a float with f64's Display
            // (never exponential) plus ".0" when it reads as an integer. BaseSort does the same, where the codecs' Rust
            // Debug format would escape strings and write exponents.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var name = fx.Constructor("Name", e, 1, fx.String);
            var quoted = fx.Make(name, fx.Str("say \"hi\""));

            Assert.Equal("(Name \"say \"hi\"\")", Extractor.ExtractValueToString(fx.Schema, e, quoted, out _));
        }

        [Fact]
        public void FloatLiteralsRenderAsEgglogDisplaysThem()
        {
            // The float half of StringLiteralsRenderAsEgglogDisplaysThem: egglog prints 1e20 as 100000000000000000000
            // and 1e-7 as 0.0000001 (f64 Display), where Rust Debug gives 1e20 and 1e-7.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var real = fx.Constructor("Real", e, 1, fx.F64);
            var big = fx.Make(real, fx.Float(1e20));
            var small = fx.Make(real, fx.Float(1e-7));

            Assert.Equal("(Real 100000000000000000000)", Extractor.ExtractValueToString(fx.Schema, e, big, out _));
            Assert.Equal("(Real 0.0000001)", Extractor.ExtractValueToString(fx.Schema, e, small, out _));
        }

    }

}
