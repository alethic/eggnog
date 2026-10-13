using Xunit;

namespace Eggnog.Tests.Cosmos.Literal
{

    /// <summary>
    /// <c>prototypes/cosmos-facts/07-sweep-links.egg</c>, as written: sweep's real <c>links</c> container and the
    /// queries of its facade probe. <c>data.type</c> is a required enum <c>park | map | spot</c> discriminating a
    /// <c>oneOf</c>; <c>park</c> and <c>map</c> require a canonical lowercase <c>data.data.parkId</c>, <c>spot</c> has
    /// none. Each <c>(push)</c> block is a fresh graph.
    /// </summary>
    /// <remarks>
    /// The expected figures, extracted strings and <c>print-size</c> counts are those of the egglog CLI built from the
    /// vendored <c>ext/egglog</c> at commit <c>5e655cae</c>: the file exits 0, every <c>(extract $q)</c> prints the
    /// string asserted here, and the offline-refresh block prints <c>Assume</c> 6, <c>parent</c> 7 and <c>form</c> 40.
    /// Eggnog breaks extraction ties by content (function, then columns left to right, classes by id) where egglog
    /// breaks them by scan order; every string here is the one egglog prints, except the per-kind park query's, where
    /// Eggnog extracts a commuted spelling of equal cost (see that test).
    /// </remarks>
    public sealed class Literal07SweepLinksTests
    {

        /// <summary>The park id the probe's queries ask for.</summary>
        public const string Park = "11111111-1111-1111-1111-111111111111";

        /// <summary>The map id the map query asks for.</summary>
        public const string Map = "22222222-2222-2222-2222-222222222222";

        /// <summary>The offline refresh's instant.</summary>
        public const string Since = "2026-08-02T12:00:00.000Z";

        /// <summary>
        /// The prelude plus what the schema compiler would emit for the <c>links</c> container: its facts and the three
        /// branch rules of the <c>data.type</c> discriminator.
        /// </summary>
        /// <returns>The theory, grounded.</returns>
        public static PrototypeTheory Container()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var schema = c.Schema.Of();
            c.Graph.Root.Assume(
                c.Present.Of(schema, "type"),
                c.Types.Of(schema, "type").Is(ValueSet.Of("string")),
                c.Present.Of(schema, "linkId"),
                c.Types.Of(schema, "linkId").Is(ValueSet.Of("number")),
                c.Lo.Of(schema, "linkId").Is(1L),
                c.Hi.Of(schema, "linkId").Is(2147483647L),
                c.Present.Of(schema, "data.type"),
                c.Dom.Of(schema, "data.type").Is(ValueSet.Of("park", "map", "spot")),
                c.Discriminator.Of("data.type", "park"),
                c.Discriminator.Of("data.type", "map"),
                c.Discriminator.Of("data.type", "spot"),
                c.Present.Of(schema, "data.guid"),
                c.Form.Of(schema, "data.guid", "uuid-lower"),
                c.Present.Of(schema, "data.label"),
                c.Types.Of(schema, "data.label").Is(ValueSet.Of("string")),
                c.Present.Of(schema, "data.offline"),
                c.Types.Of(schema, "data.offline").Is(ValueSet.Of("boolean")),
                c.Present.Of(schema, "data.metadata"),
                c.Form.Of(schema, "data.metadata.createUtcTime", "instant-ms"),
                c.Form.Of(schema, "data.metadata.changeUtcTime", "instant-ms"),
                c.Form.Of(schema, "data.metadata.deleteUtcTime", "instant-ms"));
            c.Horn.DefineRule("data.type = park", r =>
            {
                var x = r.Var<Ctx>("c");
                r.When(c.Present.Of(x, "data.type"), c.Dom.Of(x, "data.type").Is(ValueSet.Of("park")));
                r.Then(c.Present.Of(x, "data.data.parkId"), c.Form.Of(x, "data.data.parkId", "uuid-lower"));
            });
            c.Horn.DefineRule("data.type = map", r =>
            {
                var x = r.Var<Ctx>("c");
                r.When(c.Present.Of(x, "data.type"), c.Dom.Of(x, "data.type").Is(ValueSet.Of("map")));
                r.Then(
                    c.Present.Of(x, "data.data.parkId"),
                    c.Form.Of(x, "data.data.parkId", "uuid-lower"),
                    c.Present.Of(x, "data.data.mapId"),
                    c.Form.Of(x, "data.data.mapId", "uuid-lower"));
            });
            c.Horn.DefineRule("data.type = spot", r =>
            {
                var x = r.Var<Ctx>("c");
                r.When(c.Present.Of(x, "data.type"), c.Dom.Of(x, "data.type").Is(ValueSet.Of("spot")));
                r.Then(c.Present.Of(x, "data.data.location"));
            });
            return c;
        }

        /// <summary>
        /// <c>(let $q term) (where $q) (run-schedule (saturate horn lattice split rewrite))</c>.
        /// </summary>
        /// <param name="c">The container.</param>
        /// <param name="term">The query's predicate.</param>
        /// <returns>The predicate's class.</returns>
        public static Pred Ask(PrototypeTheory c, ConstructorTerm<Pred> term)
        {
            var root = c.Graph.Root;
            var q = root.Make(term);
            root.Assume(c.WhereClause.Of(q));
            var report = root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));
            Assert.True(report.Saturated);
            return q;
        }

        /// <summary>
        /// The offline refresh: park and map links of one park, written IN, that are offline and changed since an
        /// instant.
        /// </summary>
        /// <param name="c">The container.</param>
        /// <param name="uuid">The UUID comparison or its lowering.</param>
        /// <param name="instant">The instant comparison or its lowering.</param>
        /// <returns>The predicate.</returns>
        public static ConstructorTerm<Pred> OfflineRefresh(PrototypeTheory c, Term<Pred> uuid, Term<Pred> instant) =>
            c.And.Of(
                c.And.Of(c.Or.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.type", "map")), uuid),
                c.And.Of(c.BoolTrue.Of("data.offline"), instant));

        [Fact]
        public void TheMapQueryLowers()
        {
            var c = Container();
            var root = c.Graph.Root;
            var q = Ask(c, c.And.Of(c.StrEq.Of("data.type", "map"), c.UuidEq.Of("data.data.mapId", Map)));

            Assert.True(root.AreEqual(q, c.And.Of(c.StrEq.Of("data.type", "map"), c.StrEq.Of("data.data.mapId", Map))));
            PrintSize.Matches(c, "(And 2) (Assume 6) (BoolTrue 0) (InstantGt 0) (IsDefined 0) (IsString 0) (Not 0) (NumGt 0) (Or 0) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 2) (StrGe 0) (StrGt 0) (UuidEq 1) (admits 4) (conjunct 3) (contradiction 2) (discriminator 3) (dom 12) (dom-cases 3) (dom-reported 3) (form 43) (form-cases 12) (hi 8) (inset 2) (lo 8) (ne 0) (parent 7) (present 69) (present-cases 20) (types 83) (where 1)");
            var best = root.Extract(q);
            Assert.Equal($"(And (StrEq \"data.type\" \"map\") (StrEq \"data.data.mapId\" \"{Map}\"))", best.ToString());
            Assert.Equal(7, best.Cost);
        }

        [Fact]
        public void ParkAndMapWrittenInLowerTheParkId()
        {
            var c = Container();
            var root = c.Graph.Root;
            var q = Ask(c, c.And.Of(c.Or.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.type", "map")), c.UuidEq.Of("data.data.parkId", Park)));

            Assert.True(root.Check(c.Form.Of(c.Query.Of(), "data.data.parkId", "uuid-lower")));
            Assert.True(root.AreEqual(q, c.And.Of(c.Or.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.type", "map")), c.StrEq.Of("data.data.parkId", Park))));
            PrintSize.Matches(c, "(And 2) (Assume 6) (BoolTrue 0) (InstantGt 0) (IsDefined 0) (IsString 0) (Not 0) (NumGt 0) (Or 2) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 3) (StrGe 0) (StrGt 0) (UuidEq 1) (admits 6) (conjunct 3) (contradiction 1) (discriminator 3) (dom 12) (dom-cases 3) (dom-reported 3) (form 40) (form-cases 12) (hi 8) (inset 4) (lo 8) (ne 0) (parent 7) (present 66) (present-cases 20) (types 80) (where 1)");
            var best = root.Extract(q);
            Assert.Equal($"(And (Or (StrEq \"data.type\" \"park\") (StrEq \"data.type\" \"map\")) (StrEq \"data.data.parkId\" \"{Park}\"))", best.ToString());
            Assert.Equal(11, best.Cost);
        }

        [Fact]
        public void ParkAndMapSpeltPerKindLowerTheParkId()
        {
            var c = Container();
            var root = c.Graph.Root;
            var q = Ask(c, c.Or.Of(
                c.And.Of(c.StrEq.Of("data.type", "park"), c.UuidEq.Of("data.data.parkId", Park)),
                c.And.Of(c.StrEq.Of("data.type", "map"), c.UuidEq.Of("data.data.parkId", Park))));

            Assert.Equal(ValueSet.Of("map", "park"), root.Get(c.Dom.Of(c.Query.Of(), "data.type")));
            Assert.True(root.Check(c.Dom.Of(c.Query.Of(), "data.type").Is(ValueSet.Of("park", "map"))));
            Assert.True(root.Check(c.Form.Of(c.Query.Of(), "data.data.parkId", "uuid-lower")));
            Assert.True(root.AreEqual(q, c.Or.Of(
                c.And.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.data.parkId", Park)),
                c.And.Of(c.StrEq.Of("data.type", "map"), c.StrEq.Of("data.data.parkId", Park)))));
            PrintSize.Matches(c, "(And 4) (Assume 6) (BoolTrue 0) (InstantGt 0) (IsDefined 0) (IsString 0) (Not 0) (NumGt 0) (Or 2) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 3) (StrGe 0) (StrGt 0) (UuidEq 1) (admits 9) (conjunct 1) (contradiction 1) (discriminator 3) (dom 12) (dom-cases 3) (dom-reported 3) (form 40) (form-cases 12) (hi 8) (inset 3) (lo 8) (ne 0) (parent 7) (present 66) (present-cases 20) (types 80) (where 1)");
            // egglog prints (Or (And (StrEq "data.type" "park") (StrEq "data.data.parkId" …)) (And (StrEq "data.type"
            // "map") (StrEq "data.data.parkId" …))), cost 15. Eggnog extracts a spelling of the same cost that commutes
            // the second conjunction: extraction breaks the tie between (And map parkId) and (And parkId map) by
            // content, the lower class id first, and the host made the parkId comparison before the map equality. The
            // test keeps Eggnog's answer, and checks that egglog's spelling is in the same class.
            var best = root.Extract(q);
            Assert.Equal(
                $"(Or (And (StrEq \"data.type\" \"park\") (StrEq \"data.data.parkId\" \"{Park}\")) (And (StrEq \"data.data.parkId\" \"{Park}\") (StrEq \"data.type\" \"map\")))",
                best.ToString());
            Assert.Equal(15, best.Cost);
            Assert.True(root.AreEqual(q, c.Or.Of(
                c.And.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.data.parkId", Park)),
                c.And.Of(c.StrEq.Of("data.data.parkId", Park), c.StrEq.Of("data.type", "map")))));
        }

        [Fact]
        public void TheOfflineRefreshLowersEverything()
        {
            var c = Container();
            var root = c.Graph.Root;
            var q = Ask(c, OfflineRefresh(c, c.UuidEq.Of("data.data.parkId", Park), c.InstantGt.Of("data.metadata.changeUtcTime", Since)));

            Assert.True(root.AreEqual(q, OfflineRefresh(c, c.StrEq.Of("data.data.parkId", Park), c.StrGt.Of("data.metadata.changeUtcTime", Since))));

            // (print-size Assume) (print-size parent) (print-size form): three cases under (Schema), three under
            // (Query).
            Assert.Equal(6, root.CountRows(c.Assume));
            Assert.Equal(7, root.CountRows(c.Parent));
            Assert.Equal(40, root.CountRows(c.Form));
            PrintSize.Matches(c, "(And 6) (Assume 6) (BoolTrue 1) (InstantGt 1) (IsDefined 0) (IsString 0) (Not 0) (NumGt 0) (Or 2) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 3) (StrGe 0) (StrGt 1) (UuidEq 1) (admits 8) (conjunct 7) (contradiction 1) (discriminator 3) (dom 12) (dom-cases 3) (dom-reported 3) (form 40) (form-cases 12) (hi 8) (inset 4) (lo 8) (ne 0) (parent 7) (present 70) (present-cases 21) (types 80) (where 1)");
            var best = root.Extract(q);
            Assert.Equal(
                $"(And (And (Or (StrEq \"data.type\" \"park\") (StrEq \"data.type\" \"map\")) (StrEq \"data.data.parkId\" \"{Park}\")) (And (BoolTrue \"data.offline\") (StrGt \"data.metadata.changeUtcTime\" \"{Since}\")))",
                best.ToString());
            Assert.Equal(18, best.Cost);
        }

        [Fact]
        public void TheFacadeFilterDropsTheWholeEnum()
        {
            var c = Container();
            var root = c.Graph.Root;
            var q = Ask(c, c.And.Of(
                c.StrEq.Of("type", "Link"),
                c.Or.Of(c.StrEq.Of("data.type", "park"), c.Or.Of(c.StrEq.Of("data.type", "map"), c.StrEq.Of("data.type", "spot")))));

            Assert.True(root.AreEqual(q, c.StrEq.Of("type", "Link")));
            PrintSize.Matches(c, "(And 2) (Assume 6) (BoolTrue 0) (InstantGt 0) (IsDefined 0) (IsString 0) (Not 0) (NumGt 0) (Or 4) (PFalse 0) (PTrue 1) (Query 1) (Schema 1) (StrEq 4) (StrGe 0) (StrGt 0) (UuidEq 0) (admits 7) (conjunct 2) (contradiction 0) (discriminator 3) (dom 12) (dom-cases 3) (dom-reported 3) (form 38) (form-cases 12) (hi 8) (inset 6) (lo 8) (ne 0) (parent 7) (present 64) (present-cases 20) (types 78) (where 1)");
            var best = root.Extract(q);
            Assert.Equal("(StrEq \"type\" \"Link\")", best.ToString());
            Assert.Equal(3, best.Cost);
        }

        [Fact]
        public void ASpotMayBeAmongTheRowsSoNothingLowers()
        {
            var c = Container();
            var root = c.Graph.Root;
            var q = Ask(c, c.And.Of(c.Not.Of(c.StrEq.Of("data.type", "map")), c.UuidEq.Of("data.data.parkId", Park)));

            Assert.False(root.Check(c.Form.Of(c.Query.Of(), "data.data.parkId", "uuid-lower")));
            PrintSize.Matches(c, "(And 2) (Assume 6) (BoolTrue 0) (InstantGt 0) (IsDefined 0) (IsString 0) (Not 1) (NumGt 0) (Or 0) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 1) (StrGe 0) (StrGt 0) (UuidEq 1) (admits 1) (conjunct 3) (contradiction 1) (discriminator 3) (dom 8) (dom-cases 2) (dom-reported 2) (form 38) (form-cases 12) (hi 8) (inset 1) (lo 8) (ne 4) (parent 7) (present 66) (present-cases 20) (types 78) (where 1)");
            var best = root.Extract(q);
            Assert.True(best.Mentions(c.UuidEq));
            Assert.Equal($"(And (Not (StrEq \"data.type\" \"map\")) (UuidEq \"data.data.parkId\" \"{Park}\"))", best.ToString());
            Assert.Equal(1007, best.Cost);
        }

    }

}
