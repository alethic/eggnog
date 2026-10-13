using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    public sealed partial class Scenario02QueryDisjunctionTests
    {

        /// <summary>
        /// Compiles the container with its own split: the schema's cases are order, invoice and note.
        /// </summary>
        static (CosmosTheory C, Scope Schema) CompileContainer()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();

            var schema = graph.Root.CreateChild(c.Schema);
            DeclareContainer(c, schema);
            var compiled = schema.Run(c.Facts);
            Assert.True(compiled.Saturated);
            Assert.Equal(3, compiled.CasesOpened);                  // order, invoice, note
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));   // a note carries no ref
            Assert.Empty(schema.Children);
            schema.Freeze();
            return (c, schema);
        }

        [Fact]
        public void TwoKindsNamedTogetherLowerTheReference()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(c.Query);
            var q = AssertQuery(c, query);

            // The prototype's (run-schedule (saturate horn lattice split rewrite)), facts settled before the split.
            var report = query.Run(c.Saturation);

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Saturated);
            Assert.Equal(2, report.CasesOpened);                    // one case per value kind can still hold
            Assert.Empty(query.Children);                           // the cases were disposed when the run ended

            // (check (form (Query) "ref" "uuid-lower")) and (check (= $q (And (Or ...) (StrEq "ref" ...))))
            Assert.True(query.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.True(query.AreEqual(q, Lowered(c)));

            // (fail (check (form (Schema) "ref" "uuid-lower"))): read from the schema, and through the scoped handle.
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.False(query.Check(c.Form.In(c.Schema).Of("ref", "uuid-lower")));

            // (extract $q)
            var best = query.Extract(q);
            Assert.False(best.Mentions(c.UuidEq));
            Assert.Equal(1 + (1 + 3 + 3) + 3, best.Cost);           // And + Or of two StrEq + StrEq
            Assert.Equal(
                $"(And (Or (StrEq \"kind\" \"order\") (StrEq \"kind\" \"invoice\")) (StrEq \"ref\" \"{Ref}\"))",
                best.ToString());
        }

        [Fact]
        public void TheSameSplitDrivenByTheHost()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(c.Query);
            var q = AssertQuery(c, query);
            query.Run(Schedule.Saturate(c.Horn, c.Lattice));
            Assert.Empty(query.Children);

            // The caller's coverage obligation: kind is present in the query and holds one of dom(kind).
            Assert.True(query.Check(c.Present.Of("kind")));
            var kinds = query.Get(c.Dom.Of("kind"));
            Assert.Equal(ValueSet.Of("invoice", "order"), kinds);

            LiftReport lifted;
            using (var cases = query.OpenCases())
            {
                foreach (var kind in kinds)
                {
                    var k = cases.Add(c.Case);
                    Assert.True(k.IsCase);
                    k.Assume(c.Dom.Of("kind").Is(ValueSet.Of(kind)));
                    k.Run(Schedule.Saturate(c.Horn, c.Lattice));    // a case always stops on contradiction
                    Assert.False(k.IsContradictory);                // reads below would throw on a stopped case
                    Assert.True(k.Check(c.Form.Of("ref", "uuid-lower")));
                }

                Assert.Equal(2, query.Children.Count);
                lifted = cases.Lift();
            }

            Assert.Equal(2, lifted.LiveCases);
            Assert.Equal(0, lifted.VacuousCases);
            Assert.True(lifted.Rows >= 1);                          // form(ref, uuid-lower)
            Assert.True(lifted.Changed);
            Assert.Empty(query.Children);

            query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            Assert.Empty(query.Children);
            Assert.True(query.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.True(query.AreEqual(q, Lowered(c)));
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
        }

        [Fact]
        public void AQueryThatDoesNotMentionTheDiscriminatorDoesNotSplit()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(c.Query);
            var q = query.Make(c.UuidEq.Of("ref", Ref));
            query.Assume(c.WhereClause.Of(q));

            var report = query.Run(c.Saturation);

            Assert.True(report.Saturated);
            Assert.Equal(0, report.CasesOpened);                    // kind is not mentioned: the query split skips it
            Assert.Empty(query.Children);
            Assert.False(query.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.Equal($"(UuidEq \"ref\" \"{Ref}\")", query.Extract(q).ToString());
            Assert.Equal(1002, query.Extract(q).Cost);
        }

    }

}
