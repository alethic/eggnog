using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for merge functions (egglog-bridge/src/lib.rs:1010-1246): every <see cref="MergeFn"/> case resolved into a
    /// function's table merge, the subsumption merge, the "changed" contract, panics, and the lattice declarations.
    /// egglog's <c>mergefn_arithmetic</c> and <c>mergefn_nested_function</c> (egglog-bridge/src/tests.rs:877-1120) are
    /// ported with the rules that write the rows replaced by <see cref="EGraph.AddValues"/> and
    /// <see cref="EGraph.AddTerm"/>, which go through the same merges.
    /// </summary>
    public class MergeFnTests
    {

        /// <summary>
        /// egglog's <c>mergefn_arithmetic</c>: a function whose merge is <c>(+ 1 (* old new))</c>.
        /// </summary>
        [Fact]
        public void MergeFnArithmetic()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var multiply = Register(egraph, (state, args) => args.Length == 2
                ? state.BaseValues.Get<Int64Codec, long>(state.BaseValues.Unwrap<Int64Codec, long>(args[0]) * state.BaseValues.Unwrap<Int64Codec, long>(args[1])).Rep
                : null);
            var add = Register(egraph, (state, args) => args.Length == 2
                ? state.BaseValues.Get<Int64Codec, long>(state.BaseValues.Unwrap<Int64Codec, long>(args[0]) + state.BaseValues.Unwrap<Int64Codec, long>(args[1])).Rep
                : null);

            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, i64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(add, MergeFn.Const(Int(egraph, 1)), MergeFn.Primitive(multiply, MergeFn.Old, MergeFn.New)),
                Name = "f",
            });

            List<(long, long)> Contents()
            {
                var contents = new List<(long, long)>();
                egraph.ForEach(f, row =>
                {
                    Assert.False(row.Subsumed);
                    contents.Add((Unint(egraph, row.Values[0]), Unint(egraph, row.Values[1])));
                });
                contents.Sort();
                return contents;
            }

            // (f 1 0) (f 2 1)
            egraph.AddValues([(f, [Int(egraph, 1), Int(egraph, 0)]), (f, [Int(egraph, 2), Int(egraph, 1)])]);
            Assert.Equal([(1L, 0L), (2L, 1L)], Contents());

            // (f 1 5) (f 2 6): 1 + 0 * 5 = 1 and 1 + 1 * 6 = 7.
            egraph.AddValues([(f, [Int(egraph, 1), Int(egraph, 5)]), (f, [Int(egraph, 2), Int(egraph, 6)])]);
            Assert.Equal([(1L, 1L), (2L, 7L)], Contents());

            // (f 1 3) (f 2 4): 1 + 1 * 3 = 4 and 1 + 7 * 4 = 29.
            egraph.AddValues([(f, [Int(egraph, 1), Int(egraph, 3)]), (f, [Int(egraph, 2), Int(egraph, 4)])]);
            Assert.Equal([(1L, 4L), (2L, 29L)], Contents());
            Assert.False(egraph.TryTakePanic(out _));
        }

        /// <summary>
        /// egglog's <c>mergefn_nested_function</c>: a function whose merge is <c>(g (g new new) (g old old))</c> over a
        /// constructor <c>g</c>, which mints the terms it looks up.
        /// </summary>
        [Fact]
        public void MergeFnNestedFunction()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var g = Constructor(egraph, "g", true, ColumnTy.Id, ColumnTy.Id);
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.Function(g, MergeFn.Function(g, MergeFn.New, MergeFn.New), MergeFn.Function(g, MergeFn.Old, MergeFn.Old)),
                Name = "f",
                CanSubsume = true,
            });

            List<(Value, Value, Value)> GEntries() => Rows(egraph, g).Select(r => (r.Values[0], r.Values[1], r.Values[2])).ToList();
            List<(long, Value)> FEntries() => Rows(egraph, f).Select(r => (Unint(egraph, r.Values[0]), r.Values[1])).OrderBy(e => e.Item1).ToList();

            // The rule's lookups of (f 1) and (f 2) mint their outputs; no merge has run, so g is empty.
            egraph.AddTerm(f, [Int(egraph, 1)]);
            egraph.AddTerm(f, [Int(egraph, 2)]);
            var f1 = FEntries();
            Assert.Equal(2, f1.Count);
            var base1 = f1[0].Item2;
            var base2 = f1[1].Item2;
            Assert.Empty(GEntries());

            // (set (f 1) base2) merges old = base1 with new = base2.
            egraph.AddValues([(f, [Int(egraph, 1), base2])]);
            var f2 = FEntries();
            var g2 = GEntries();
            Assert.Equal(2, f2.Count);
            Assert.Equal(3, g2.Count);

            var newBase1 = f2[0].Item2;
            var (mid1, mid2, _) = g2.Single(e => e.Item3 == newBase1);
            var (baseL1, baseL2, _) = g2.Single(e => e.Item3 == mid1);
            var (baseR1, baseR2, _) = g2.Single(e => e.Item3 == mid2);

            // (g (g new new) (g old old)) with new = base2 and old = base1.
            Assert.Equal(baseL1, baseL2);
            Assert.Equal(base2, baseL1);
            Assert.Equal(baseR1, baseR2);
            Assert.Equal(base1, baseR1);
        }

        [Fact]
        public void FunctionMergeSkipsEqualValues()
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
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            egraph.AddValues([(f, [a, b])]);
            egraph.AddValues([(f, [a, b])]);

            // Equal values short-circuit (egglog PR 287): nothing is looked up or minted.
            Assert.Equal(0, egraph.TableSize(g));
            Assert.Equal([$"{a.Rep} {b.Rep}"], Show(egraph, f));
        }

        [Fact]
        public void FunctionMergeOnACustomFunctionReadsAndPanicsWhenMissing()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var max = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, i64, i64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "max",
            });
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, i64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Function(max, MergeFn.Old, MergeFn.New),
                Name = "f",
            });

            var a = egraph.FreshId();
            egraph.AddValues([(max, [Int(egraph, 3), Int(egraph, 5), Int(egraph, 5)])]);
            egraph.AddValues([(f, [a, Int(egraph, 3)])]);
            egraph.AddValues([(f, [a, Int(egraph, 5)])]);
            Assert.Equal(Int(egraph, 5), egraph.LookupId(f, [a]));

            // (max 5 4) is not in the table: a custom function only reads, so the merge panics and keeps the old value.
            egraph.AddValues([(f, [a, Int(egraph, 4)])]);
            Assert.Equal(Int(egraph, 5), egraph.LookupId(f, [a]));
            Assert.Equal(1, egraph.TableSize(max));
            Assert.True(egraph.TryTakePanic(out var message));
            Assert.Equal("Lookup on max failed in the merge function for f", message);
        }

        [Fact]
        public void OldNewAndConstMerges()
        {
            var egraph = NewEGraph();
            FunctionId Declare(string name, MergeFn merge) => egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = merge,
                Name = name,
            });

            var old = Declare("old", MergeFn.Old);
            var @new = Declare("new", MergeFn.New);
            var constant = Declare("const", MergeFn.Const(new Value(9)));
            var a = egraph.FreshId();
            foreach (var function in new[] { old, @new, constant })
            {
                egraph.AddValues([(function, [a, new Value(10)])]);
                egraph.AddValues([(function, [a, new Value(11)])]);
            }

            Assert.Equal(new Value(10), egraph.LookupId(old, [a]));
            Assert.Equal(new Value(11), egraph.LookupId(@new, [a]));
            Assert.Equal(new Value(9), egraph.LookupId(constant, [a]));

            // Merging the constant into a row that already holds it is not a change.
            var ts = PhysicalRow(egraph, constant, a.Rep)[2];
            egraph.AddValues([(constant, [a, new Value(12)])]);
            Assert.Equal(ts, PhysicalRow(egraph, constant, a.Rep)[2]);
        }

        [Fact]
        public void ConstantAndPrimitiveMergesRunEvenOnEqualValues()
        {
            var egraph = NewEGraph();
            var sum = Register(egraph, (_, args) => args[0] + args[1]);
            FunctionId Declare(string name, MergeFn merge) => egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = merge,
                Name = name,
            });

            var constant = Declare("const", MergeFn.Const(new Value(9)));
            var doubled = Declare("sum", MergeFn.Primitive(sum, MergeFn.Old, MergeFn.New));
            var a = egraph.FreshId();
            foreach (var function in new[] { constant, doubled })
            {
                egraph.AddValues([(function, [a, new Value(10)])]);
                egraph.AddValues([(function, [a, new Value(10)])]);
            }

            // egglog runs the merge on every conflict; only merges that are the identity on equal values may skip it.
            Assert.Equal(new Value(9), egraph.LookupId(constant, [a]));
            Assert.Equal(new Value(20), egraph.LookupId(doubled, [a]));
        }

        /// <summary>
        /// An id constant yields its class as the last rebuild left it canonical, as egglog's read of a global yields the
        /// class the rebuild keeps canonical in the global's table; one that keeps the old value yields it when the values
        /// agree, as egglog's function node does (egglog PR 287), and one that does not yields the class then too.
        /// </summary>
        [Fact]
        public void AnIdConstantYieldsItsCanonicalClassUnlessTheValuesAgree()
        {
            var egraph = NewEGraph();
            var x = egraph.FreshId();
            var y = egraph.FreshId();
            var g = egraph.FreshId();
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.IdConst(g, true),
                Name = "f",
            });
            var h = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.IdConst(g, false),
                Name = "h",
            });
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var c = egraph.FreshId();

            egraph.AddValues([(f, [a, x])]);
            egraph.AddValues([(f, [a, y])]);
            Assert.Equal(g, egraph.LookupId(f, [a]));

            egraph.AddValues([(f, [b, y])]);
            egraph.AddValues([(f, [b, y])]);
            Assert.Equal(y, egraph.LookupId(f, [b]));

            egraph.AddValues([(h, [b, y])]);
            egraph.AddValues([(h, [b, y])]);
            Assert.Equal(g, egraph.LookupId(h, [b]));

            // g loses a union to the smaller x: the merge yields x.
            Union(egraph, x, g);
            egraph.AddValues([(f, [c, y])]);
            egraph.AddValues([(f, [c, a])]);
            Assert.Equal(x, egraph.LookupId(f, [c]));
        }

        [Fact]
        public void AChangedMergeTakesTheIncomingTimestampAndAnUnchangedOneKeepsItsOwn()
        {
            var egraph = NewEGraph();
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.New,
                Name = "f",
            });
            var a = egraph.FreshId();
            egraph.AddValues([(f, [a, new Value(10)])]);
            Assert.Equal(1u, PhysicalRow(egraph, f, a.Rep)[2]);

            egraph.AddValues([(f, [a, new Value(10)])]);
            Assert.Equal(1u, PhysicalRow(egraph, f, a.Rep)[2]);

            egraph.AddValues([(f, [a, new Value(20)])]);
            Assert.Equal([a.Rep, 20u, 3u], PhysicalRow(egraph, f, a.Rep));
        }

        [Fact]
        public void SubsumptionIsStickyAndAloneCountsAsAChange()
        {
            var egraph = NewEGraph();
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Old,
                Name = "f",
                CanSubsume = true,
            });
            var a = egraph.FreshId();
            var table = egraph.GetTable(f);

            void Stage(uint output, uint subsumed)
            {
                egraph.WithExecutionState(state =>
                {
                    state.StageInsert(table, [a.Rep, output, (uint)state.ReadCounter(egraph.TimestampCounter), subsumed]);
                    return 0;
                });
            }

            Stage(5, SchemaMath.NotSubsumed);
            Assert.True(egraph.FlushUpdates());

            // Only the subsumption cell changes: a change, stamped with the incoming timestamp, keeping the old output.
            Stage(6, SchemaMath.Subsumed);
            Assert.True(egraph.FlushUpdates());
            Assert.Equal([a.Rep, 5u, 2u, SchemaMath.Subsumed], PhysicalRow(egraph, f, a.Rep));

            // A row that is not subsumed does not clear the cell, and changes nothing.
            Stage(7, SchemaMath.NotSubsumed);
            Assert.False(egraph.FlushUpdates());
            Assert.Equal([a.Rep, 5u, 2u, SchemaMath.Subsumed], PhysicalRow(egraph, f, a.Rep));
        }

        [Fact]
        public void UnionIdMergeKeepsTheSmallerIdAndUnions()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var a = egraph.FreshId();
            var x = egraph.FreshId();
            var y = egraph.FreshId();

            egraph.AddValues([(f, [a, y])]);
            egraph.AddValues([(f, [a, x])]);

            // The incoming id is smaller: the row changes to it, and x and y are one class.
            Assert.Equal(x, egraph.LookupId(f, [a]));
            Assert.Equal(x, egraph.GetCanonRepr(y, ColumnTy.Id));

            var z = egraph.FreshId();
            egraph.AddValues([(f, [a, z])]);
            Assert.Equal(x, egraph.LookupId(f, [a]));
            Assert.Equal(x, egraph.GetCanonRepr(z, ColumnTy.Id));
            Assert.Equal(2, egraph.UnionCount);
        }

        [Fact]
        public void AssertEqConflictPanicsAndKeepsTheOldValue()
        {
            var egraph = NewEGraph();
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "f",
            });
            var a = egraph.FreshId();
            egraph.AddValues([(f, [a, new Value(10)])]);
            egraph.AddValues([(f, [a, new Value(10)])]);
            Assert.False(egraph.PanicChannel.HasValue);

            egraph.AddValues([(f, [a, new Value(11)])]);
            Assert.Equal(new Value(10), egraph.LookupId(f, [a]));

            // The flush does not report the panic; the next iteration does (egglog's run_rules).
            var error = Assert.Throws<PanicException>(() => egraph.RunIteration(_ => RuleSetReport.Empty));
            Assert.Equal("Illegal merge attempted for function f", error.PanicMessage);
            Assert.Equal("Panic: Illegal merge attempted for function f", error.Message);
            egraph.RunIteration(_ => RuleSetReport.Empty);
        }

        [Fact]
        public void APrimitiveThatProducesNothingPanicsAndKeepsTheOldValue()
        {
            var egraph = NewEGraph();
            var fail = Register(egraph, (_, _) => null);
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(fail, MergeFn.Old, MergeFn.New),
                Name = "f",
            });
            var a = egraph.FreshId();
            egraph.AddValues([(f, [a, new Value(10)])]);
            egraph.AddValues([(f, [a, new Value(11)])]);
            Assert.Equal(new Value(10), egraph.LookupId(f, [a]));
            Assert.True(egraph.TryTakePanic(out var message));
            Assert.Equal("Merge function for f primitive call failed", message);
        }

        [Fact]
        public void APrimitiveMergeWithManyArgumentsEvaluatesThemInOrder()
        {
            var egraph = NewEGraph();
            var seen = new List<uint[]>();
            var first = Register(egraph, (_, args) =>
            {
                seen.Add(args);
                return args[0];
            });
            var args = Enumerable.Range(0, 12).Select(i => i % 3 == 0 ? MergeFn.Old : i % 3 == 1 ? MergeFn.New : MergeFn.Const(new Value((uint)(100 + i)))).ToArray();
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(first, args),
                Name = "f",
            });
            var a = egraph.FreshId();
            egraph.AddValues([(f, [a, new Value(10)])]);
            egraph.AddValues([(f, [a, new Value(11)])]);

            var call = Assert.Single(seen);
            Assert.Equal([10u, 11u, 102u, 10u, 11u, 105u, 10u, 11u, 108u, 10u, 11u, 111u], call);
        }

        [Fact]
        public void MergesDeclareWhetherTheyAreLattices()
        {
            var prim = new ExternalFunctionId(3);
            Assert.True(MergeFn.AssertEq.IsLattice);
            Assert.True(MergeFn.UnionId.IsLattice);
            Assert.False(MergeFn.Old.IsLattice);
            Assert.False(MergeFn.New.IsLattice);
            Assert.False(MergeFn.Const(new Value(1)).IsLattice);
            Assert.False(MergeFn.IdConst(new Value(1), true).IsLattice);
            Assert.Throws<InvalidOperationException>(() => MergeFn.IdConst(new Value(1), true).AsLattice());

            var min = MergeFn.Primitive(prim, MergeFn.Old, MergeFn.New);
            Assert.False(min.IsLattice);
            var declared = min.AsLattice();
            Assert.True(declared.IsLattice);
            Assert.Same(declared, declared.AsLattice());
            Assert.Equal(MergeFnKind.Primitive, declared.Kind);
            Assert.Equal(prim, declared.PrimitiveId);
            Assert.Equal(2, declared.Args.Length);
            Assert.True(MergeFn.Function(new FunctionId(0), MergeFn.Old).AsLattice().IsLattice);
            Assert.Throws<InvalidOperationException>(() => MergeFn.Old.AsLattice());
            Assert.Throws<InvalidOperationException>(() => MergeFn.UnionId.AsLattice());

            // The table's merge carries the declaration.
            var egraph = NewEGraph();
            var max = Register(egraph, (_, args) => Math.Max(args[0], args[1]));
            var lattice = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(max, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "lattice",
            });
            var plain = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.New,
                Name = "plain",
            });
            Assert.True(((SortedWritesTable)egraph.Database.GetTable(egraph.GetTable(lattice))).MergeFunction.IsLattice);
            Assert.False(((SortedWritesTable)egraph.Database.GetTable(egraph.GetTable(plain))).MergeFunction.IsLattice);
        }

        [Fact]
        public void MergeFnFormatsAsAnExpression()
        {
            var merge = MergeFn.Primitive(new ExternalFunctionId(2), MergeFn.Const(new Value(1)), MergeFn.Function(new FunctionId(0), MergeFn.Old, MergeFn.New));
            Assert.Equal("(f2 (const 1) (fn0 old new))", merge.ToString());
            Assert.Equal("union", MergeFn.UnionId.ToString());
            Assert.Equal("(id 4)", MergeFn.IdConst(new Value(4), true).ToString());
            Assert.Equal("assert-eq", MergeFn.AssertEq.ToString());
            Assert.Throws<ArgumentNullException>(() => MergeFn.Primitive(new ExternalFunctionId(0), MergeFn.Old, null!));
        }

        [Fact]
        public void ResolvingCreatesThePanicsEgglogCreates()
        {
            var egraph = NewEGraph();
            var g = Constructor(egraph, "g", false, ColumnTy.Id);
            var prim = Register(egraph, (_, args) => args[0]);
            egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(prim, MergeFn.Function(g, MergeFn.Old), MergeFn.AssertEq),
                Name = "f",
            });

            // Resolution order: the function node's panic, then its argument; the assert-eq panic; the primitive's
            // panic.
            var lookup = egraph.NewPanic("Lookup on g failed in the merge function for f");
            var assertEq = egraph.NewPanic("Illegal merge attempted for function f");
            var primitive = egraph.NewPanic("Merge function for f primitive call failed");
            Assert.True(lookup.Rep < assertEq.Rep);
            Assert.True(assertEq.Rep < primitive.Rep);
            Assert.Equal(4, egraph.PanicFunctionCount);
        }

    }

}
