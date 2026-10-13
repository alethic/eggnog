using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="TableAction"/>, <see cref="UnionAction"/> and <see cref="ActionRegistry"/>
    /// (egglog-bridge/src/lib.rs:44-96, 1248-1395): the handles merge functions and primitives use.
    /// </summary>
    public class TableActionTests
    {

        static TableAction Action(EGraph egraph, string name)
        {
            Assert.True(egraph.ActionRegistry.TryGetTable(name, out var action));
            return action!;
        }

        [Fact]
        public void LookupIsAPureReadOfTheCommittedTable()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var a = egraph.FreshId();
            var fa = egraph.AddTerm(f, [a]);
            var action = Action(egraph, "f");

            egraph.WithExecutionState(state =>
            {
                Assert.True(action.TryLookup(state, [a.Rep], out var value));
                Assert.Equal(fa.Rep, value);
                Assert.False(action.TryLookup(state, [a.Rep + 100], out _));
                Assert.Equal(1, action.RowCount(state));
                return 0;
            });

            // A lookup never inserts, whatever the default.
            Assert.False(egraph.FlushUpdates());
            Assert.Equal(1, egraph.TableSize(f));
        }

        [Fact]
        public void LookupOrInsertMintsOncePerStateForAConstructor()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", true, ColumnTy.Id);
            var a = egraph.FreshId();
            var action = Action(egraph, "f");

            var (first, second) = egraph.WithExecutionState(state =>
            {
                Assert.True(action.TryLookupOrInsert(state, [a.Rep], out var x));
                Assert.True(action.TryLookupOrInsert(state, [a.Rep], out var y));
                return (x, y);
            });
            Assert.Equal(first, second);
            Assert.Equal(1u, first);

            // The predicted row is [key, fresh, ts, not subsumed] and lands at the flush.
            Assert.Equal(0, egraph.TableSize(f));
            Assert.True(egraph.FlushUpdates());
            Assert.Equal([a.Rep, 1u, 1u, SchemaMath.NotSubsumed], PhysicalRow(egraph, f, a.Rep));

            // A committed key is read, not minted again.
            var third = egraph.WithExecutionState(state => action.TryLookupOrInsert(state, [a.Rep], out var z) ? z : uint.MaxValue);
            Assert.Equal(1u, third);
            Assert.Equal(2, egraph.Database.ReadCounter(egraph.IdCounter));
        }

        [Fact]
        public void LookupOrInsertUsesAConstantDefault()
        {
            var egraph = NewEGraph();
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Const(new Value(77)),
                Merge = MergeFn.AssertEq,
                Name = "f",
            });
            var action = Action(egraph, "f");
            Assert.True(action.HasDefault);
            var value = egraph.WithExecutionState(state => action.TryLookupOrInsert(state, [5u], out var v) ? v : 0u);
            Assert.Equal(77u, value);
            egraph.FlushUpdates();
            Assert.Equal(new Value(77), egraph.LookupId(f, [new Value(5)]));
            Assert.Equal(0, egraph.Database.ReadCounter(egraph.IdCounter));
        }

        [Fact]
        public void LookupOrInsertWithoutADefaultOnlyReads()
        {
            var egraph = NewEGraph();
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "f",
            });
            var action = Action(egraph, "f");
            Assert.False(action.HasDefault);
            Assert.False(egraph.WithExecutionState(state => action.TryLookupOrInsert(state, [5u], out _)));
            Assert.False(egraph.FlushUpdates());
            Assert.Equal(0, egraph.TableSize(f));
        }

        [Fact]
        public void InsertAndRemoveAreStaged()
        {
            var egraph = NewEGraph();
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "f",
                CanSubsume = true,
            });
            var action = Action(egraph, "f");
            egraph.WithExecutionState(state =>
            {
                action.Insert(state, [1u, 2u, 3u]);
                action.Insert(state, [4u, 5u, 6u]);
                Assert.Throws<ArgumentException>(() => action.Insert(state, [1u, 2u]));
                return 0;
            });
            Assert.Equal(0, egraph.TableSize(f));
            egraph.FlushUpdates();
            Assert.Equal(["1 2 3", "4 5 6"], Show(egraph, f));
            Assert.Equal([1u, 2u, 3u, 1u, SchemaMath.NotSubsumed], PhysicalRow(egraph, f, 1u, 2u));

            egraph.WithExecutionState(state =>
            {
                action.Remove(state, [1u, 2u]);
                return 0;
            });
            egraph.FlushUpdates();
            Assert.Equal(["4 5 6"], Show(egraph, f));
        }

        [Fact]
        public void SubsumeMarksTheCommittedRow()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", true, ColumnTy.Id);
            var a = egraph.FreshId();
            var fa = egraph.AddTerm(f, [a]);
            var action = Action(egraph, "f");
            egraph.WithExecutionState(state =>
            {
                action.Subsume(state, [a.Rep]);
                return 0;
            });
            egraph.FlushUpdates();
            Assert.Equal([$"{a.Rep} {fa.Rep} (s)"], Show(egraph, f));
            Assert.Equal([a.Rep, fa.Rep, 2u, SchemaMath.Subsumed], PhysicalRow(egraph, f, a.Rep));

            // egglog panics on a missing key, and on a function without subsumption.
            Assert.Throws<InvalidOperationException>(() => egraph.WithExecutionState(state =>
            {
                action.Subsume(state, [a.Rep + 50]);
                return 0;
            }));
            var g = Constructor(egraph, "g", false, ColumnTy.Id);
            egraph.AddTerm(g, [a]);
            Assert.Throws<InvalidOperationException>(() => egraph.WithExecutionState(state =>
            {
                Action(egraph, "g").Subsume(state, [a.Rep]);
                return 0;
            }));
        }

        [Fact]
        public void UnionActionStagesAUnionWithTheCurrentTimestamp()
        {
            var egraph = NewEGraph();
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var union = new UnionAction(egraph);
            Assert.Equal(egraph.ActionRegistry.UnionAction, union);

            egraph.WithExecutionState(state =>
            {
                union.Union(state, b.Rep, a.Rep);
                return 0;
            });
            Assert.Equal(0, egraph.UnionCount);
            egraph.FlushUpdates();
            Assert.Equal(1, egraph.UnionCount);
            Assert.Equal(a, egraph.GetCanonRepr(b, ColumnTy.Id));

            // The union-find row records the displaced id, its canonical id and the timestamp.
            var row = egraph.Database.GetTable(egraph.UnionFindTable).GetRow([b.Rep]);
            Assert.NotNull(row);
            Assert.Equal([b.Rep, a.Rep, 1u], row.Value.Values);
        }

        [Fact]
        public void RegistryListsTableSizes()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            Constructor(egraph, "g", false, ColumnTy.Id);
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            egraph.AddTerm(f, [a]);
            egraph.AddTerm(f, [b]);

            var sizes = egraph.WithExecutionState(state => egraph.ActionRegistry.TableSizes(state));
            Assert.Equal([("f", 2), ("g", 0)], sizes.Select(p => (p.Key, p.Value)).OrderBy(p => p.Key).ToArray());
        }

        [Fact]
        public void RegisteringANameAgainReplacesItsHandle()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var g = Constructor(egraph, "f", false, ColumnTy.Id, ColumnTy.Id);
            Assert.Equal(1, egraph.ActionRegistry.Count);
            Assert.Equal(egraph.GetTable(g), Action(egraph, "f").Table);
            Assert.NotEqual(egraph.GetTable(f), Action(egraph, "f").Table);
        }

    }

}
