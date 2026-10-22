using System;
using System.IO;
using System.Linq;
using System.Text;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Diagnostics
{

    /// <summary>
    /// <see cref="Scope.Serialize"/>, egglog's <c>EGraph::serialize</c>: egglog's tests of it ported (the class and node
    /// counts of <c>test_serialize_617</c>, <c>test_serialize_subsume_status</c>, the root class of an extracted term and
    /// a sort name with a dash), and the shape egglog gives values, relations, sets, placeholders, edges and the JSON.
    /// </summary>
    public sealed class SerializeTests
    {

        /// <summary>
        /// egglog's <c>test_serialize_617</c>: after the rewrite <c>mkb x =&gt; mk x</c> and the unions of <c>mk 1</c>,
        /// <c>mk 3</c> and <c>mk 5</c>, there are two classes of <c>Node</c> and four of <c>i64</c>, and twelve nodes:
        /// eight rows and four values.
        /// </summary>
        [Fact]
        public void TheGraphOfIssue617HasSixClassesAndTwelveNodes()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var mk = graph.DefineConstructor<long, Node>("mk");
            var mkb = graph.DefineConstructor<long, Node>("mkb");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("mkb", r =>
            {
                var x = r.Var<long>("x");
                r.Rewrite(mkb.Of(x), mk.Of(x));
            });
            foreach (var n in new[] { 1L, 3L, 5L, 6L })
                graph.Root.Make(mkb.Of(n));

            graph.Root.Union(graph.Root.Make(mk.Of(1L)), graph.Root.Make(mk.Of(3L)));
            graph.Root.Union(graph.Root.Make(mk.Of(3L)), graph.Root.Make(mk.Of(5L)));
            graph.Root.Run(Schedule.Saturate(rules));

            var serialized = graph.Root.Serialize();

            Assert.True(serialized.IsComplete);
            Assert.Equal(6, serialized.Classes.Count);
            Assert.Equal(12, serialized.Nodes.Count);
            Assert.Equal(2, serialized.Classes.Count(c => c.Type == "Node"));
        }

        /// <summary>
        /// egglog's <c>test_serialize_subsume_status</c>: a subsumed row's node is marked.
        /// </summary>
        [Fact]
        public void ASubsumedRowsNodeIsMarked()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("a", options: new TableOptions { Subsumable = true });
            var b = graph.DefineConstructor<Node>("b", options: new TableOptions { Subsumable = true });
            graph.Root.Make(a.Of());
            graph.Root.Make(b.Of());
            graph.Root.Subsume(a.Of());

            var serialized = graph.Root.Serialize();

            Assert.True(serialized.IsComplete);
            Assert.True(serialized.TryGetNode("function-0-a", out var nodeA));
            Assert.True(serialized.TryGetNode("function-0-b", out var nodeB));
            Assert.True(nodeA.Subsumed);
            Assert.False(nodeB.Subsumed);
        }

        /// <summary>
        /// egglog's tests of the class of an extracted value, with a sort name holding a dash
        /// (<c>test_serialize_sort_name_with_dash</c>): the root's class is <c>{sort}-{cell}</c>, and is among the classes.
        /// </summary>
        [Fact]
        public void ARootIsItsClassUnderTheSortsName()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math-Expr");
            var lit = graph.DefineConstructor<long, Node>("Lit");
            var five = graph.Root.Make(lit.Of(5L));

            var serialized = graph.Root.Serialize(new SerializeOptions { Roots = new[] { five.Id } });

            var root = Assert.Single(serialized.RootClasses);
            Assert.StartsWith("Math-Expr-", root);
            Assert.True(serialized.TryGetClass(root, out var @class));
            Assert.Equal("Math-Expr", @class.Type);
            Assert.Equal(root, serialized.Nodes.Single(n => n.Op == "Lit").Class);
        }

        /// <summary>
        /// A function's row joins its value's class, beside the value's own node; values print as egglog prints them,
        /// a string quoted.
        /// </summary>
        [Fact]
        public void AFunctionsRowIsInItsValuesClass()
        {
            var graph = new EGraph();
            var f = graph.DefineFunction<long, long>("f");
            var g = graph.DefineFunction<string, double>("g");
            graph.Root.Assume(f.Of(1L).Is(5L), g.Of("a b").Is(1.5));

            var serialized = graph.Root.Serialize();

            Assert.True(serialized.TryGetNode("function-0-f", out var row));
            Assert.Equal("i64-5", row.Class);
            Assert.Equal(new[] { "primitive-i64-1" }, row.Children);
            Assert.Equal(1.0, row.Cost);
            Assert.True(serialized.TryGetNode("primitive-i64-5", out var five));
            Assert.Equal("5", five.Op);
            Assert.Equal("i64-5", five.Class);
            var key = serialized.Nodes.Single(n => n.Op == "\"a b\"");
            Assert.Equal("String", serialized.Classes.Single(c => c.Id == key.Class).Type);
            Assert.Contains(serialized.Nodes, n => n.Op == "1.5");
        }

        /// <summary>
        /// A relation's row is a class of its own, of egglog's relation sort <c>@{name}Sort</c>, which egglog's symbol
        /// generator names without dashes and counts when two names meet.
        /// </summary>
        [Fact]
        public void ARelationsRowIsAClassOfItsOwn()
        {
            var graph = new EGraph();
            var first = graph.DefineRelation<long>("a-b");
            var second = graph.DefineRelation<long>("ab");
            graph.Root.Assume(first.Of(1L), first.Of(2L), second.Of(1L));

            var serialized = graph.Root.Serialize();

            Assert.True(serialized.TryGetNode("function-0-a-b", out var row0));
            Assert.True(serialized.TryGetNode("function-1-a-b", out var row1));
            Assert.True(serialized.TryGetNode("function-0-ab", out var other));
            Assert.Equal("@abSort-0", row0.Class);
            Assert.Equal("@abSort-1", row1.Class);
            Assert.Equal("@abSort1-0", other.Class);
            Assert.Equal("@abSort", serialized.Classes.Single(c => c.Id == "@abSort-0").Type);
            Assert.Equal(new[] { "primitive-i64-1" }, row0.Children);
        }

        /// <summary>
        /// Rows are numbered in the table's storage order, as egglog's <c>for_each_while</c> walks it, not in content
        /// order: rows written one batch after another keep the order they were written in.
        /// </summary>
        [Fact]
        public void RowsAreNumberedInStorageOrder()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<long>("R");
            graph.Root.Assume(r.Of(3L));
            graph.Root.Assume(r.Of(1L));
            graph.Root.Assume(r.Of(2L));

            var serialized = graph.Root.Serialize();

            var children = Enumerable.Range(0, 3).Select(i => serialized.Nodes.Single(n => n.Id == $"function-{i}-R").Children.Single());
            Assert.Equal(new[] { "primitive-i64-3", "primitive-i64-1", "primitive-i64-2" }, children);
        }

        [Fact]
        public void TooManyRowsTruncateATable()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<long>("R");
            graph.Root.Assume(r.Of(1L), r.Of(2L), r.Of(3L));

            var serialized = graph.Root.Serialize(new SerializeOptions { MaxRowsPerTable = 2 });

            Assert.False(serialized.IsComplete);
            Assert.Equal(new[] { "R" }, serialized.TruncatedTables);
            Assert.Equal(2, serialized.Nodes.Count(n => n.Op == "R"));
            Assert.Equal("Truncated: R\n", serialized.OmittedDescription);
        }

        /// <summary>
        /// Past <see cref="SerializeOptions.MaxTables"/> tables with rows, the rest are discarded; a class whose every
        /// node was left out is one placeholder, <c>[...]</c>, of infinite cost, which the JSON writes as <c>null</c>.
        /// </summary>
        [Fact]
        public void TooManyTablesDiscardTheRestAndLeavePlaceholders()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var wrap = graph.DefineConstructor<Node, Node>("Wrap");
            var leaf = graph.DefineConstructor<Node>("Leaf");
            graph.Root.Make(wrap.Of(leaf.Of()));

            var serialized = graph.Root.Serialize(new SerializeOptions { MaxTables = 1 });

            Assert.Equal(new[] { "Leaf" }, serialized.DiscardedTables);
            Assert.Equal("Omitted: Leaf\n", serialized.OmittedDescription);
            Assert.True(serialized.TryGetNode("function-0-Wrap", out var row));
            var placeholder = Assert.Single(row.Children);
            Assert.StartsWith("dummy-Node-", placeholder);
            Assert.True(serialized.TryGetNode(placeholder, out var dummy));
            Assert.Equal("[...]", dummy.Op);
            Assert.Equal(double.PositiveInfinity, dummy.Cost);
            Assert.Contains("\"cost\": null", serialized.ToJson());
        }

        /// <summary>
        /// The edges into a class take its nodes in turn, as egglog's do: each serialization of the class, its own rows'
        /// outputs included, rotates them.
        /// </summary>
        [Fact]
        public void EdgesIntoAClassTakeItsNodesInTurn()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var pair = graph.DefineConstructor<Node, Node, Node>("Pair");
            var x = graph.Root.Make(a.Of());
            graph.Root.Union(x, graph.Root.Make(b.Of()));
            graph.Root.Make(pair.Of(x, x));

            var serialized = graph.Root.Serialize();

            Assert.True(serialized.TryGetNode("function-0-Pair", out var row));
            Assert.Equal(new[] { "function-0-B", "function-0-A" }, row.Children);
        }

        /// <summary>
        /// A set is a node <c>set-of</c> whose children are its elements, as an egglog container's inner values are.
        /// </summary>
        [Fact]
        public void ASetsElementsAreItsChildren()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            graph.Root.Assume(dom.Of("k").Is(Set.Of("a", "b")));

            var serialized = graph.Root.Serialize();

            var set = serialized.Nodes.Single(n => n.Op == "set-of");
            Assert.Equal(2, set.Children.Count);
            var elements = set.Children.Select(id => serialized.Nodes.Single(n => n.Id == id).Op).ToArray();
            Assert.Equal(new[] { "\"a\"", "\"b\"" }, elements);
            Assert.True(serialized.TryGetNode("function-0-dom", out var row));
            Assert.Equal(set.Class, row.Class);
        }

        /// <summary>
        /// A scope serializes its own view: a child that unions two classes of its frozen parent shows one class where the
        /// parent shows two.
        /// </summary>
        [Fact]
        public void AChildSerializesItsOwnView()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var x = graph.Root.Make(a.Of());
            var y = graph.Root.Make(b.Of());
            graph.Root.Freeze();
            var child = graph.Root.CreateChild();
            child.Union(x, y);

            Assert.Equal(2, graph.Root.Serialize().Classes.Count);
            Assert.Single(child.Serialize().Classes);
        }

        [Fact]
        public void SerializingInternsNothing()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            graph.Root.Assume(dom.Of("k").Is(Set.Of("never on its own")));
            var interned = graph.Values.InternedCount;

            graph.Root.Serialize().ToJson();

            Assert.Equal(interned, graph.Values.InternedCount);
        }

        /// <summary>
        /// The JSON is egraph-serialize's, as its <c>to_json_file</c> writes it: two-space indents, <c>\n</c> line ends,
        /// and the fields in its order.
        /// </summary>
        [Fact]
        public void TheJsonIsEgraphSerializes()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A", cost: 3);
            var x = graph.Root.Make(a.Of());

            var serialized = graph.Root.Serialize(new SerializeOptions { Roots = new[] { x.Id } });
            var id = serialized.RootClasses[0];

            var expected =
                "{\n" +
                "  \"nodes\": {\n" +
                "    \"function-0-A\": {\n" +
                "      \"op\": \"A\",\n" +
                "      \"children\": [],\n" +
                $"      \"eclass\": \"{id}\",\n" +
                "      \"cost\": 3.0,\n" +
                "      \"subsumed\": false\n" +
                "    }\n" +
                "  },\n" +
                "  \"root_eclasses\": [\n" +
                $"    \"{id}\"\n" +
                "  ],\n" +
                "  \"class_data\": {\n" +
                $"    \"{id}\": {{\n" +
                "      \"type\": \"Node\"\n" +
                "    }\n" +
                "  }\n" +
                "}";
            Assert.Equal(expected, serialized.ToJson());
            using var stream = new MemoryStream();
            serialized.WriteJson(stream);
            Assert.Equal(expected, Encoding.UTF8.GetString(stream.ToArray()));
        }

        /// <summary>
        /// A set's elements come in cell order, as an egglog set's do, each its interned value's node, printed as egglog
        /// prints it: Rust's <c>Debug</c>, a string escaped. egglog's <c>--to-json</c> lists <c>"b"</c>, <c>"a"</c>,
        /// <c>"q\"x"</c> under <c>set-of</c>.
        /// </summary>
        [Fact]
        public void ASetsElementsComeInCellOrderAndPrintAsEgglogs()
        {
            var graph = new EGraph();
            var text = graph.DefineRelation<string>("text");
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            graph.Root.Assume(text.Of("b"));
            graph.Root.Assume(text.Of("a"));
            graph.Root.Assume(dom.Of("k").Is(Set.Of("a", "b", "q\"x")));

            var serialized = graph.Root.Serialize();

            var set = serialized.Nodes.Single(n => n.Op == "set-of");
            var elements = set.Children.Select(id => serialized.Nodes.Single(n => n.Id == id)).ToArray();
            Assert.Equal(new[] { "\"b\"", "\"a\"", "\"q\\\"x\"" }, elements.Select(e => e.Op));
            Assert.All(elements, e => Assert.Matches("^String-[0-9]+$", e.Class));
        }

        /// <summary>
        /// A set's elements are told apart by their cells, not by their text: two values that print alike are two
        /// classes.
        /// </summary>
        [Fact]
        public void ElementsThatPrintAlikeAreTwoClasses()
        {
            var graph = new EGraph();
            graph.Values.Register<Ranked>("Ranked", format: _ => "p");
            graph.Values.RegisterSet<Ranked>();
            var f = graph.DefineFunction<long, Set<Ranked>>("f");
            graph.Root.Assume(f.Of(1L).Is(Set.Of(new Ranked(1), new Ranked(2))));

            var serialized = graph.Root.Serialize();

            var set = serialized.Nodes.Single(n => n.Op == "set-of");
            Assert.Equal(new[] { "primitive-Ranked-0", "primitive-Ranked-1" }, set.Children);
        }

        /// <summary>
        /// egglog reserves <c>@</c>, so its relation sorts never meet a user's sort; here a sort may be named
        /// <c>@abSort</c>, and the relation <c>ab</c>'s sort takes the next name.
        /// </summary>
        [Fact]
        public void ARelationsSortSkipsAUsersSortName()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("@abSort");
            var leaf = graph.DefineConstructor<long, Node>("Leaf");
            var ab = graph.DefineRelation<long>("ab");
            graph.Root.Make(leaf.Of(100L));
            graph.Root.Assume(ab.Of(1L));

            var serialized = graph.Root.Serialize();

            Assert.True(serialized.TryGetNode("function-0-ab", out var row));
            Assert.Equal("@abSort1-0", row.Class);
            Assert.True(serialized.TryGetNode("function-0-Leaf", out var leafRow));
            Assert.NotEqual(row.Class, leafRow.Class);
        }

        /// <summary>
        /// egglog has no contradiction relation, so the built-in one is left out, rows and all, and counts toward no limit.
        /// </summary>
        [Fact]
        public void TheContradictionRelationIsLeftOut()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<long>("R");
            graph.Root.Assume(graph.Contradiction.Of(), r.Of(1L));

            var serialized = graph.Root.Serialize(new SerializeOptions { MaxTables = 1 });

            Assert.True(serialized.IsComplete);
            Assert.DoesNotContain(serialized.Nodes, n => n.Op == "contradiction");
            Assert.Contains(serialized.Nodes, n => n.Op == "R");
        }

        /// <summary>
        /// The JSON escapes what serde_json escapes and nothing more: the quote, the backslash and the control characters
        /// below U+0020, in lowercase hex where they have no short form; an emoji stays as it is.
        /// </summary>
        [Fact]
        public void TheJsonEscapesAsSerdeDoes()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<string>("a\tb\u001b\u007f");
            graph.Root.Assume(r.Of("\U0001F600"));

            var json = graph.Root.Serialize().ToJson();

            Assert.Contains("\"function-0-a\\tb\\u001b\u007f\": {", json);
            Assert.Contains("\"op\": \"\\\"\U0001F600\\\"\"", json);
        }

        [Fact]
        public void TheOptionsAreChecked()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            var foreign = other.Root.Make(other.A.Of());

            Assert.Throws<ArgumentOutOfRangeException>(() => t.Root.Serialize(new SerializeOptions { MaxTables = -1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.Root.Serialize(new SerializeOptions { MaxRowsPerTable = -1 }));
            Assert.Throws<ArgumentException>(() => t.Root.Serialize(new SerializeOptions { Roots = new[] { foreign.Id } }));
        }

    }

}
