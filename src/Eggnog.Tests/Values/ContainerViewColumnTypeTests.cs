using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Containers of classes read through a scope's container view: with root containers of each container type placed
    /// in a child's layers under other root ids (the engine's test hook, <c>ContainerLayers.Place</c>), as a child's
    /// rebuild could leave them (<see cref="ContainerViewTheory"/>), every way the library reads a container by its cell
    /// (decoding, formatting, comparing, its elements and content, the class walks, a content-sorted read, the cell
    /// primitives, the higher-order bodies and extraction) reads the copy in that child and the root's container at the
    /// root and in a sibling.
    /// </summary>
    public sealed class ContainerViewColumnTypeTests
    {

        /// <summary>
        /// Asserts that every column-type member that reads a container by its cell reads <paramref name="function"/>'s
        /// container, under <paramref name="cell"/>, and its twin, under <paramref name="twin"/>, through the scope's view:
        /// at the root and in the plain sibling as the root reads them, and in the copy, which swaps them, as the root reads
        /// the other.
        /// </summary>
        /// <param name="t">The fixture.</param>
        /// <param name="function">The function whose value is the container.</param>
        /// <param name="cell">The container's root cell.</param>
        /// <param name="twin">The twin's root cell.</param>
        /// <param name="elementCells">The type's own list of a container's element cells, if it has one.</param>
        static void AssertReadsThroughTheView<T>(ContainerViewTheory t, Function<T> function, uint cell, uint twin,
            Func<uint, Relations.ContainerView, uint[]>? elementCells)
        {
            var type = t.Graph.Values.TypeOf<T>();
            var container = (IContainerColumnType)type;
            var root = t.Root.ContainerView;
            var original = t.Root.Get(function.Of());
            var copied = type.Decode(twin, t.Root);

            // The two contents read apart at the root, so that every comparison below tells them apart.
            Assert.NotEqual(type.FormatCell(cell, root), type.FormatCell(twin, root));
            Assert.NotEqual(0, type.CompareCells(cell, twin, root));

            foreach (var (scope, copy) in new[] { (t.Root, false), (t.Plain, false), (t.Copy, true) })
            {
                // The root's cells of what the scope reads under the container's cell and under the twin's.
                var view = scope.ContainerView;
                var (seen, other) = copy ? (twin, cell) : (cell, twin);

                // Decode, through a read of the function: the value is the content the scope reads under the cell.
                Assert.True(type.TryEncodeExisting(scope.Get(function.Of()), root, out var decoded));
                Assert.Equal(seen, decoded);

                Assert.Equal(type.FormatCell(seen, root), type.FormatCell(cell, view));
                Assert.Equal(type.FormatCell(other, root), type.FormatCell(twin, view));

                // A cell no container of the type has, a class's, formats as a class's does.
                var stray = t.One.Id.Cell;
                Assert.Equal(FormattableString.Invariant($"#{stray}"), type.FormatCell(stray, view));
                Assert.Equal(type.CompareCells(seen, other, root), type.CompareCells(cell, twin, view));
                Assert.Equal(container.Content(seen, root), container.Content(cell, view));
                Assert.Equal(Elements(container, seen, root, distinct: false), Elements(container, cell, view, distinct: false));
                Assert.Equal(Elements(container, seen, root, distinct: true), Elements(container, cell, view, distinct: true));
                Assert.Equal(Classes(type, seen, root), Classes(type, cell, view));
                Assert.Equal(ContainerClassCells.CanonicalContent(type, seen, root, c => c), ContainerClassCells.CanonicalContent(type, cell, view, c => c));
                Assert.Equal(container.SerializedName(seen, root), container.SerializedName(cell, view));
                if (elementCells is not null)
                    Assert.Equal(elementCells(seen, root), elementCells(cell, view));

                // By content: the scope finds each content under the cell it reads it under.
                Assert.True(type.TryEncodeExisting(original, view, out var found));
                Assert.Equal(copy ? twin : cell, found);
                Assert.True(type.TryEncodeExisting(copied, view, out found));
                Assert.Equal(copy ? cell : twin, found);
            }
        }

        /// <summary>
        /// Gets the element cells of the container in <paramref name="cell"/>, read through <paramref name="view"/>.
        /// </summary>
        static List<uint> Elements(IContainerColumnType type, uint cell, Relations.ContainerView view, bool distinct)
        {
            var elements = new List<(ColumnType Type, uint Cell)>();
            if (distinct)
                type.AppendDistinctElements(cell, view, elements);
            else
                type.AppendElements(cell, view, elements);

            return elements.ConvertAll(e => e.Cell);
        }

        /// <summary>
        /// Gets the class cells the container in <paramref name="cell"/> names, read through <paramref name="view"/>.
        /// </summary>
        static List<uint> Classes(ColumnType type, uint cell, Relations.ContainerView view)
        {
            var classes = new List<uint>();
            ContainerClassCells.AppendClasses(type, cell, view, classes);
            return classes;
        }

        [Theory]
        [InlineData("vec")]
        [InlineData("set")]
        [InlineData("multiset")]
        [InlineData("map")]
        [InlineData("pair")]
        [InlineData("fn")]
        public void EachContainerTypeReadsItsCellsThroughTheScopesView(string kind)
        {
            var t = new ContainerViewTheory();
            switch (kind)
            {
                case "vec":
                    var vecs = (VecColumnType<Node>)t.Graph.Values.TypeOf<Vec<Node>>();
                    AssertReadsThroughTheView(t, t.Vector, t.X, t.TwinX, vecs.ElementCells);

                    // A constant read in the scope finds the vector it sees.
                    Assert.True(t.Root.Check(t.Vector.Of().Is(Primitives.VecOf<Node>().Of(t.Num.Of(1L)))));
                    Assert.True(t.Plain.Check(t.Vector.Of().Is(Primitives.VecOf<Node>().Of(t.Num.Of(1L)))));
                    Assert.True(t.Copy.Check(t.Vector.Of().Is(Primitives.VecOf<Node>().Of(t.Num.Of(2L)))));
                    Assert.False(t.Copy.Check(t.Vector.Of().Is(Primitives.VecOf<Node>().Of(t.Num.Of(1L)))));
                    break;
                case "set":
                    AssertReadsThroughTheView(t, t.Set, t.S, t.TwinS, null);
                    break;
                case "multiset":
                    var multisets = (MultiSetColumnType<Node>)t.Graph.Values.TypeOf<MultiSet<Node>>();
                    AssertReadsThroughTheView(t, t.Bag, t.M, t.TwinM, multisets.ElementCells);
                    break;
                case "map":
                    AssertReadsThroughTheView(t, t.Dict, t.D, t.TwinD, null);
                    break;
                case "pair":
                    AssertReadsThroughTheView(t, t.Couple, t.P, t.TwinP, null);
                    break;
                default:
                    AssertReadsThroughTheView(t, t.Fn, t.F, t.TwinF, null);
                    break;
            }

            Assert.False(t.Copy.IsFaulted);
        }

        [Fact]
        public void AContentSortedReadOrdersBySetsTheScopeSees()
        {
            // Rank's keys, (set-of (Num 1)) and (set-of (Num 1) (Num 2)): a prefix first at the root, and in the copy,
            // where the first is (set-of (Num 2)), last.
            var t = new ContainerViewTheory();
            Assert.Equal(new object?[] { 1L, 2L }, t.Root.Rows(t.Rank).Select(row => row.Output));
            Assert.Equal(new object?[] { 1L, 2L }, t.Plain.Rows(t.Rank).Select(row => row.Output));
            Assert.Equal(new object?[] { 2L, 1L }, t.Copy.Rows(t.Rank).Select(row => row.Output));
            Assert.Equal(1, ((Set<Node>)t.Copy.Rows(t.Rank)[1].Keys[0]).Count);
        }

        [Fact]
        public void ExtractionReadsTheContainersTheScopeHolds()
        {
            var t = new ContainerViewTheory();
            Assert.Equal("(L (vec-of (Num 1)))", t.Root.Extract(t.Term).ToString());
            Assert.Equal("(L (vec-of (Num 1)))", t.Plain.Extract(t.Term).ToString());
            Assert.Equal("(L (vec-of (Num 2)))", t.Copy.Extract(t.Term).ToString());

            // The extracted argument decodes through the scope too.
            Assert.Equal(t.Two.Id.Cell, t.Copy.Extract(t.Term).Value<Vec<Node>>(0)[0].Id.Cell);
        }

        [Fact]
        public void TheCellPrimitivesAndTheHigherOrderBodiesReadTheScopesContainersInARule()
        {
            var t = new ContainerViewTheory();
            t.Plain.Run(t.Rules);
            t.Copy.Run(t.Rules);

            // multiset-pick, in a head and in a body, and multiset-pick-max, over the cell.
            Assert.True(t.Plain.Check(t.Picked.Of().Is(t.Num.Of(1L))));
            Assert.True(t.Copy.Check(t.Picked.Of().Is(t.Num.Of(2L))));
            Assert.True(t.Plain.Check(t.PickedInBody.Of().Is(t.Num.Of(1L))));
            Assert.True(t.Copy.Check(t.PickedInBody.Of().Is(t.Num.Of(2L))));
            Assert.True(t.Plain.Check(t.PickedMax.Of().Is(t.Num.Of(1L))));
            Assert.True(t.Copy.Check(t.PickedMax.Of().Is(t.Num.Of(3L))));

            // multiset-count and multiset-contains, set-get and set-contains, map-get.
            Assert.Equal(1L, t.Plain.Get(t.Counted.Of()));
            Assert.Equal(3L, t.Copy.Get(t.Counted.Of()));
            Assert.False(t.Plain.TryGet(t.Flags.Of(0L), out _));
            Assert.Equal(1L, t.Copy.Get(t.Flags.Of(0L)));
            Assert.True(t.Plain.Check(t.Gotten.Of().Is(t.Num.Of(1L))));
            Assert.True(t.Copy.Check(t.Gotten.Of().Is(t.Num.Of(2L))));
            Assert.False(t.Plain.TryGet(t.Flags.Of(1L), out _));
            Assert.Equal(1L, t.Copy.Get(t.Flags.Of(1L)));
            Assert.False(t.Plain.TryGet(t.Mapped.Of(), out _));
            Assert.Equal(20L, t.Copy.Get(t.Mapped.Of()));

            // unstable-multiset-map, -filter, -flat-map and -reduce over the multiset's cell, unstable-vec-map over the
            // vector's, and unstable-app over the function value's.
            Assert.Equal(MultiSet.Of(1L, 1L, 3L), t.Plain.Get(t.Weights.Of()));
            Assert.Equal(MultiSet.Of(2L, 3L, 3L, 3L), t.Copy.Get(t.Weights.Of()));
            Assert.True(t.Plain.Check(t.Filtered.Of().Is(t.Empty.Of())));
            Assert.True(t.Copy.Check(t.Filtered.Of().Is(t.Even.Of())));
            Assert.True(t.Plain.Check(t.FlatMapped.Of().Is(t.Bag.Of())));
            Assert.True(t.Copy.Check(t.FlatMapped.Of().Is(t.Flat.Of())));
            Assert.True(t.Plain.Check(t.Reduced.Of().Is(t.Add.Of(t.Add.Of(t.Num.Of(1L), t.Num.Of(1L)), t.Num.Of(3L)))));
            Assert.True(t.Copy.Check(t.Reduced.Of().Is(t.Add.Of(t.Add.Of(t.Add.Of(t.Num.Of(2L), t.Num.Of(3L)), t.Num.Of(3L)), t.Num.Of(3L)))));
            Assert.Equal(Vec.Of(1L), t.Plain.Get(t.VectorWeights.Of()));
            Assert.Equal(Vec.Of(2L), t.Copy.Get(t.VectorWeights.Of()));
            Assert.Equal(5L, t.Plain.Get(t.Applied.Of()));
            Assert.Equal(7L, t.Copy.Get(t.Applied.Of()));

            Assert.False(t.Plain.IsFaulted);
            Assert.False(t.Copy.IsFaulted);
        }

        [Fact]
        public void AHostReadComputesOverTheScopesContainers()
        {
            var t = new ContainerViewTheory();
            var bag = Primitives.MultiSetOf<Node>();
            foreach (var (scope, copy) in new[] { (t.Root, false), (t.Plain, false), (t.Copy, true) })
            {
                // The values a read decodes, and the elements' cells the value forms of the cell primitives order by.
                Assert.True(scope.AreEqual(scope.Get(Primitives.MultiSetPick<Node>().Of(t.Bag.Of())), copy ? t.Two : t.One));
                Assert.True(scope.AreEqual(scope.Get(Primitives.MultiSetPickMax<Node>().Of(t.Bag.Of())), copy ? t.Three : t.One));
                Assert.Equal(copy ? 3L : 1L, scope.Get(Primitives.MultiSetCount<Node>().Of(t.Bag.Of(), t.Num.Of(3L))));
                Assert.True(scope.AreEqual(scope.Get(Primitives.Get<Node>().Of(t.Set.Of(), 0L)), copy ? t.Two : t.One));
                Assert.Equal(copy, scope.Check(Primitives.MapContains<Node, long>().Of(t.Dict.Of(), t.Num.Of(2L)).Is(true)));
                Assert.Equal(copy ? 7L : 5L, scope.Get(Primitives.UnstableApp<long, long>().Of(t.Fn.Of(), 1L)));
                Assert.True(scope.Check(Primitives.UnstableMultiSetFilter<Node>().Of(Primitives.UnstableFn<Node, ValueTuple>(t.IsEven).Of(), t.Bag.Of())
                    .Is(copy ? bag.Of(t.Num.Of(2L)) : bag.Of())));

                // A read folds over the elements in cell order, a function's rows read where reads are: the root's
                // (Num 1) (Num 1) (Num 3) folds to (Num 3), the copy's (Num 2) (Num 3) (Num 3) (Num 3) to (Num 2).
                Assert.True(scope.AreEqual(scope.Get(Primitives.UnstableMultiSetReduce<Node>().Of(Primitives.UnstableFn<Node, Node, Node>(t.Fold).Of(), t.Num.Of(1L), t.Bag.Of())),
                    copy ? t.Two : t.Three));
                Assert.Equal(copy ? Vec.Of(2L) : Vec.Of(1L), scope.Get(Primitives.UnstableVecMap<Node, long>().Of(Primitives.UnstableFn<Node, long>(t.Weight).Of(), t.Vector.Of())));
                var argument = (Node)((IFnValue)scope.Get(t.Fn.Of())).Content!.ArgumentArray[0];
                Assert.Equal((copy ? t.Two : t.One).Id.Cell, argument.Id.Cell);
            }
        }

        [Fact]
        public void SiblingsReadVectorsOfClassesWhileAnotherInternsVectorsOfValues()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<long>();
            var vector = graph.DefineFunction<Vec<Node>>("vector");
            var pairs = graph.DefineFunction<long, Vec<long>>("pairs");
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            root.Assume(vector.Of().Is(Primitives.VecOf<Node>().Of(num.Of(1L), num.Of(2L))));
            var vecs = (VecColumnType<Node>)graph.Values.TypeOf<Vec<Node>>();
            Assert.True(vecs.TryEncodeExisting(Vec.Of(one, two), root.ContainerView, out var x));
            root.Freeze();

            // One sibling holds the vector reversed, the others hold nothing; a fourth writes vectors of values, which its
            // host writes intern into the same root environment, growing its shards, while the readers read through
            // their views.
            var readers = new Scope[3];
            for (var i = 0; i < readers.Length; i++)
                readers[i] = root.CreateChild();

            var env = graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>();
            readers[0].Engine.Context.Context.ContainerLayers!.Place(env, new Relations.VecContainer(true, [two.Id.Cell, one.Id.Cell]), x);
            using var writer = root.CreateChild();
            var longs = graph.Values.TypeOf<Vec<long>>();
            const long Writes = 20_000;

            var done = 0;
            var tasks = new List<Task>
            {
                Task.Run(() =>
                {
                    try
                    {
                        for (var i = 0L; i < Writes; i++)
                            writer.Assume(pairs.Of(i).Is(Vec.Of(i, i + 1)));
                    }
                    finally
                    {
                        Volatile.Write(ref done, 1);
                    }
                }),
            };

            for (var i = 0; i < readers.Length; i++)
            {
                var reader = readers[i];
                var expected = i == 0 ? new[] { two.Id.Cell, one.Id.Cell } : new[] { one.Id.Cell, two.Id.Cell };
                tasks.Add(Task.Run(() =>
                {
                    var view = reader.ContainerView;
                    var reads = 0;
                    while (Volatile.Read(ref done) == 0 || reads < 100)
                    {
                        Assert.Equal(expected, vecs.ElementCells(x, view));
                        if (reads++ % 16 == 0)
                        {
                            var read = reader.Get(vector.Of());
                            Assert.Equal(expected[0], read[0].Id.Cell);
                            Assert.Equal(expected[1], read[1].Id.Cell);
                        }
                    }
                }));
            }

            Assert.True(Task.WaitAll(tasks.ToArray(), TimeSpan.FromMinutes(2)));
            Assert.Equal(Vec.Of(Writes - 1, Writes), writer.Get(pairs.Of(Writes - 1)));
            Assert.True(longs.TryEncodeExisting(Vec.Of(Writes - 1, Writes), root.ContainerView, out _));
            Assert.False(writer.IsFaulted);
            foreach (var reader in readers)
            {
                Assert.False(reader.IsFaulted);
                reader.Dispose();
            }
        }

    }

}
