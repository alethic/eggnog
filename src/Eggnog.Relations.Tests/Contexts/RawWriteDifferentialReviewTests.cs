using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A review differential for X38 (canonical, stamped writes), §7.6 (root settling) and catch-up branch (a): the
    /// context differential's facade canonicalizes every written id before it writes, so it never exercises raw writes.
    /// Here rows and get-or-add keys are written raw (ids possibly stale) at every level. A child's view must still be
    /// exactly the reference's (every visible id cell canonical, I13), and so must the root's once it is frozen (settled,
    /// I35). The root's own unsettled view is egglog's and is not compared.
    /// </summary>
    public class RawWriteDifferentialReviewTests
    {

        sealed class Node
        {

            public Node(Context context, SpikeReferenceContext reference, Node? parent)
            {
                Context = context;
                Reference = reference;
                Parent = parent;
                Depth = parent is null ? 0 : parent.Depth + 1;
            }

            public Context Context { get; }

            public SpikeReferenceContext Reference { get; }

            public Node? Parent { get; }

            public int Depth { get; }

            public List<uint> Own { get; } = new();

            public List<Node> Children { get; } = new();

            public List<uint> Ids()
            {
                var ids = Parent?.Ids() ?? new List<uint>();
                ids.AddRange(Own);
                return ids;
            }

        }

        public static TheoryData<int> Seeds()
        {
            var data = new TheoryData<int>();
            for (var seed = 0; seed < 300; seed++)
                data.Add(seed);

            return data;
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void RawWritesGiveCanonicalChildViews(int seed)
        {
            var random = new Random(seed);
            var store = new Store(ParallelHeuristics.Serial);
            var schema = new SpikeTestSchema(store, null);
            var root = new Node(store.Root, new SpikeReferenceContext(schema.Tables, null, new HashSet<ulong>()), null);
            var live = new List<Node> { root };
            var trace = new StringBuilder();
            var freezeAt = random.Next(2) == 0 ? random.Next(40, 160) : -1;
            try
            {
                for (var step = 0; step < 200; step++)
                {
                    if (step == freezeAt && root.Context.IsFrozen == false)
                    {
                        trace.AppendLine("root: freeze");
                        root.Context.Freeze();
                        Check(root, schema, trace);
                    }

                    var writable = live.Where(n => n.Context.IsFrozen == false).ToList();
                    if (writable.Count == 0)
                        break;

                    Step(random, writable[random.Next(writable.Count)], live, schema, trace);
                }

                foreach (var node in live)
                    if (node.Parent is not null || node.Context.IsFrozen)
                        Check(node, schema, trace);
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.Message}{Environment.NewLine}{trace}", e);
            }
        }

        static string Name(Node node) => node.Parent is null ? "r" : Name(node.Parent) + "." + node.Parent.Children.IndexOf(node);

        static void Step(Random random, Node node, List<Node> live, SpikeTestSchema schema, StringBuilder trace)
        {
            var ids = node.Ids();
            uint Pick() => ids[random.Next(ids.Count)];
            var roll = random.Next(100);
            var name = Name(node);
            if (ids.Count < 2 || (roll < 10 && ids.Count < 14))
            {
                var id = node.Context.NewId();
                node.Own.Add(id);
                trace.AppendLine($"{name}: new {id}");
                return;
            }

            switch (roll)
            {
                case < 28:
                {
                    var (a, b) = (Pick(), Pick());
                    trace.AppendLine($"{name}: union {a} {b}");
                    node.Context.Union(a, b);
                    node.Reference.Union(a, b);
                    break;
                }
                case < 44:
                    GetOrAdd(node, schema.Pair, [Pick(), Pick()], trace);
                    break;
                case < 50:
                    GetOrAdd(node, schema.Wrap, [Pick()], trace);
                    break;
                case < 58:
                    RawInsert(node, schema.Pair, [Pick(), Pick(), Pick()], trace);
                    break;
                case < 66:
                    RawInsert(node, schema.Low, [Pick(), (uint)random.Next(10)], trace);
                    break;
                case < 72:
                    RawInsert(node, schema.Tag, [Pick(), (uint)random.Next(3)], trace);
                    break;
                case < 76:
                    GetOrAdd(node, schema.Named, [(uint)random.Next(4)], trace);
                    break;
                case < 86:
                    if (node.Depth < 3 && live.Count < 8)
                    {
                        trace.AppendLine($"{name}: child");
                        var child = new Node(node.Context.CreateChild(), node.Reference.CreateChild(ids.Select(i => (ulong)i)), node);
                        node.Children.Add(child);
                        live.Add(child);
                    }

                    break;
                case < 90:
                    trace.AppendLine($"{name}: rebuild");
                    node.Context.Rebuild();
                    break;
                case < 94:
                    foreach (var other in live.ToArray())
                        if (other.Parent is not null || other.Context.IsFrozen)
                            Check(other, schema, trace);

                    break;
                case < 97:
                    if (node.Parent is not null && node.Parent.Context.IsFrozen)
                    {
                        trace.AppendLine($"{name}: freeze");
                        node.Context.Freeze();
                        Check(node, schema, trace);
                    }

                    break;
                default:
                    if (node.Parent is not null && node.Children.Count == 0)
                    {
                        trace.AppendLine($"{name}: dispose");
                        node.Context.Dispose();
                        node.Parent.Children.Remove(node);
                        live.Remove(node);
                    }

                    break;
            }
        }

        static void RawInsert(Node node, SpikeTable table, uint[] row, StringBuilder trace)
        {
            trace.AppendLine($"{Name(node)}: raw {table.Name}({string.Join(",", row)})");
            node.Context.Write(state =>
            {
                var cells = new uint[table.EngineArity];
                Array.Copy(row, cells, row.Length);
                cells[table.Arity] = state.Timestamp;
                state.StageInsert(table.Id, cells);
                return 0;
            });
            node.Reference.Insert(table, row.Select(c => (ulong)c).ToArray());
        }

        static void GetOrAdd(Node node, SpikeTable table, uint[] key, StringBuilder trace)
        {
            var store = node.Context.Store;
            var (output, made) = node.Context.Write(state =>
            {
                var before = state.PredictedCount;
                MergeVal[] values = [MergeVal.FromCounter(store.IdCounter), MergeVal.FromConstant(new Value(state.Timestamp))];
                var row = state.PredictRow(table.Id, key, values);
                return (row[table.KeyCount], state.PredictedCount > before);
            });

            trace.AppendLine($"{Name(node)}: raw {table.Name}({string.Join(",", key)}) -> {output}{(made ? " new" : "")}");
            if (made)
            {
                node.Own.Add(output);
                node.Reference.Insert(table, key.Select(c => (ulong)c).Append(output).ToArray());
            }
        }

        static void Check(Node node, SpikeTestSchema schema, StringBuilder trace)
        {
            var context = node.Context;
            trace.AppendLine($"{Name(node)}: check");
            if (context.IsFrozen == false)
            {
                context.Rebuild();
                context.CheckInvariants();
            }

            var view = node.Reference.View();
            var ids = node.Ids();
            foreach (var id in ids)
                Assert.True(view.Find(id) == context.Find(id), $"{Name(node)}: find({id}) is {context.Find(id)}, the reference says {view.Find(id)}");

            foreach (var table in schema.Tables)
            {
                var actual = new List<string>();
                foreach (var (_, _, cells) in LayeredTestUtil.ScanVisible(context.GetView(table.Id)))
                {
                    for (var c = 0; c < table.Arity; c++)
                        if (table.IsId[c])
                            Assert.True(context.Find(cells[c]) == cells[c], $"{Name(node)}: {table.Name} holds the non-canonical id {cells[c]} (I13): ({string.Join(",", cells)})");

                    actual.Add(string.Join(",", cells.Take(table.Arity)));
                }

                var expected = view.Rows(table).Select(r => string.Join(",", r)).ToList();
                actual.Sort(StringComparer.Ordinal);
                expected.Sort(StringComparer.Ordinal);
                Assert.True(expected.SequenceEqual(actual), $"{Name(node)}: {table.Name} is [{string.Join(" | ", actual)}], the reference [{string.Join(" | ", expected)}]");
            }

            LookupPaths.Check(context);
        }

    }

}
