using System;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests that a child context's database shares the store-wide registries of the root's by reference: the counters,
    /// external functions, dependency graph, container environments and base value registry, so that a base value boxed
    /// at the root unboxes in a child's execution state and the other way round, at every depth and in concurrent
    /// children.
    /// </summary>
    public class ChildSharedRegistriesTests
    {

        /// <summary>
        /// A store with a value-only lattice table <c>f(k) → v</c>, whose values are base values.
        /// </summary>
        static (Store Store, TableId F) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(LayeredTestUtil.ValueOnly(), name: "f");
            return (store, f);
        }

        /// <summary>
        /// Reads <c>f(key)</c> in a write batch of <paramref name="context"/> through its execution state (the
        /// write-phase lookup a merge function makes), and unboxes it there.
        /// </summary>
        static string ReadString(Context context, TableId f, uint key) =>
            context.Write(state =>
            {
                var value = state.PredictColumn(f, [key], [MergeVal.FromConstant(default)], new ColumnId(1));
                return state.BaseValues.Unwrap<StringCodec, string>(new Value(value));
            });

        /// <summary>
        /// A child's and a grandchild's databases, states and cloned states hold the root's registries themselves.
        /// </summary>
        [Fact]
        public void AChildSharesEveryStoreWideRegistryOfTheRoot()
        {
            var (store, _) = Setup();
            var root = store.Root.Database;
            var child = store.Root.CreateChild();
            var grandchild = child.CreateChild();
            foreach (var db in new[] { child.Database, grandchild.Database })
            {
                Assert.Same(root.BaseValues, db.BaseValues);
                Assert.Same(root.BaseValues, db.CreateExecutionState().BaseValues);
                Assert.Same(root.BaseValues, db.CreateExecutionState().Clone().BaseValues);
                Assert.Same(root.Counters, db.Counters);
                Assert.Same(root.ExternalFunctions, db.ExternalFunctions);
                Assert.Same(root.Dependencies, db.Dependencies);
                Assert.Same(root.Containers, db.Containers);
            }

            grandchild.Dispose();
            child.Dispose();
        }

        /// <summary>
        /// Strings boxed and staged at the root unbox in a child's and a grandchild's execution states, and the lookup
        /// creates no layer.
        /// </summary>
        [Fact]
        public void ABaseValueStagedAtTheRootIsReadFromAChildsExecutionState()
        {
            var (store, f) = Setup();

            // Box and stage at the root, in a root batch.
            store.Root.Write(state =>
            {
                state.BaseValues.RegisterType<StringCodec, string>();
                state.StageInsert(f, [1, state.BaseValues.Get<StringCodec, string>("hello").Rep]);
                state.StageInsert(f, [2, state.BaseValues.Get<StringCodec, string>("world").Rep]);
                return 0;
            });

            var child = store.Root.CreateChild();
            Assert.Equal("hello", ReadString(child, f, 1));
            Assert.Equal("world", ReadString(child, f, 2));

            // A hitting lookup created no layer.
            Assert.Null(child.Tables!.TryGetLayer(f));

            // Deeper in the chain.
            var grandchild = child.CreateChild();
            Assert.Equal("hello", ReadString(grandchild, f, 1));
            grandchild.Dispose();
            child.Dispose();
        }

        /// <summary>
        /// A base value type registered and a value boxed in a child have the same id and meaning at the root.
        /// </summary>
        [Fact]
        public void BaseValuesBoxedInAChildMeanTheSameAtTheRoot()
        {
            var (store, f) = Setup();

            // No type is registered before the child exists; the child registers it.
            var child = store.Root.CreateChild();
            var id = child.Write(state =>
            {
                var type = state.BaseValues.RegisterType<Int64Codec, long>();
                var boxed = state.BaseValues.Get<Int64Codec, long>(long.MaxValue - 7);
                state.StageInsert(f, [9, boxed.Rep]);
                return type;
            });

            Assert.Equal(id, store.Root.Database.BaseValues.GetTypeId<Int64Codec, long>());
            var row = LayeredTestUtil.Row(child, f, 9)!;
            Assert.Equal(long.MaxValue - 7, store.Root.Database.BaseValues.Unwrap<Int64Codec, long>(new Value(row[1])));

            // A string interned in the child is the root's interned value too.
            var hello = child.Write(state =>
            {
                state.BaseValues.RegisterType<StringCodec, string>();
                return state.BaseValues.Get<StringCodec, string>("from the child");
            });

            Assert.Equal(hello, store.Root.Database.BaseValues.Get<StringCodec, string>("from the child"));
            child.Dispose();
        }

        /// <summary>
        /// Eight children of a frozen root, on parallel threads, read the root's value and intern strings; equal
        /// strings are one value everywhere.
        /// </summary>
        [Fact]
        public void ConcurrentChildrenOfAFrozenRootShareTheRegistry()
        {
            var (store, f) = Setup();
            store.Root.Write(state =>
            {
                state.BaseValues.RegisterType<StringCodec, string>();
                state.StageInsert(f, [1, state.BaseValues.Get<StringCodec, string>("shared").Rep]);
                return 0;
            });

            store.Root.Freeze();
            var children = Enumerable.Range(0, 8).Select(_ => store.Root.CreateChild()).ToArray();
            var boxed = new Value[children.Length];
            Parallel.For(0, children.Length, i =>
            {
                Assert.Equal("shared", ReadString(children[i], f, 1));
                boxed[i] = children[i].Write(state => state.BaseValues.Get<StringCodec, string>($"child {i % 4}"));
            });

            // Equal strings interned by different children, concurrently, are one value everywhere.
            for (var i = 0; i < children.Length; i++)
            {
                Assert.Equal($"child {i % 4}", store.Root.Database.BaseValues.Unwrap<StringCodec, string>(boxed[i]));
                Assert.Equal(boxed[i % 4], boxed[i]);
                children[i].Dispose();
            }
        }

    }

}
