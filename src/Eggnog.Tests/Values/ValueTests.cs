using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Column types, codecs and interning: which types may be value types, how values are stored, and reads that never
    /// intern.
    /// </summary>
    public sealed class ValueTests
    {

        [Fact]
        public void TheBuiltInTypesHaveTheirNames()
        {
            var values = new EGraph().Values;
            Assert.Equal("i64", values.TypeOf<long>().Name);
            Assert.Equal("i32", values.TypeOf<int>().Name);
            Assert.Equal("bool", values.TypeOf<bool>().Name);
            Assert.Equal("f64", values.TypeOf<double>().Name);
            Assert.Equal("String", values.TypeOf<string>().Name);
            Assert.Equal("Set<String>", values.TypeOf<Set<string>>().Name);
            Assert.Equal("Set<i64>", values.TypeOf<Set<long>>().Name);
            Assert.Equal("Set<i32>", values.TypeOf<Set<int>>().Name);
            Assert.All(new ColumnType[] { values.TypeOf<long>(), values.TypeOf<string>(), values.TypeOf<Set<int>>() }, t => Assert.Equal(ColumnKind.Value, t.Kind));
        }

        [Fact]
        public void EveryBuiltInTypeRoundTripsThroughAFunction()
        {
            var graph = new EGraph();
            RoundTrip(graph, 0L, 42L, -42L, long.MaxValue, long.MinValue, 1L << 31);
            RoundTrip(graph, 0, 7, -1, int.MinValue, int.MaxValue);
            RoundTrip(graph, false, true);
            RoundTrip(graph, 0.0, 1.5, -2.25, double.MaxValue, double.PositiveInfinity);
            RoundTrip(graph, "", "kind", "with \"quotes\"", "ünïcödé");
            RoundTrip(graph, Set<string>.Empty, Set.Of("a"), Set.Of("b", "a"));
            RoundTrip(graph, Set.Of(-2L, 1L, 3L), Set<long>.Empty);
            RoundTrip(graph, Set.Of(-1, int.MinValue));
        }

        [Fact]
        public void TheBuiltInTypesFormatAsLiterals()
        {
            var values = new EGraph().Values;
            Assert.Equal("42", values.TypeOf<long>().Format(42L));
            Assert.Equal("-1", values.TypeOf<int>().Format(-1));
            Assert.Equal("true", values.TypeOf<bool>().Format(true));
            Assert.Equal("1.5", values.TypeOf<double>().Format(1.5));
            Assert.Equal("2.0", values.TypeOf<double>().Format(2.0));
            Assert.Equal("\"kind\"", values.TypeOf<string>().Format("kind"));
            Assert.Equal("(set-of \"a\" \"b\")", values.TypeOf<Set<string>>().Format(Set.Of("b", "a")));
            Assert.Equal("(set-of)", values.TypeOf<Set<long>>().Format(Set<long>.Empty));
            Assert.Equal("42", values.TypeOf<long>().Format((object)42L));
            Assert.Throws<ArgumentException>(() => values.TypeOf<long>().Format((object)"42"));
        }

        [Fact]
        public void ContentOrderIsOrdinalNumericAndLexicographic()
        {
            var values = new EGraph().Values;
            Assert.True(values.TypeOf<string>().Compare("B", "a") < 0);
            Assert.True(values.TypeOf<long>().Compare(-5L, 3L) < 0);
            Assert.True(values.TypeOf<int>().Compare(-1, 0) < 0);
            Assert.True(values.TypeOf<bool>().Compare(false, true) < 0);
            Assert.True(values.TypeOf<Set<string>>().Compare(Set.Of("a"), Set.Of("a", "b")) < 0);
            Assert.True(values.TypeOf<Set<string>>().Compare(Set.Of("a", "z"), Set.Of("b")) < 0);
        }

        [Fact]
        public void BothZerosAreOneValueAndNaNIsAValue()
        {
            var graph = new EGraph();
            var type = graph.Values.TypeOf<double>();
            Assert.Equal(type.Encode(0.0), type.Encode(-0.0));
            Assert.True(double.IsPositiveInfinity(1 / type.Decode(type.Encode(-0.0), null)));

            var f = graph.DefineFunction<string, double>("f");
            graph.Root.Assume(f.Of("k").Is(-0.0));
            Assert.True(graph.Root.Check(f.Of("k").Is(0.0)));
            Assert.True(double.IsPositiveInfinity(1 / graph.Root.Get(f.Of("k"))));
            graph.Root.Assume(f.Of("j").Is(double.NaN));
            Assert.False(graph.Root.Check(f.Of("k").Is(double.NaN)));
            Assert.True(graph.Root.TryGet(f.Of("j"), out var nan));
            Assert.True(double.IsNaN(nan));
        }

        [Fact]
        public void IntMinusOneAndMinValueRoundTripAsTheFirstColumnOfARelation()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<int>("r");
            graph.Root.Assume(r.Of(-1), r.Of(int.MinValue), r.Of(0));
            Assert.True(graph.Root.Check(r.Of(-1)));
            Assert.True(graph.Root.Check(r.Of(int.MinValue)));
            Assert.True(graph.Root.Check(r.Of(0)));
            Assert.False(graph.Root.Check(r.Of(1)));

            // Stored as i64: Int32Codec would box -1 to the stale marker.
            var schema = graph.Engine.GetFunctionInfo(r.Function).Schema;
            Assert.Equal(Relations.ColumnTy.Base(graph.BaseValues.GetTypeId<Relations.Int64Codec, long>()), schema[0]);

            var f = graph.DefineFunction<int, int>("f");
            graph.Root.Assume(f.Of(-1).Is(int.MinValue));
            Assert.Equal(int.MinValue, graph.Root.Get(f.Of(-1)));
        }

        [Fact]
        public void EnumsRoundTripAndFormatByName()
        {
            var graph = new EGraph();
            var shade = graph.Values.RegisterEnum<Shade>();
            Assert.Equal("Shade", shade.Name);
            Assert.Same(shade, graph.Values.RegisterEnum<Shade>());
            Assert.Equal("Dark", shade.Format(Shade.Dark));
            Assert.True(shade.Compare(Shade.Dark, Shade.Bright) < 0);

            var tiny = graph.Values.RegisterEnum<Tiny>();
            var f = graph.DefineFunction<Shade, Tiny>("f");
            graph.Root.Assume(f.Of(Shade.Dark).Is(Tiny.High), f.Of(Shade.Bright).Is(Tiny.Low));
            Assert.Equal(Tiny.High, graph.Root.Get(f.Of(Shade.Dark)));
            Assert.Equal(Tiny.Low, graph.Root.Get(f.Of(Shade.Bright)));
            Assert.False(graph.Root.TryGet(f.Of(Shade.Plain), out _));
            Assert.Equal("High", tiny.Format(Tiny.High));
        }

        [Fact]
        public void RegisteredTypesRoundTrip()
        {
            var graph = new EGraph();
            var point = graph.Values.Register<Point>("Point", format: p => $"(point {p.X} {p.Y})");
            Assert.Equal("(point 1 2)", point.Format(new Point(1, 2)));
            Assert.Throws<ArgumentException>(() => graph.Values.Register<Point>("Point2"));
            Assert.Throws<ArgumentException>(() => graph.Values.Register<Guid>("Point"));
            Assert.Throws<ArgumentException>(() => graph.Values.Register<string>("Text"));

            var f = graph.DefineFunction<Point, string>("f");
            graph.Root.Assume(f.Of(new Point(1, 2)).Is("p"));
            Assert.Equal("p", graph.Root.Get(f.Of(new Point(1, 2))));
            Assert.False(graph.Root.TryGet(f.Of(new Point(2, 1)), out _));

            // A set keeps its elements in Comparer<T>.Default order, so its element type must be comparable.
            Assert.Contains("IComparable", Assert.Throws<ArgumentException>(() => graph.Values.RegisterSet<Point>()).Message);
            Assert.False(graph.Values.TryGetType<Set<Point>>(out _));
            graph.Values.Register<Guid>("Guid");
            var set = graph.Values.RegisterSet<Guid>();
            Assert.Equal("Set<Guid>", set.Name);
            Assert.Same(set, graph.Values.RegisterSet<Guid>());
        }

        [Fact]
        public void ClassesAndSortsAreNeverValueTypes()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            Assert.Contains("EGraph.DefineSort", Assert.Throws<ArgumentException>(() => graph.Values.Register<ClassId>("cid")).Message);
            Assert.Contains("EGraph.DefineSort", Assert.Throws<ArgumentException>(() => new EGraph().Values.Register<Node>("Node")).Message);
            Assert.Contains("RegisterSet", Assert.Throws<ArgumentException>(() => graph.Values.Register<Set<long>>("longs")).Message);
            Assert.False(graph.Values.TryGetType<ClassId>(out _));
        }

        [Fact]
        public void ComparingNeverInterns()
        {
            var graph = new EGraph();
            var point = graph.Values.Register<Point>("Point");
            var before = graph.Values.InternedCount;
            Assert.Equal(0, point.Compare(new Point(1, 2), new Point(1, 2)));
            var unseen = point.Compare(new Point(1, 2), new Point(3, 4));
            Assert.Equal(-unseen, point.Compare(new Point(3, 4), new Point(1, 2)));
            Assert.Equal(before, graph.Values.InternedCount);

            var f = graph.DefineFunction<Point, long>("f");
            graph.Root.Assume(f.Of(new Point(5, 5)).Is(1L));
            var interned = graph.Values.InternedCount;
            Assert.True(point.Compare(new Point(5, 5), new Point(1, 2)) < 0);
            Assert.True(point.Compare(new Point(1, 2), new Point(5, 5)) > 0);
            Assert.Equal(interned, graph.Values.InternedCount);
        }

        [Fact]
        public void AnUnknownTypeNamesTheMethodToCall()
        {
            var values = new EGraph().Values;
            Assert.Contains("ValueStore.Register.", Assert.Throws<ArgumentException>(() => values.TypeOf<Point>()).Message);
            Assert.Contains("ValueStore.RegisterSet", Assert.Throws<ArgumentException>(() => values.TypeOf<Set<double>>()).Message);
            Assert.Contains("ValueStore.RegisterEnum", Assert.Throws<ArgumentException>(() => values.TypeOf<Shade>()).Message);
            Assert.Contains("EGraph.DefineSort", Assert.Throws<ArgumentException>(() => values.TypeOf<Node>()).Message);
            Assert.False(values.TryGetType<Point>(out _));
            Assert.Throws<ArgumentException>(() => values.RegisterSet<Point>());
        }

        [Fact]
        public void SetsOfClassesAreEqSortContainers()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var type = graph.Values.RegisterSet<Node>();
            Assert.Equal("Set<Node>", type.Name);
            Assert.True(type.HoldsClasses);
            Assert.Same(type, graph.Values.RegisterSet<Node>());
        }

        [Fact]
        public void ValueSetOperationsKeepOrderAndReturnThisWhenNothingChanges()
        {
            var set = Set.Of("b", "a", "b");
            Assert.Equal(new[] { "a", "b" }, set.ToArray());
            Assert.Equal(2, set.Count);
            Assert.False(set.IsEmpty);
            Assert.True(set.Contains("a"));
            Assert.False(set.Contains("c"));
            Assert.Same(set, set.Add("a"));
            Assert.Equal(new[] { "a", "b", "c" }, set.Add("c").ToArray());
            Assert.Same(set, set.Remove("z"));
            Assert.Equal(new[] { "b" }, set.Remove("a").ToArray());
            Assert.Same(Set<string>.Empty, Set.Of("a").Remove("a"));
            Assert.Same(set, set.Union(Set.Of("a")));
            Assert.Equal(new[] { "a", "b", "c" }, set.Union(Set.Of("c")).ToArray());
            Assert.Same(set, set.Intersect(Set.Of("a", "b", "c")));
            Assert.Equal(new[] { "b" }, set.Intersect(Set.Of("b", "c")).ToArray());
            Assert.Same(set, set.Except(Set.Of("c")));
            Assert.Equal(new[] { "a" }, set.Except(Set.Of("b")).ToArray());
            Assert.Same(set, set.Where(s => s.Length == 1));
            Assert.Equal(new[] { "b" }, set.Where(s => s == "b").ToArray());
            Assert.True(Set.Of("a").IsSubsetOf(set));
            Assert.False(set.IsSubsetOf(Set.Of("a")));
            Assert.True(set.Overlaps(Set.Of("b", "z")));
            Assert.False(set.Overlaps(Set.Of("z")));
            Assert.Equal(Set.Of("a", "b"), set);
            Assert.Equal(Set.Of("a", "b").GetHashCode(), set.GetHashCode());
            Assert.NotEqual(Set.Of("a"), set);
            Assert.Equal("{a, b}", set.ToString());
            Assert.Equal(new[] { "B", "a" }, Set.Of("a", "B").ToArray());
            Assert.Equal(new[] { -2L, 1L, 3L }, Set.Of(3L, -2L, 1L).ToArray());
            Assert.Equal(new[] { 1, 2 }, Set.From(new List<int> { 2, 1, 2 }).ToArray());
            Assert.Throws<ArgumentNullException>(() => Set.Of<string>("a", null!));
        }

        [Fact]
        public void ValueSetOfAStringIsTheSingletonOfThatString()
        {
            var set = Set.Of("abc");
            Assert.Single(set);
            Assert.Equal("abc", set[0]);
        }

        [Fact]
        public void InterningIsThreadSafeAndCanonical()
        {
            var graph = new EGraph();
            var type = graph.Values.TypeOf<string>();
            var before = graph.Values.InternedCount;
            var words = Enumerable.Range(0, 200).Select(i => "word" + i).ToArray();
            var cells = new uint[8][];
            Parallel.For(0, 8, t =>
            {
                cells[t] = new uint[words.Length];
                for (var i = 0; i < words.Length; i++)
                    cells[t][i] = type.Encode(words[(i + t * 17) % words.Length]);
            });

            for (var t = 0; t < 8; t++)
                for (var i = 0; i < words.Length; i++)
                    Assert.Equal(type.Encode(words[(i + t * 17) % words.Length]), cells[t][i]);

            Assert.Equal(words.Length, graph.Values.InternedCount - before);
            Assert.Equal(words.Length, cells.SelectMany(c => c).Distinct().Count());
        }

        [Fact]
        public void ACheckWithAnUnseenStringLeavesTheInternedCountUnchanged()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<string>("r");
            var f = graph.DefineFunction<string, Set<string>>("f");
            graph.Root.Assume(r.Of("seen"));
            var before = graph.Values.InternedCount;

            Assert.False(graph.Root.Check(r.Of("never interned")));
            Assert.False(graph.Root.TryGet(f.Of("also never interned"), out _));
            Assert.False(graph.Root.Check(f.Of("seen").Is(Set.Of("an unseen set"))));
            Assert.True(graph.Root.Check(r.Of("seen")));
            Assert.Equal(before, graph.Values.InternedCount);
        }

        /// <summary>
        /// Writes each value through a function and reads it back.
        /// </summary>
        static void RoundTrip<T>(EGraph graph, params T[] values) where T : notnull
        {
            var type = graph.Values.TypeOf<T>();
            var f = graph.DefineFunction<long, T>("roundtrip-" + type.Name + "-" + graph.Tables.Count);
            for (var i = 0; i < values.Length; i++)
            {
                Assert.Equal(values[i], type.Decode(type.Encode(values[i]), null));
                graph.Root.Assume(f.Of(i).Is(values[i]));
            }

            for (var i = 0; i < values.Length; i++)
            {
                Assert.Equal(values[i], graph.Root.Get(f.Of(i)));
                Assert.True(graph.Root.Check(f.Of(i).Is(values[i])));
            }
        }

        /// <summary>
        /// <c>ValueStore.TypeOf</c> of an unknown type throws an <see cref="ArgumentException"/> that names no
        /// parameter, or the method's own type parameter.
        /// </summary>
        [Fact]
        public void TypeOfNamesNoForeignParameter()
        {
            var graph = new EGraph();
            var e = Assert.Throws<ArgumentException>(() => graph.Values.TypeOf<Point>());
            Assert.True(e.ParamName is null || e.ParamName == "T", $"ParamName is '{e.ParamName}'");
        }

        /// <summary>
        /// <c>default(ClassId)</c> names no class and every scope rejects it, and class values never enter value cells:
        /// classes compare only with <c>Is</c> and <c>IsNot</c>.
        /// <c>ValueStore.Register</c> refuses <see cref="ClassId"/>, and a sort, as a value type.
        /// </summary>
        [Fact]
        public void RegisterRefusesClassIdAndSortsAsValueTypes()
        {
            var g = new EGraph();
            Assert.Throws<ArgumentException>(() => g.Values.Register<ClassId>("cid"));
            Assert.Throws<ArgumentException>(() => g.Values.Register<Node>("node"));
        }

        /// <summary>
        /// A host write never stores <c>default(ClassId)</c>: <c>Register</c> refuses <see cref="ClassId"/> as a value
        /// type, and were it accepted, the write of a <c>default(ClassId)</c> value would be refused.
        /// </summary>
        [Fact]
        public void ADefaultClassIdNeverReachesATable()
        {
            var g = new EGraph();
            try
            {
                g.Values.Register<ClassId>("cid");
            }
            catch (ArgumentException)
            {
                return;
            }

            var f = g.DefineFunction<string, ClassId>("f");
            Assert.Throws<ArgumentException>(() => g.Root.Assume(f.Of("k").Is(default(ClassId))));
        }

        /// <summary>
        /// A <see cref="Set{T}"/> keeps its elements in <c>Comparer&lt;T&gt;.Default</c> order: <c>RegisterSet</c>
        /// either refuses an element type with no default order, or registers a set type whose sets hold two elements.
        /// </summary>
        [Fact]
        public void RegisterSetRefusesAnElementTypeWithNoOrder()
        {
            var g = new EGraph();
            g.Values.Register<Opaque>("Opaque", Comparer<Opaque>.Create((x, y) => x.X.CompareTo(y.X)));
            var registered = Record.Exception(() => g.Values.RegisterSet<Opaque>());
            if (registered is ArgumentException)
                return;

            Assert.Null(registered);
            var two = Record.Exception(() => Set.Of(new Opaque(1), new Opaque(2)));
            Assert.Null(two);
        }

        /// <summary>
        /// Values used only for reading never intern: <c>ColumnType&lt;T&gt;.Compare</c> is a read, and does not grow
        /// the graph's pools.
        /// </summary>
        [Fact]
        public void CompareDoesNotIntern()
        {
            var g = new EGraph();
            var type = g.Values.Register<Opaque>("Opaque");
            var before = g.Values.InternedCount;
            type.Compare(new Opaque(1), new Opaque(2));
            Assert.Equal(before, g.Values.InternedCount);
        }

    }

}
