using System;
using System.Collections.Generic;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the writes and reads of an <see cref="EGraph"/> outside rules (egglog-bridge/src/lib.rs:322-504, 946-959):
    /// <see cref="EGraph.AddValues"/>, <see cref="EGraph.AddTerm"/>, <see cref="EGraph.LookupId"/>, the size queries,
    /// <see cref="EGraph.ClearTable"/>, the row iteration and <see cref="EGraph.FlushUpdates"/>.
    /// </summary>
    public class EGraphValuesTests
    {

        [Fact]
        public void AddValuesStampsRowsWithTheCurrentTimestampAndAdvancesIt()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = "f",
                CanSubsume = true,
            });

            var id = egraph.FreshId();
            egraph.AddValues([(f, [Int(egraph, 7), id])]);

            // [key, output, ts = 1, not subsumed]; the flush advanced the timestamp.
            Assert.Equal([Int(egraph, 7).Rep, id.Rep, 1u, SchemaMath.NotSubsumed], PhysicalRow(egraph, f, Int(egraph, 7).Rep));
            Assert.Equal(2u, egraph.NextTimestamp);
            Assert.Equal(id, egraph.LookupId(f, [Int(egraph, 7)]));
            Assert.Null(egraph.LookupId(f, [Int(egraph, 8)]));
        }

        [Fact]
        public void AddValuesRejectsMalformedRowsWithoutWritingAnything()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var a = egraph.FreshId();
            Assert.Throws<ArgumentException>(() => egraph.AddValues([(f, [a, a]), (f, [a])]));
            Assert.Throws<ArgumentException>(() => egraph.AddValues([(new FunctionId(5), [a, a])]));
            Assert.Equal(0, egraph.TableSize(f));
            Assert.Equal(1u, egraph.NextTimestamp);
        }

        [Fact]
        public void AddValuesToSeveralFunctionsMergesThemTogether()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var g = Constructor(egraph, "g", false, ColumnTy.Id, ColumnTy.Id);
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var c = egraph.FreshId();
            egraph.AddValues([(f, [a, b]), (g, [a, b, c]), (f, [b, c])]);

            Assert.Equal([$"{a.Rep} {b.Rep}", $"{b.Rep} {c.Rep}"], Show(egraph, f));
            Assert.Equal([$"{a.Rep} {b.Rep} {c.Rep}"], Show(egraph, g));
            Assert.Equal(2, egraph.TableSize(f));
            Assert.Equal(2, egraph.ApproxTableSize(f));
            Assert.Equal(1, egraph.TableSize(g));
        }

        [Fact]
        public void AddTermReturnsTheExistingTermsCanonicalId()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var num = Constructor(egraph, "num", false, i64);
            var add = Constructor(egraph, "add", false, ColumnTy.Id, ColumnTy.Id);

            var one = egraph.AddTerm(num, [Int(egraph, 1)]);
            var two = egraph.AddTerm(num, [Int(egraph, 2)]);
            Assert.Equal(new Value(0), one);
            Assert.Equal(new Value(1), two);

            // The same term again: a fresh id is minted and unioned with the existing one, which is smaller and wins.
            var oneAgain = egraph.AddTerm(num, [Int(egraph, 1)]);
            Assert.Equal(one, oneAgain);
            Assert.Equal(new Value(2), egraph.GetCanonRepr(new Value(2), ColumnTy.Base(new BaseValueTypeId(0))));
            Assert.Equal(one, egraph.GetCanonRepr(new Value(2), ColumnTy.Id));
            Assert.Equal(1, egraph.UnionCount);

            var sum = egraph.AddTerm(add, [one, two]);
            Assert.Equal(sum, egraph.AddTerm(add, [one, two]));
            Assert.Equal(2, egraph.TableSize(num));
            Assert.Equal(1, egraph.TableSize(add));
            Assert.Throws<ArgumentException>(() => egraph.AddTerm(add, [one]));
        }

        [Fact]
        public void LookupIdDoesNotCanonicalizeItsKey()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var fb = egraph.AddTerm(f, [b]);
            Union(egraph, a, b);

            // The row was rebuilt to f(a); a lookup of the stale key b misses (egglog's lookup_id, map §3.6).
            Assert.Null(egraph.LookupId(f, [b]));
            Assert.Equal(fb, egraph.LookupId(f, [a]));
            Assert.Throws<ArgumentException>(() => egraph.LookupId(f, [a, b]));
        }

        [Fact]
        public void ForEachHidesBookkeepingAndReportsSubsumption()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var f = Constructor(egraph, "f", true, i64);
            var x = egraph.AddTerm(f, [Int(egraph, 1)]);
            var y = egraph.AddTerm(f, [Int(egraph, 2)]);
            egraph.WithExecutionState(state =>
            {
                egraph.ActionRegistry.TryGetTable("f", out var action);
                action!.Subsume(state, [Int(egraph, 2).Rep]);
                return 0;
            });
            egraph.FlushUpdates();

            var rows = Rows(egraph, f);
            Assert.Equal(2, rows.Count);
            Assert.Equal([Int(egraph, 1), x], rows[0].Values);
            Assert.False(rows[0].Subsumed);
            Assert.Equal([Int(egraph, 2), y], rows[1].Values);
            Assert.True(rows[1].Subsumed);
        }

        [Fact]
        public void ForEachWhileStopsWhenTheVisitorSaysSo()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var f = Constructor(egraph, "f", false, i64);
            for (var i = 0; i < 100; i++)
                egraph.AddTerm(f, [Int(egraph, i)]);

            var seen = 0;
            egraph.ForEachWhile(f, row =>
            {
                seen++;
                return seen < 40;
            });
            Assert.Equal(40, seen);

            var counter = new CountingRowVisitor(int.MaxValue);
            egraph.ForEach(f, ref counter);
            Assert.Equal(100, counter.Count);

            var limited = new CountingRowVisitor(5);
            egraph.ForEach(f, ref limited);
            Assert.Equal(5, limited.Count);
        }

        [Fact]
        public void ForEachSkipsSupersededRows()
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
            egraph.AddValues([(f, [a, new Value(11)])]);
            egraph.AddValues([(f, [a, new Value(12)])]);

            Assert.Equal([$"{a.Rep} 12"], Show(egraph, f));
            Assert.Equal(1, egraph.TableSize(f));
        }

        [Fact]
        public void ClearTableEmptiesOnlyThatFunction()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var g = Constructor(egraph, "g", false, ColumnTy.Id);
            var a = egraph.FreshId();
            egraph.AddTerm(f, [a]);
            egraph.AddTerm(g, [a]);

            egraph.ClearTable(f);
            Assert.Equal(0, egraph.TableSize(f));
            Assert.Equal(1, egraph.TableSize(g));
            Assert.Empty(Rows(egraph, f));
            Assert.Null(egraph.LookupId(f, [a]));

            // The function works as before.
            var fa = egraph.AddTerm(f, [a]);
            Assert.Equal(fa, egraph.LookupId(f, [a]));
        }

        [Fact]
        public void FlushUpdatesReportsWhetherTheMergeChangedAnything()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var a = egraph.FreshId();
            Assert.False(egraph.FlushUpdates());
            Assert.Equal(2u, egraph.NextTimestamp);

            egraph.WithExecutionState(state =>
            {
                egraph.ActionRegistry.TryGetTable("f", out var action);
                action!.Insert(state, [a.Rep, a.Rep]);
                return 0;
            });
            Assert.True(egraph.FlushUpdates());
            Assert.Equal(3u, egraph.NextTimestamp);

            // Re-deriving the same row is not a change, and keeps the row's timestamp.
            egraph.WithExecutionState(state =>
            {
                egraph.ActionRegistry.TryGetTable("f", out var action);
                action!.Insert(state, [a.Rep, a.Rep]);
                return 0;
            });
            Assert.False(egraph.FlushUpdates());
            Assert.Equal(2u, PhysicalRow(egraph, f, a.Rep)[2]);
        }

        [Fact]
        public void FlushUpdatesRebuildsAfterAUnion()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var fa = egraph.AddTerm(f, [a]);
            var fb = egraph.AddTerm(f, [b]);
            Assert.NotEqual(fa, fb);
            var before = egraph.NextTimestamp;

            Union(egraph, a, b);

            // Congruence: f(a) = f(b) once a = b.
            Assert.Equal(egraph.GetCanonRepr(fa, ColumnTy.Id), egraph.GetCanonRepr(fb, ColumnTy.Id));
            Assert.Equal([$"{a.Rep} {Math.Min(fa.Rep, fb.Rep)}"], Show(egraph, f));

            // The flush took one timestamp and the rebuild at least two passes (one that changed the table, one that
            // found nothing to do), each advancing it.
            Assert.True(egraph.NextTimestamp >= before + 3);
        }

        [Fact]
        public void DumpDebugInfoListsEveryRow()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var a = egraph.FreshId();
            var fa = egraph.AddTerm(f, [a]);
            var writer = new System.IO.StringWriter();
            egraph.DumpDebugInfo(writer);
            var text = writer.ToString();
            Assert.Contains("=== View Tables ===", text);
            Assert.Contains($"View Table f / {f} / {egraph.GetTable(f)}: [{a.Rep}, {fa.Rep}, 1]", text);
        }

    }

}
