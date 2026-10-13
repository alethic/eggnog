using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the construction of an <see cref="EGraph"/> and the declaration of functions (egglog-bridge/src/lib.rs:
    /// 140-189, 506-573): the union-find table, the counters, the default panic, the table layout of a function and its
    /// merge dependencies.
    /// </summary>
    public class EGraphTests
    {

        [Fact]
        public void NewEGraphHasTheUnionFindCountersAndDefaultPanic()
        {
            var egraph = NewEGraph();
            var db = egraph.Database;

            Assert.Equal(new TableId(0), egraph.UnionFindTable);
            Assert.IsType<DisplacedTable>(db.GetTable(egraph.UnionFindTable));
            Assert.Equal(EGraph.UnionFindName, db.GetTableInfo(egraph.UnionFindTable).Name);
            Assert.Equal(0, db.ReadCounter(egraph.IdCounter));
            Assert.Equal(1, db.ReadCounter(egraph.TimestampCounter));
            Assert.Equal(1u, egraph.NextTimestamp);
            Assert.Equal(0, egraph.FunctionCount);

            // The default panic is the first external function, and is interned under its message.
            Assert.Equal(new ExternalFunctionId(0), egraph.ActionRegistry.DefaultPanicId);
            Assert.Equal(egraph.ActionRegistry.DefaultPanicId, egraph.NewPanic(EGraph.DefaultPanicMessage));
            Assert.Equal(egraph.UnionFindTable, egraph.ActionRegistry.UnionAction.UnionFind);
            Assert.Equal(egraph.TimestampCounter, egraph.ActionRegistry.UnionAction.Timestamp);
        }

        [Fact]
        public void ThePublicConstructorUsesTheDefaultParallelism()
        {
            var egraph = new EGraph();
            Assert.Same(ParallelHeuristics.Default, egraph.Database.Parallelism);
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var a = egraph.FreshId();
            Assert.Equal(egraph.AddTerm(f, [a]), egraph.AddTerm(f, [a]));
        }

        [Fact]
        public void FreshIdsCountFromZero()
        {
            var egraph = NewEGraph();
            Assert.Equal(new Value(0), egraph.FreshId());
            Assert.Equal(new Value(1), egraph.FreshId());
            Assert.Equal(2, egraph.Database.ReadCounter(egraph.IdCounter));
        }

        [Fact]
        public void AddTableLaysOutTheTableAsEgglogDoes()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, i64, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = "f",
                CanSubsume = true,
            });
            var g = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, i64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "g",
            });

            Assert.Equal(new FunctionId(0), f);
            Assert.Equal(new FunctionId(1), g);
            Assert.Equal(2, egraph.FunctionCount);
            Assert.Equal("f", egraph.GetFunctionName(f));

            var fInfo = egraph.GetFunctionInfo(f);
            Assert.Equal(new TableId(1), fInfo.Table);
            Assert.Equal(new SchemaMath(3, true), fInfo.Math);
            Assert.Equal(ColumnTy.Id, fInfo.OutputType);
            var fTable = Assert.IsType<SortedWritesTable>(egraph.Database.GetTable(fInfo.Table));
            Assert.Equal(2, fTable.KeyCount);
            Assert.Equal(5, fTable.Arity);
            Assert.Equal(3, fTable.SortColumn);
            Assert.Equal("f", egraph.Database.GetTableInfo(fInfo.Table).Name);
            var fMerge = Assert.IsType<FunctionTableMerge>(fTable.MergeFunction);
            Assert.True(fMerge.IsLattice);
            Assert.Equal(MergeFnKind.UnionId, fMerge.Output.Kind);

            var gInfo = egraph.GetFunctionInfo(g);
            var gTable = Assert.IsType<SortedWritesTable>(egraph.Database.GetTable(gInfo.Table));
            Assert.Equal(1, gTable.KeyCount);
            Assert.Equal(3, gTable.Arity);
            Assert.Equal(2, gTable.SortColumn);
            Assert.Equal(MergeFnKind.AssertEq, ((FunctionTableMerge)gTable.MergeFunction).Output.Kind);

            Assert.Equal([fInfo.Table, gInfo.Table], egraph.FunctionTables.ToArray());
        }

        [Fact]
        public void IdColumnsAreRebuiltAndBaseColumnsAreNot()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, i64, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = "f",
            });

            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var c = egraph.FreshId();
            egraph.AddValues([(f, [b, new Value(b.Rep), c])]);
            Union(egraph, a, b);

            // The id key column and the id output were canonicalized; the base column kept the raw value.
            Assert.Equal([$"{a.Rep} {b.Rep} {c.Rep}"], Show(egraph, f));
        }

        [Fact]
        public void SchemaMathMatchesEgglog()
        {
            var plain = new SchemaMath(3, false);
            Assert.Equal(2, plain.KeyCount);
            Assert.Equal(2, plain.OutputColumn);
            Assert.Equal(3, plain.TimestampColumn);
            Assert.Equal(4, plain.TableColumns);
            Assert.Equal(-1, plain.SubsumeColumnOrNone);
            Assert.Throws<InvalidOperationException>(() => plain.SubsumeColumn);

            var subsume = new SchemaMath(1, true);
            Assert.Equal(0, subsume.KeyCount);
            Assert.Equal(0, subsume.OutputColumn);
            Assert.Equal(1, subsume.TimestampColumn);
            Assert.Equal(2, subsume.SubsumeColumn);
            Assert.Equal(3, subsume.TableColumns);

            var row = new uint[] { 7, 99, 99 };
            subsume.WriteTail(row, 5, SchemaMath.Subsumed);
            Assert.Equal([7u, 5u, 1u], row);
            Assert.Throws<ArgumentOutOfRangeException>(() => new SchemaMath(0, false));
        }

        [Fact]
        public void AddTableRejectsBadDeclarations()
        {
            var egraph = NewEGraph();
            var externalsBefore = egraph.PanicFunctionCount;
            Assert.Throws<ArgumentException>(() => egraph.AddTable(new FunctionConfig
            {
                Schema = [],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = "empty",
            }));

            var g = Constructor(egraph, "g", false, ColumnTy.Id, ColumnTy.Id);

            // The function merge must pass one argument per key of g.
            Assert.Throws<ArgumentException>(() => egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.Function(g, MergeFn.Old),
                Name = "bad-arity",
            }));

            // An undeclared function cannot be referenced.
            Assert.Throws<ArgumentException>(() => egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.Function(new FunctionId(9), MergeFn.Old, MergeFn.New),
                Name = "undeclared",
            }));

            // A rejected declaration creates nothing.
            Assert.Equal(1, egraph.FunctionCount);
            Assert.Equal(externalsBefore, egraph.PanicFunctionCount);
            Assert.Throws<ArgumentException>(() => egraph.GetFunctionInfo(new FunctionId(1)));
        }

        [Fact]
        public void MergeDependenciesDecideTheMergeOrder()
        {
            var egraph = NewEGraph();
            var g = Constructor(egraph, "g", false, ColumnTy.Id, ColumnTy.Id);
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.Function(g, MergeFn.Old, MergeFn.New),
                Name = "f",
            });
            var h = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "h",
            });

            var deps = egraph.Database.Dependencies;
            var uf = deps.LevelOf(egraph.UnionFindTable);
            var gLevel = deps.LevelOf(egraph.GetTable(g));
            var fLevel = deps.LevelOf(egraph.GetTable(f));

            // A constructor only writes the union-find (same stratum); a function merge reads g (the next stratum).
            Assert.Equal(uf, gLevel);
            Assert.Equal(gLevel + 1, fLevel);
            Assert.Equal(uf, deps.LevelOf(egraph.GetTable(h)));
        }

        [Fact]
        public void EveryFunctionIsRegisteredByName()
        {
            var egraph = NewEGraph();
            var num = Constructor(egraph, "num", false, IntType(egraph));
            Assert.True(egraph.ActionRegistry.TryGetTable("num", out var action));
            Assert.Equal(egraph.GetTable(num), action!.Table);
            Assert.True(action.HasDefault);
            Assert.False(egraph.ActionRegistry.TryGetTable("missing", out _));
            Assert.Equal(1, egraph.ActionRegistry.Count);
        }

        [Fact]
        public void GetCanonReprCanonicalizesOnlyIds()
        {
            var egraph = NewEGraph();
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            Union(egraph, b, a);

            Assert.Equal(a, egraph.GetCanonRepr(b, ColumnTy.Id));
            Assert.Equal(a, egraph.GetCanonRepr(a, ColumnTy.Id));
            Assert.Equal(b, egraph.GetCanonRepr(b, ColumnTy.Base(new BaseValueTypeId(0))));

            // An id the union-find never saw is canonical.
            Assert.Equal(new Value(1000), egraph.GetCanonRepr(new Value(1000), ColumnTy.Id));
        }

        [Fact]
        public void ColumnTypesAndDefaultsCompareStructurally()
        {
            var bt = new BaseValueTypeId(3);
            Assert.Equal(ColumnTy.Id, ColumnTy.Id);
            Assert.Equal(ColumnTy.Id, default(ColumnTy));
            Assert.Equal("Id", ColumnTy.Id.ToString());
            Assert.Equal("Base(3)", ColumnTy.Base(bt).ToString());
            Assert.NotEqual(ColumnTy.Base(new BaseValueTypeId(0)), ColumnTy.Id);
            Assert.Equal(ColumnTy.Base(bt), ColumnTy.Base(bt));
            Assert.NotEqual(ColumnTy.Id, ColumnTy.Base(bt));
            Assert.True(ColumnTy.Id.IsId);
            Assert.False(ColumnTy.Id.IsBase);
            Assert.Equal(bt, ColumnTy.Base(bt).BaseType);
            Assert.Throws<InvalidOperationException>(() => ColumnTy.Id.BaseType);

            Assert.Equal(DefaultValKind.FreshId, DefaultVal.FreshId.Kind);
            Assert.Equal(DefaultValKind.Fail, DefaultVal.Fail.Kind);
            Assert.Equal(new Value(4), DefaultVal.Const(new Value(4)).Constant);
            Assert.Equal(DefaultVal.Const(new Value(4)), DefaultVal.Const(new Value(4)));
        }

    }

}
