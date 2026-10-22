using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Terms
{

    /// <summary>
    /// Primitives over the tables, egglog's read, write and full primitives (src/exec_state.rs), and where each may run:
    /// egglog's <c>tests/typed_primitive.rs</c> at 5e655cae, ported. A pure primitive runs everywhere; a read primitive
    /// where reads are, a host read or write and a naive rule; a write primitive where writes are, a rule's head, a merge
    /// and a host write; a full primitive only where both are, a host write and a naive rule's head. Elsewhere the call is
    /// refused when it is defined, as egglog's typechecker refuses it.
    /// </summary>
    public sealed class TablePrimitiveTests
    {

        static readonly Type[] Long = [typeof(long)];

        static readonly Type[] TwoLongs = [typeof(long), typeof(long)];

        /// <summary>
        /// egglog's <c>PureAdd</c>.
        /// </summary>
        static readonly PrimitiveN<long> PureAdd = Primitive.Create<long>("p-add", TwoLongs, args => (long)args[0] + (long)args[1]);

        /// <summary>
        /// egglog's <c>WriteEcho</c>: returns its argument from a write primitive's state.
        /// </summary>
        static readonly PrimitiveN<long> WriteEcho = Primitive.CreateWrite("w-echo", Long, (ITableWriter _, ReadOnlySpan<object> args, out long result) =>
        {
            result = (long)args[0];
            return true;
        });

        /// <summary>
        /// egglog's <c>FullEcho</c>.
        /// </summary>
        static readonly PrimitiveN<long> FullEcho = Primitive.CreateFull("f-echo", Long, (ITables _, ReadOnlySpan<object> args, out long result) =>
        {
            result = (long)args[0];
            return true;
        });

        /// <summary>
        /// egglog's <c>ReadLookup</c> of the table <c>f</c>: its value at a key, no result where it has none.
        /// </summary>
        static PrimitiveN<long> LookupOf(Function<long, long> f) =>
            Primitive.CreateRead("lookup-f", Long, (ITableReader tables, ReadOnlySpan<object> args, out long result) =>
                tables.TryLookup(f, [args[0]], out result));

        [Fact]
        public void APurePrimitiveRunsEverywhere()
        {
            // (check (= (p-add 2 3) 5)) (let $x (p-add 7 8)) (rule ((= y (p-add 1 2))) ((set (f y) (p-add 10 20)))) (run 1)
            var graph = new EGraph();
            var x = graph.DefineFunction<long>("x");
            var f = graph.DefineFunction<long, long>("f");
            Assert.True(graph.Root.Check(PureAdd.Of(2L, 3L).Is(5L)));
            graph.Root.Assume(x.Of().Is(PureAdd.Of(7L, 8L)));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("r", r =>
            {
                var y = r.Var<long>("y");
                r.When(y.Is(PureAdd.Of(1L, 2L)));
                r.Then(f.Of(y).Is(PureAdd.Of(10L, 20L)));
            });
            graph.Root.Run(rules);

            Assert.Equal(15L, graph.Root.Get(x.Of()));
            Assert.Equal(30L, graph.Root.Get(f.Of(3L)));
        }

        [Fact]
        public void AWritePrimitiveIsRefusedWhereNothingIsWritten()
        {
            // (rule () ((set (g 0) (w-echo 42)))) is fine; (rule ((= x (w-echo 1))) ...) and (check (= (w-echo 1) 1)) are
            // refused.
            var graph = new EGraph();
            var g = graph.DefineFunction<long, long>("g");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("head", r => r.Then(g.Of(0L).Is(WriteEcho.Of(42L))));
            graph.Root.Run(rules);
            Assert.Equal(42L, graph.Root.Get(g.Of(0L)));

            var body = Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("body", r =>
            {
                var x = r.Var<long>("x");
                r.When(x.Is(WriteEcho.Of(1L)));
                r.Then(g.Of(0L).Is(x));
            }));
            Assert.Contains("writes tables, and a semi-naive rule's body neither reads nor writes", body.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => graph.Root.Check(WriteEcho.Of(1L).Is(1L)));
            Assert.Throws<RuleDefinitionException>(() => graph.DefineQuery("query", q =>
            {
                var x = q.Var<long>("x");
                q.When(x.Is(WriteEcho.Of(1L)));
            }));
        }

        [Fact]
        public void AReadPrimitiveReadsOnlyWhereReadsAre()
        {
            // (set (f 7) 42) (check (= (lookup-f 7) 42)) (let $r (lookup-f 7)) (check (= $r 42)); a semi-naive rule's body
            // and head refuse it; a naive rule takes it in both: (set (f 1) 99) (rule ((= x (lookup-f 1))) ((set (g 0) x))
            // :naive) (run 1) (check (= (g 0) 99)).
            var graph = new EGraph();
            var f = graph.DefineFunction<long, long>("f");
            var g = graph.DefineFunction<long, long>("g");
            var r = graph.DefineFunction<long>("r");
            var lookup = LookupOf(f);
            graph.Root.Assume(f.Of(7L).Is(42L));
            Assert.True(graph.Root.Check(lookup.Of(7L).Is(42L)));
            Assert.False(graph.Root.Check(lookup.Of(8L).Is(42L)));
            graph.Root.Assume(r.Of().Is(lookup.Of(7L)));
            Assert.Equal(42L, graph.Root.Get(r.Of()));

            var rules = graph.DefineRuleset("rules");
            Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("body", b =>
            {
                var x = b.Var<long>("x");
                b.When(x.Is(lookup.Of(1L)));
                b.Then(g.Of(0L).Is(x));
            }));
            Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("head", b => b.Then(g.Of(0L).Is(lookup.Of(1L)))));

            graph.Root.Assume(f.Of(1L).Is(99L));
            rules.DefineRule("naive", b =>
            {
                var x = b.Var<long>("x");
                b.Naive();
                b.When(x.Is(lookup.Of(1L)));
                b.Then(g.Of(0L).Is(x), g.Of(1L).Is(lookup.Of(7L)));
            });
            graph.Root.Run(rules);
            Assert.Equal(99L, graph.Root.Get(g.Of(0L)));
            Assert.Equal(42L, graph.Root.Get(g.Of(1L)));
        }

        [Fact]
        public void AQueryReadsWithAReadPrimitive()
        {
            // A query is egglog's check, a read context: its body may call a read primitive, which sees the scope's rows.
            var graph = new EGraph();
            var f = graph.DefineFunction<long, long>("f");
            var key = graph.DefineRelation<long>("key");
            var lookup = LookupOf(f);
            var query = graph.DefineQuery("values", q =>
            {
                var (k, v) = q.Vars<long, long>("k", "v");
                q.When(key.Of(k), v.Is(lookup.Of(k)));
            });
            graph.Root.Assume(key.Of(1L), key.Of(2L), f.Of(1L).Is(10L));

            Assert.Equal(1, graph.Root.Count(query));
        }

        [Fact]
        public void AReadPrimitiveSeesTableSizes()
        {
            // (set (f 1) 10) (set (f 2) 20) (check (= (table-size "f") 2))
            var graph = new EGraph();
            var f = graph.DefineFunction<long, long>("f");
            var size = Primitive.CreateRead("table-size", [], (ITableReader tables, ReadOnlySpan<object> _, out long result) =>
            {
                result = tables.TableSize(f);
                return true;
            });
            graph.Root.Assume(f.Of(1L).Is(10L), f.Of(2L).Is(20L));

            Assert.True(graph.Root.Check(size.Of().Is(2L)));
        }

        [Fact]
        public void AFullPrimitiveRunsOnlyWhereReadsAndWritesAre()
        {
            // (let $ff (f-echo 7)) is fine; (check (= (f-echo 1) 1)), a semi-naive rule's body and head refuse it; with
            // :naive: (set (trigger) 1) (rule ((= _ (trigger))) ((set (f 0) (f-echo 5))) :naive) (run 1) (check (= (f 0) 5)).
            var graph = new EGraph();
            var ff = graph.DefineFunction<long>("ff");
            var f = graph.DefineFunction<long, long>("f");
            var trigger = graph.DefineFunction<long>("trigger");
            graph.Root.Assume(ff.Of().Is(FullEcho.Of(7L)));
            Assert.Equal(7L, graph.Root.Get(ff.Of()));
            Assert.Throws<ArgumentException>(() => graph.Root.Check(FullEcho.Of(1L).Is(1L)));

            var rules = graph.DefineRuleset("rules");
            Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("body", r =>
            {
                var x = r.Var<long>("x");
                r.When(x.Is(FullEcho.Of(1L)));
                r.Then(f.Of(0L).Is(x));
            }));
            Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("head", r => r.Then(f.Of(0L).Is(FullEcho.Of(1L)))));

            graph.Root.Assume(trigger.Of().Is(1L));
            rules.DefineRule("naive", r =>
            {
                var t = r.Var<long>("t");
                r.Naive();
                r.When(t.Is(trigger.Of()));
                r.Then(f.Of(0L).Is(FullEcho.Of(5L)));
            });
            graph.Root.Run(rules);
            Assert.Equal(5L, graph.Root.Get(f.Of(0L)));
        }

        [Fact]
        public void AMergeRunsInTheWriteContext()
        {
            // (function g () i64 :merge (w-echo old)) is fine; :merge (lookup-f old) and :merge (f-echo old) are refused.
            var graph = new EGraph();
            var f = graph.DefineFunction<long, long>("f");
            var g = graph.DefineFunction("g", FunctionMerge.Expression<long>((old, _) => WriteEcho.Of(old)));
            graph.Root.Assume(g.Of().Is(1L));
            graph.Root.Assume(g.Of().Is(2L));
            Assert.Equal(1L, graph.Root.Get(g.Of()));

            var lookup = LookupOf(f);
            Assert.Throws<ArgumentException>(() => graph.DefineFunction("h", FunctionMerge.Expression<long>((old, _) => lookup.Of(old))));
            Assert.Throws<ArgumentException>(() => graph.DefineFunction("k", FunctionMerge.Expression<long>((old, _) => FullEcho.Of(old))));
        }

        [Fact]
        public void AWritePrimitiveWritesFromAHeadAndAHostWrite()
        {
            // A write primitive that inserts a row of seen and returns its argument: from a rule's head, the row is staged
            // with the iteration's writes; from a host write, at the end of the fact, as egglog stages a command's.
            var graph = new EGraph();
            var seen = graph.DefineRelation<long>("seen");
            var g = graph.DefineFunction<long, long>("g");
            var mark = Primitive.CreateWrite("mark", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Insert(seen, args[0]);
                result = (long)args[0];
                return true;
            });
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("head", r => r.Then(g.Of(0L).Is(mark.Of(5L))));
            graph.Root.Run(rules);
            Assert.True(graph.Root.Check(seen.Of(5L)));

            graph.Root.Assume(g.Of(1L).Is(mark.Of(6L)), seen.Of(7L));
            Assert.True(graph.Root.Check(seen.Of(6L)));
            Assert.Equal(6L, graph.Root.Get(g.Of(1L)));
        }

        [Fact]
        public void AFullPrimitiveOfAHostWriteSeesTheBatchAndMakesTerms()
        {
            // In one write, a full primitive reads the value an earlier fact wrote and makes a term whose class it returns.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var num = graph.DefineConstructor<long, Node>("Num");
            var f = graph.DefineFunction<long, long>("f");
            var node = graph.DefineFunction<long, Node>("node");
            var make = Primitive.CreateFull("make-num", Long, (ITables tables, ReadOnlySpan<object> args, out Node result) =>
            {
                result = default;
                if (tables.TryLookup(f, [args[0]], out long value) == false)
                    return false;

                result = tables.Make<Node>(num, value);
                return true;
            });

            graph.Root.Write(w =>
            {
                w.Assume(f.Of(1L).Is(10L));
                w.Assume(node.Of(1L).Is(make.Of(1L)));
            });

            Assert.True(graph.Root.AreEqual(graph.Root.Get(node.Of(1L)), graph.Root.Make(num.Of(10L))));
            Assert.Throws<RuleEvaluationException>(() => graph.Root.Assume(node.Of(2L).Is(make.Of(2L))));
        }

        [Fact]
        public void ANaiveRulesReadPrimitiveReadsItsScope()
        {
            // A naive rule of a frozen root, run in a child: its read primitive reads the child's rows.
            var graph = new EGraph();
            var f = graph.DefineFunction<long, long>("f");
            var g = graph.DefineFunction<long, long>("g");
            var key = graph.DefineRelation<long>("key");
            var lookup = LookupOf(f);
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("copy", r =>
            {
                var (k, v) = r.Vars<long, long>("k", "v");
                r.Naive();
                r.When(key.Of(k), v.Is(lookup.Of(k)));
                r.Then(g.Of(k).Is(v));
            });
            graph.Root.Assume(key.Of(1L));
            graph.Root.Freeze();

            using var child = graph.Root.CreateChild();
            child.Assume(f.Of(1L).Is(11L));
            child.Run(rules);
            Assert.Equal(11L, child.Get(g.Of(1L)));
            Assert.False(graph.Root.Check(g.Of(1L).Is(11L)));
        }

        [Fact]
        public void ARefusedWriteFailsTheRuleEvenWhenCaught()
        {
            // A write primitive that writes a scoped view, which a primitive does not use, and catches the refusal.
            var graph = new EGraph();
            var tag = graph.DefineScopeTag("tagged");
            var seen = graph.DefineRelation<long>("seen");
            var g = graph.DefineFunction<long, long>("g");
            var sneaky = Primitive.CreateWrite("sneaky", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                try
                {
                    tables.Insert(seen.In(tag), args[0]);
                }
                catch (ArgumentException)
                {
                }

                result = (long)args[0];
                return true;
            });
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("head", r => r.Then(g.Of(0L).Is(sneaky.Of(1L))));

            Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules));
        }

        [Fact]
        public void AReadPrimitiveSumsEveryTablesSize()
        {
            // egglog's ReadAllTableSizes: (set (f 1) 10) (set (f 2) 20) (check (= (all-table-sizes) 2)), over the graph's
            // tables.
            var graph = new EGraph();
            var f = graph.DefineFunction<long, long>("f");
            graph.DefineRelation<long>("r");
            var sizes = Primitive.CreateRead("all-table-sizes", [], (ITableReader tables, ReadOnlySpan<object> _, out long result) =>
            {
                result = 0;
                foreach (var table in graph.Tables)
                    result += tables.TableSize(table);

                return true;
            });
            graph.Root.Assume(f.Of(1L).Is(10L), f.Of(2L).Is(20L));

            Assert.True(graph.Root.Check(sizes.Of().Is(2L)));
        }

        [Fact]
        public void AReadPrimitiveFiltersANaiveRule()
        {
            // (rule ((key k) (has-f k)) ((set (o k) 1)) :naive): the filter reads f.
            var graph = new EGraph();
            var key = graph.DefineRelation<long>("key");
            var f = graph.DefineFunction<long, long>("f");
            var o = graph.DefineFunction<long, long>("o");
            var has = Primitive.CreateRead("has-f", Long, (ITableReader tables, ReadOnlySpan<object> args, out bool result) =>
            {
                result = tables.TryLookup(f, [args[0]], out long _);
                return true;
            });
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("n", r =>
            {
                var k = r.Var<long>("k");
                r.Naive();
                r.When(key.Of(k), Fact.True(has.Of(k)));
                r.Then(o.Of(k).Is(1L));
            });
            graph.Root.Assume(key.Of(1L), key.Of(2L), f.Of(2L).Is(0L));
            graph.Root.Run(rules);

            Assert.Equal(1L, graph.Root.CountRows(o));
            Assert.True(graph.Root.Check(o.Of(2L).Is(1L)));
        }

        [Fact]
        public void EachCallOfAWritePrimitiveWrites()
        {
            // (function counter () i64 :merge (+ old new)), a primitive that adds 1 to it, called twice with one argument:
            // egglog gives each call of an action its own binding, so the counter is 2, from a head as from a host write.
            var graph = new EGraph();
            var counter = graph.DefineFunction("counter", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, @new)));
            var a = graph.DefineFunction<long>("a");
            var b = graph.DefineFunction<long>("b");
            var bump = Primitive.CreateWrite("bump", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Set(counter, [], 1L);
                result = (long)args[0];
                return true;
            });
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("twice", r => r.Then(a.Of().Is(bump.Of(1L)), b.Of().Is(bump.Of(1L))));
            graph.Root.Run(rules);
            Assert.Equal(2L, graph.Root.Get(counter.Of()));

            graph.Root.Assume(a.Of().Is(bump.Of(1L)), b.Of().Is(bump.Of(1L)));
            Assert.Equal(4L, graph.Root.Get(counter.Of()));
        }

        [Fact]
        public void AReadPrimitiveWritesNothing()
        {
            // A read primitive that casts its tables to a writer, as egglog's read state, which has no write methods,
            // cannot: refused in a naive rule's head and in a host write, whose contexts write.
            var graph = new EGraph();
            var seen = graph.DefineRelation<long>("seen");
            var o = graph.DefineFunction<long, long>("o");
            var sneaky = Primitive.CreateRead("sneaky", Long, (ITableReader tables, ReadOnlySpan<object> args, out long result) =>
            {
                ((ITableWriter)tables).Insert(seen, args[0]);
                result = 1;
                return true;
            });
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("n", r =>
            {
                r.Naive();
                r.Then(o.Of(0L).Is(sneaky.Of(9L)));
            });

            var write = Assert.Throws<RuleEvaluationException>(() => graph.Root.Assume(o.Of(1L).Is(sneaky.Of(10L))));
            Assert.Contains("only reads them", write.Message, StringComparison.Ordinal);
            Assert.Equal(0L, graph.Root.CountRows(seen));
            var run = Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules));
            Assert.Contains("only reads them", run.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void APrimitiveThatReadsTablesNeedsTheRulesLicence()
        {
            // A licensed rule reads fact tables only through its licence; a read primitive reads any, as a full callback
            // does, so it needs ReadsOwnFacts. A rule that writes an analysis table may not read a fact table through one.
            var graph = new EGraph();
            var tag = graph.DefineScopeTag("case");
            var key = graph.DefineRelation<long>("key");
            var f = graph.DefineFunction<long, long>("f");
            var o = graph.DefineFunction<long, long>("o");
            var an = graph.DefineFunction<long, long>("an", (ValueLattice<long>?)null, new TableOptions { Role = TableRole.Analysis });
            var lookup = LookupOf(f);
            var rules = graph.DefineRuleset("rules");

            var licensed = Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("licensed", r =>
            {
                var (k, v) = r.Vars<long, long>("k", "v");
                r.Erasing(tag);
                r.Naive();
                r.When(key.In(tag).Of(k), v.Is(lookup.Of(k)));
                r.Then(o.Of(k).Is(v));
            }));
            Assert.Contains("reads tables through primitive 'lookup-f', outside scope 'case'", licensed.Message, StringComparison.Ordinal);
            rules.DefineRule("own", r =>
            {
                var (k, v) = r.Vars<long, long>("k", "v");
                r.Erasing(tag);
                r.ReadsOwnFacts("the test reads f on purpose");
                r.Naive();
                r.When(key.In(tag).Of(k), v.Is(lookup.Of(k)));
                r.Then(o.Of(k).Is(v));
            });

            var analysis = Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("analysis", r =>
            {
                r.Naive();
                r.Then(an.Of(1L).Is(lookup.Of(1L)));
            }));
            Assert.Contains("reads tables through primitive 'lookup-f'", analysis.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AHostWritesPrimitiveDoesNotSeeItsFactsOwnTerms()
        {
            // egglog's lookup sees no row its command stages: a full primitive that makes Num(3) and looks it up does not
            // find it in its own fact, in a host write as in a naive rule's head; a later fact finds it.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var num = graph.DefineConstructor<long, Node>("Num");
            var o = graph.DefineFunction<long, long>("o");
            var both = Primitive.CreateFull("make-and-find", Long, (ITables tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Make<Node>(num, args[0]);
                result = tables.TryLookup(num, [args[0]], out Node _) ? 1 : 0;
                return true;
            });
            var find = Primitive.CreateRead("find", Long, (ITableReader tables, ReadOnlySpan<object> args, out long result) =>
            {
                result = tables.TryLookup(num, [args[0]], out Node _) ? 1 : 0;
                return true;
            });
            var count = Primitive.CreateRead("count", [], (ITableReader tables, ReadOnlySpan<object> _, out long result) =>
            {
                result = tables.TableSize(num);
                return true;
            });

            graph.Root.Write(w =>
            {
                w.Assume(o.Of(0L).Is(both.Of(3L)));
                w.Assume(o.Of(1L).Is(find.Of(3L)));
                w.Assume(o.Of(2L).Is(Primitives.Add.Of(count.Of(), find.Of(4L))));
            });
            Assert.Equal(0L, graph.Root.Get(o.Of(0L)));
            Assert.Equal(1L, graph.Root.Get(o.Of(1L)));
            Assert.Equal(1L, graph.Root.Get(o.Of(2L)));

            var makeAndCount = Primitive.CreateFull("make-and-count", Long, (ITables tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Make<Node>(num, args[0]);
                result = tables.TableSize(num);
                return true;
            });
            graph.Root.Assume(o.Of(4L).Is(makeAndCount.Of(7L)));
            Assert.Equal(1L, graph.Root.Get(o.Of(4L)));
            Assert.True(graph.Root.Check(count.Of().Is(2L)));

            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("n", r =>
            {
                r.Naive();
                r.Then(o.Of(3L).Is(both.Of(5L)));
            });
            graph.Root.Run(rules);
            Assert.Equal(0L, graph.Root.Get(o.Of(3L)));
        }

        [Fact]
        public void AHostWritesPrimitiveReadsNoTableOfClassesAfterAMerge()
        {
            // egglog's next command would see r(a) and r(b) become one row once a = b; the batch merges after its writes,
            // so a primitive's read of r after the batch's union is refused, and a later call reads 1.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var num = graph.DefineConstructor<long, Node>("Num");
            var r = graph.DefineRelation<Node>("r");
            var o = graph.DefineFunction<long, long>("o");
            var size = Primitive.CreateRead("size", [], (ITableReader tables, ReadOnlySpan<object> _, out long result) =>
            {
                result = tables.TableSize(r);
                return true;
            });
            var a = graph.Root.Make(num.Of(1L));
            var b = graph.Root.Make(num.Of(2L));
            graph.Root.Assume(r.Of(a), r.Of(b));

            var refused = Assert.Throws<RuleEvaluationException>(() => graph.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(o.Of(0L).Is(size.Of()));
            }));
            Assert.Contains("read after this batch merged classes", refused.Message, StringComparison.Ordinal);

            var node = graph.DefineFunction<long, Node>("node");
            graph.Root.Assume(node.Of(1L).Is(a), node.Of(2L).Is(b));
            var same = Primitive.CreateRead("same", [], (ITableReader tables, ReadOnlySpan<object> _, out long result) =>
            {
                result = tables.TryLookup(node, [1L], out Node x) && tables.TryLookup(node, [2L], out Node y) && x == y ? 1 : 0;
                return true;
            });
            var classes = Assert.Throws<RuleEvaluationException>(() => graph.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(o.Of(0L).Is(same.Of()));
            }));
            Assert.Contains("read after this batch merged classes", classes.Message, StringComparison.Ordinal);

            graph.Root.Union(a, b);
            Assert.True(graph.Root.Check(size.Of().Is(1L)));
            Assert.True(graph.Root.Check(same.Of().Is(1L)));
        }

        [Fact]
        public void AHostWritesPrimitiveSubsumesOnlyARowItSees()
        {
            // egglog's subsume looks the row up and fails without one: from a host write and from a rule's head. A row the
            // primitive sees is subsumed: queries skip it, checks still see it.
            var graph = new EGraph();
            var r = graph.DefineRelation<long>("r", new TableOptions { Subsumable = true });
            var o = graph.DefineFunction<long, long>("o");
            var sub = Primitive.CreateWrite("sub", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Subsume(r, args[0]);
                result = 0;
                return true;
            });
            var rows = graph.DefineQuery("rows", q =>
            {
                var x = q.Var<long>("x");
                q.When(r.Of(x));
            });

            var missing = Assert.Throws<RuleEvaluationException>(() => graph.Root.Assume(o.Of(0L).Is(sub.Of(4L))));
            Assert.Contains("holds no row for the keys", missing.Message, StringComparison.Ordinal);
            Assert.Equal(0L, graph.Root.CountRows(r));

            graph.Root.Assume(r.Of(6L));
            graph.Root.Write(w =>
            {
                w.Assume(r.Of(7L));
                w.Assume(o.Of(2L).Is(sub.Of(6L)));
                w.Assume(o.Of(3L).Is(sub.Of(7L)));
            });
            Assert.True(graph.Root.Check(r.Of(6L)));
            Assert.True(graph.Root.Check(r.Of(7L)));
            Assert.Equal(0, graph.Root.Count(rows));

            // A missing row an earlier fact subsumes is a row, as egglog's subsume of a relation inserts it.
            var has = Primitive.CreateRead("has", Long, (ITableReader tables, ReadOnlySpan<object> args, out long result) =>
            {
                result = tables.Contains(r, args[0]) ? tables.TableSize(r) : -1;
                return true;
            });
            graph.Root.Write(w =>
            {
                w.Subsume(r.Of(8L));
                w.Assume(o.Of(4L).Is(has.Of(8L)));
            });
            Assert.Equal(3L, graph.Root.Get(o.Of(4L)));

            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("s", b => b.Then(o.Of(1L).Is(sub.Of(5L))));
            Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules));
        }

        [Fact]
        public void AHostWritesPrimitiveDeletesAsEgglogsCommandsDo()
        {
            // egglog's flush applies a command's removals before its inserts, and its lookups see no staged removal; the
            // facts of a write are egglog's commands, one after the other.
            var graph = new EGraph();
            var f = graph.DefineFunction<long, long>("f");
            var r = graph.DefineRelation<long>("r");
            var o = graph.DefineFunction<long, long>("o");
            var del = Primitive.CreateWrite("del", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Remove(f, args[0]);
                result = 0;
                return true;
            });
            var delR = Primitive.CreateWrite("del-r", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Remove(r, args[0]);
                result = (long)args[0];
                return true;
            });
            var take = Primitive.CreateFull("take", Long, (ITables tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Remove(f, args[0]);
                if (tables.TryLookup(f, [args[0]], out result) == false)
                    result = -1;

                return true;
            });
            var size = Primitive.CreateRead("size", [], (ITableReader tables, ReadOnlySpan<object> _, out long result) =>
            {
                result = tables.TableSize(f);
                return true;
            });
            graph.Root.Assume(f.Of(1L).Is(3L), f.Of(2L).Is(4L), f.Of(3L).Is(6L), f.Of(4L).Is(8L), r.Of(5L));

            // (set (o 0) (del 1)): the row is gone.
            graph.Root.Assume(o.Of(0L).Is(del.Of(1L)));
            Assert.False(graph.Root.Check(Fact.Exists(f.Of(1L))));

            // (set (f 2) (+ 4 (del 2))) and (r (del-r 5)): the command's own write of the row it deletes is kept.
            graph.Root.Assume(f.Of(2L).Is(Primitives.Add.Of(4L, del.Of(2L))), r.Of(delR.Of(5L)));
            Assert.True(graph.Root.Check(f.Of(2L).Is(4L)));
            Assert.True(graph.Root.Check(r.Of(5L)));

            // The command's own lookup still sees the row: (set (o 1) (take 3)).
            graph.Root.Assume(o.Of(1L).Is(take.Of(3L)));
            Assert.Equal(6L, graph.Root.Get(o.Of(1L)));
            Assert.False(graph.Root.Check(Fact.Exists(f.Of(3L))));

            // (set (f 5) 7) (set (o 2) (del 5)): an earlier command's write is deleted. (set (o 3) (del 4)) (set (f 4) 9):
            // a later command writes the key afresh, and a later lookup sees the deletion.
            graph.Root.Write(w =>
            {
                w.Assume(f.Of(5L).Is(7L));
                w.Assume(o.Of(2L).Is(del.Of(5L)));
                w.Assume(o.Of(3L).Is(del.Of(4L)));
                w.Assume(o.Of(4L).Is(size.Of()));
                w.Assume(f.Of(4L).Is(9L));
                w.Assume(o.Of(5L).Is(size.Of()));
            });
            Assert.False(graph.Root.Check(Fact.Exists(f.Of(5L))));
            Assert.True(graph.Root.Check(f.Of(4L).Is(9L)));
            Assert.Equal(1L, graph.Root.Get(o.Of(4L)));
            Assert.Equal(2L, graph.Root.Get(o.Of(5L)));

            // (delete (f (mark 2))): a primitive in a deletion's keys writes.
            var seen = graph.DefineRelation<long>("seen");
            var mark = Primitive.CreateWrite("mark", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Insert(seen, args[0]);
                result = (long)args[0];
                return true;
            });
            graph.Root.Delete(f.Of(mark.Of(2L)));
            Assert.False(graph.Root.Check(Fact.Exists(f.Of(2L))));
            Assert.True(graph.Root.Check(seen.Of(2L)));

            // From a rule's head, as from a host write.
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("delete", b => b.Then(o.Of(6L).Is(del.Of(4L))));
            graph.Root.Run(rules);
            Assert.False(graph.Root.Check(Fact.Exists(f.Of(4L))));
        }

        [Fact]
        public void AHostWritesPrimitiveDeletesBeforeItsUnionsMerge()
        {
            // (f a) = 1, (f b) = 2: a primitive that deletes (f a) and unions a and b in one command meets no conflict,
            // since egglog's flush deletes before its rebuild merges.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var num = graph.DefineConstructor<long, Node>("Num");
            var f = graph.DefineFunction<Node, long>("f");
            var o = graph.DefineFunction<long, long>("o");
            var a = graph.Root.Make(num.Of(1L));
            var b = graph.Root.Make(num.Of(2L));
            graph.Root.Assume(f.Of(a).Is(1L), f.Of(b).Is(2L));
            var merge = Primitive.CreateWrite("merge", [], (ITableWriter tables, ReadOnlySpan<object> _, out long result) =>
            {
                tables.Remove(f, a);
                tables.Union(a, b);
                result = 0;
                return true;
            });

            graph.Root.Assume(o.Of(0L).Is(merge.Of()));
            Assert.True(graph.Root.AreEqual(a, b));
            Assert.True(graph.Root.Check(f.Of(a).Is(2L)));
        }

        [Fact]
        public void AUnionAndASubsumptionLookUpTheRowBeforeTheirCommandsDeletions()
        {
            // egglog's (union (C k) x) and (subsume (C k)) look the row up, as the earlier commands left it, and write after
            // their command's removals: a deletion of the row in the same command keeps the union, and the subsumed row,
            // unless it was subsumed already.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var c = graph.DefineConstructor<long, Node>("C");
            var rs = graph.DefineRelation<long>("rs");
            var o = graph.DefineFunction<long, long>("o");
            var rem = Primitive.CreateWrite("rem", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Remove(c, args[0]);
                result = (long)args[0];
                return true;
            });
            var remRs = Primitive.CreateWrite("rem-rs", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Remove(rs, args[0]);
                result = (long)args[0];
                return true;
            });
            var both = Primitive.CreateWrite("rem-and-subsume", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Remove(c, args[0]);
                tables.Subsume(c, args[0]);
                result = 0;
                return true;
            });
            var bothRs = Primitive.CreateWrite("rem-and-subsume-rs", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Remove(rs, args[0]);
                tables.Subsume(rs, args[0]);
                result = 0;
                return true;
            });
            var subsume = Primitive.CreateWrite("subsume", Long, (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Subsume(c, args[0]);
                result = (long)args[0];
                return true;
            });
            var x = graph.Root.Make(c.Of(5L));

            // (union (C (rem 4)) x): C(4) is gone, its class is x's.
            var c4 = graph.Root.Make(c.Of(4L));
            graph.Root.Assume(c.Of(rem.Of(4L)).Is(x));
            Assert.True(graph.Root.AreEqual(c4, x));
            Assert.False(graph.Root.Check(Fact.Exists(c.Of(4L))));

            // The same of a term an earlier command of the write made.
            Node c7 = default;
            graph.Root.Write(w =>
            {
                c7 = w.Make(c.Of(7L));
                w.Assume(c.Of(rem.Of(7L)).Is(x));
            });
            Assert.True(graph.Root.AreEqual(c7, x));
            Assert.False(graph.Root.Check(Fact.Exists(c.Of(7L))));

            // (subsume (C (rem 8))), a primitive's remove then subsume, and (delete (C (subsume 10))): the row stays,
            // subsumed, with its class.
            var c8 = graph.Root.Make(c.Of(8L));
            var c9 = graph.Root.Make(c.Of(9L));
            var c10 = graph.Root.Make(c.Of(10L));
            graph.Root.Subsume(c.Of(rem.Of(8L)));
            graph.Root.Assume(o.Of(0L).Is(both.Of(9L)));
            graph.Root.Delete(c.Of(subsume.Of(10L)));
            Assert.True(graph.Root.AreEqual(c8, graph.Root.Make(c.Of(8L))));
            Assert.True(graph.Root.AreEqual(c9, graph.Root.Make(c.Of(9L))));
            Assert.True(graph.Root.AreEqual(c10, graph.Root.Make(c.Of(10L))));
            var terms = graph.DefineQuery("terms", q =>
            {
                var k = q.Var<long>("k");
                q.When(Fact.Exists(c.Of(k)));
            });
            Assert.Equal(1, graph.Root.Count(terms));

            // A row subsumed already is left to the deletion: (subsume (rs (rem-rs 1))) and a primitive's remove then
            // subsume of rs(2).
            graph.Root.Assume(rs.Of(1L), rs.Of(2L));
            graph.Root.Subsume(rs.Of(1L), rs.Of(2L));
            graph.Root.Subsume(rs.Of(remRs.Of(1L)));
            graph.Root.Assume(o.Of(1L).Is(bothRs.Of(2L)));
            Assert.Equal(0L, graph.Root.CountRows(rs));
        }

        [Fact]
        public void AnAccessPrimitiveHasNoFunctionOfValuesAlone()
        {
            Assert.Equal(PrimitiveAccess.Write, WriteEcho.Access);
            Assert.Equal(PrimitiveAccess.Pure, PureAdd.Access);
            Assert.Equal(PrimitiveAccess.Write, Primitives.VecUnion<Node>().Access);
            Assert.Throws<InvalidOperationException>(() => WriteEcho.Function([1L]));
            Assert.Throws<InvalidOperationException>(() => WriteEcho.TryInvoke([1L], out _));
        }

    }

}
