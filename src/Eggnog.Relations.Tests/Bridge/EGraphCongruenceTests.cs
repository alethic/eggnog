using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A differential test of an <see cref="EGraph"/> against <see cref="ReferenceView"/>, a naive congruence closure:
    /// random programs of terms (<see cref="EGraph.AddTerm"/>), explicit rows (<see cref="EGraph.AddValues"/>) and
    /// unions (a <see cref="UnionAction"/> and <see cref="EGraph.FlushUpdates"/>) over constructors and a lattice
    /// function, checked after every step. Both sides keep the smallest id of a class canonical, so the views must
    /// agree exactly.
    /// </summary>
    public class EGraphCongruenceTests
    {

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void RandomProgramsAgreeWithTheReference(int seed)
        {
            Run(seed, steps: 250, ParallelHeuristics.Serial);
        }

        [Fact]
        public void ARandomProgramAgreesWithTheReferenceWithParallelSettings()
        {
            Run(7, steps: 250, new ParallelHeuristics(4));
        }

        static void Run(int seed, int steps, ParallelHeuristics parallelism)
        {
            var random = new Random(seed);
            var egraph = new EGraph(parallelism);
            var i64 = IntType(egraph);
            var min = Register(egraph, (_, args) => Math.Min(args[0], args[1]));
            var pair = Constructor(egraph, "pair", false, ColumnTy.Id, ColumnTy.Id);
            var wrap = Constructor(egraph, "wrap", true, ColumnTy.Id);
            var named = Constructor(egraph, "named", false, i64);
            var low = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, i64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(min, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "low",
            });

            var refPair = new ReferenceTable("pair", 2, [true, true, true], true);
            var refWrap = new ReferenceTable("wrap", 1, [true, true], true);
            var refNamed = new ReferenceTable("named", 1, [false, true], true);
            var refLow = new ReferenceTable("low", 1, [true, false], true, Math.Min);
            var reference = new ReferenceView([refPair, refWrap, refNamed, refLow]);
            var functions = new (FunctionId Function, ReferenceTable Table)[] { (pair, refPair), (wrap, refWrap), (named, refNamed), (low, refLow) };

            var ids = new List<uint>();
            var trace = new StringBuilder();
            for (var i = 0; i < 4; i++)
                ids.Add(egraph.FreshId().Rep);

            Value Pick() => egraph.GetCanonRepr(new Value(ids[random.Next(ids.Count)]), ColumnTy.Id);

            uint NextId() => (uint)egraph.Database.ReadCounter(egraph.IdCounter);

            for (var step = 0; step < steps; step++)
            {
                // Mostly new terms; unions and explicit outputs (which union with an existing output) are rare, which
                // keeps many classes apart.
                var op = random.Next(40) switch
                {
                    < 20 => 0,
                    < 26 => 2,
                    < 30 => 3,
                    < 34 => 4,
                    < 37 => 5,
                    _ => 6,
                };
                switch (op)
                {
                    case 0:
                    {
                        var (a, b) = (Pick(), Pick());
                        var id = NextId();
                        var result = egraph.AddTerm(pair, [a, b]);
                        trace.AppendLine($"pair({a.Rep}, {b.Rep}) minted {id} -> {result.Rep}");
                        reference.Add(refPair, [a.Rep, b.Rep, id]);
                        ids.Add(id);
                        break;
                    }
                    case 2:
                    {
                        var a = Pick();
                        var id = NextId();
                        var result = egraph.AddTerm(wrap, [a]);
                        trace.AppendLine($"wrap({a.Rep}) minted {id} -> {result.Rep}");
                        reference.Add(refWrap, [a.Rep, id]);
                        ids.Add(id);
                        break;
                    }
                    case 3:
                    {
                        var k = Int(egraph, random.Next(6));
                        var id = NextId();
                        var result = egraph.AddTerm(named, [k]);
                        trace.AppendLine($"named({k.Rep}) minted {id} -> {result.Rep}");
                        reference.Add(refNamed, [k.Rep, id]);
                        ids.Add(id);
                        break;
                    }
                    case 4:
                    {
                        var a = Pick();
                        var v = Int(egraph, random.Next(20));
                        egraph.AddValues([(low, [a, v])]);
                        trace.AppendLine($"low({a.Rep}) = {v.Rep}");
                        reference.Add(refLow, [a.Rep, v.Rep]);
                        break;
                    }
                    case 5:
                    {
                        var (a, b, c) = (Pick(), Pick(), Pick());
                        egraph.AddValues([(pair, [a, b, c])]);
                        trace.AppendLine($"set pair({a.Rep}, {b.Rep}) = {c.Rep}");
                        reference.Add(refPair, [a.Rep, b.Rep, c.Rep]);
                        break;
                    }
                    default:
                    {
                        var (a, b) = (Pick(), Pick());
                        Union(egraph, a, b);
                        trace.AppendLine($"union({a.Rep}, {b.Rep})");
                        reference.Union(a.Rep, b.Rep);
                        break;
                    }
                }

                reference.Close();
                Check(egraph, reference, functions, ids, trace);
            }

            Assert.False(egraph.TryTakePanic(out var message), message);

            // The program exercised congruence: unions happened and collapsed rows.
            Assert.True(egraph.UnionCount > 10, $"only {egraph.UnionCount} unions");
            Assert.True(egraph.TableSize(pair) > 5, $"only {egraph.TableSize(pair)} pairs");
            Assert.True(egraph.TableSize(pair) < ids.Count, "no pair collapsed");
        }

        static void Check(EGraph egraph, ReferenceView reference, (FunctionId Function, ReferenceTable Table)[] functions, List<uint> ids, StringBuilder trace)
        {
            foreach (var id in ids)
            {
                var expected = reference.Find(id);
                var actual = egraph.GetCanonRepr(new Value(id), ColumnTy.Id).Rep;
                if (expected != actual)
                    Assert.Fail($"find({id}) is {actual}, expected {expected}, after:\n{trace}");
            }

            foreach (var (function, table) in functions)
            {
                var expected = reference.Rows(table).Select(r => string.Join(" ", r)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                var actual = Rows(egraph, function).Select(r => string.Join(" ", r.Values.Select(v => v.Rep))).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                if (expected.SequenceEqual(actual) == false)
                    Assert.Fail($"{table.Name} holds [{string.Join("; ", actual)}], expected [{string.Join("; ", expected)}], after:\n{trace}");
            }
        }

    }

}
