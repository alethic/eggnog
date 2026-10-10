using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Eggnog.Storage.Tests.Reference;

using Xunit;

namespace Eggnog.Storage.Tests.Harness
{

    /// <summary>
    /// A store context and the oracle context that should agree with it, driven in lockstep.
    /// </summary>
    public sealed class Pair
    {

        public static Pair Root(Store store) => new(store.Root, new ReferenceContext(store.Tables, null), null);

        /// <summary>
        /// The operations performed on this thread so far, so that a failing program can be read back.
        /// </summary>
        [ThreadStatic]
        static System.Text.StringBuilder? trace;

        public static string Trace => trace?.ToString() ?? "";

        public static void ResetTrace() => trace = new System.Text.StringBuilder();

        void Note(string operation) => trace?.Append(Name).Append(": ").AppendLine(operation);

        public string Name => Parent is null ? "root" : Parent.Name + "/" + Parent.children.IndexOf(this);

        readonly List<ulong> ownIds = new();
        readonly List<Pair> children = new();
        long lastClock;
        List<(Table Table, ulong[] Row)> lastRows = new();

        Pair(Context store, ReferenceContext reference, Pair? parent)
        {
            Store = store;
            Reference = reference;
            Parent = parent;
            Depth = parent is null ? 0 : parent.Depth + 1;
        }

        public Context Store { get; }

        public ReferenceContext Reference { get; }

        public Pair? Parent { get; }

        public int Depth { get; }

        public IReadOnlyList<Pair> Children => children;

        public List<ulong> VisibleIds()
        {
            var ids = Parent?.VisibleIds() ?? new List<ulong>();
            ids.AddRange(ownIds);
            return ids;
        }

        public Pair CreateChild()
        {
            var child = new Pair(Store.CreateChild(), Reference.CreateChild(), this);
            lock (children)
                children.Add(child);

            child.Note("created");

            return child;
        }

        public void Dispose()
        {
            Note("disposed");
            Store.Dispose();
            lock (Parent!.children)
                Parent.children.Remove(this);
        }

        public ulong NewId()
        {
            var id = Store.NewId().ToValue();
            ownIds.Add(id);
            Note($"new {id}");
            return id;
        }

        public void Union(ulong a, ulong b)
        {
            Note($"union {a} {b}");
            Store.Union(Id.FromValue(a), Id.FromValue(b));
            Reference.Union(a, b);
        }

        public void Insert(Table table, ulong[] key, ulong output)
        {
            Note($"{table.Name}({string.Join(",", key)}) = {output}");
            Store.Insert(table, key, output);
            Reference.Insert(table, key.Append(output).ToArray());
        }

        public void Insert(Table table, ulong[] key)
        {
            Note($"{table.Name}({string.Join(",", key)})");
            Store.Insert(table, key);
            Reference.Insert(table, key);
        }

        /// <summary>
        /// Gets or adds through the store. A new class is told to the oracle as the row it is; an existing one is
        /// checked against the oracle's own answer, which a stale lookup can only ever agree with.
        /// </summary>
        public ulong GetOrAdd(Table table, ulong[] key)
        {
            var id = Store.GetOrAdd(table, key, out var created).ToValue();
            Note($"{table.Name}({string.Join(",", key)}) -> {id}{(created ? " new" : "")}");

            if (created)
            {
                ownIds.Add(id);
                Reference.Insert(table, key.Append(id).ToArray());
            }
            else
            {
                var view = Reference.View();
                Assert.True(view.TryLookup(table, key, out var expected), $"the store found {table.Name}({string.Join(",", key)}) where the oracle has nothing");
                Assert.Equal(view.Find(expected), view.Find(id));
            }

            return id;
        }

        /// <summary>
        /// Rebuilds the store context and asserts it sees what the oracle sees: the same classes over every
        /// identifier it can name, the same rows, the same lookups, and the same rows by column; and that a scan
        /// since the last check returned at least everything that changed.
        /// </summary>
        public void Check()
        {
            Note("check");
            Store.Rebuild();
            var view = Reference.View();
            var ids = VisibleIds();

            var tables = Store.Store.Tables;
            Assert.Equal(Describe(tables, ids, view.Find, t => view.Rows(t)), Describe(tables, ids, id => Store.Find(Id.FromValue(id)).ToValue(), Rows));

            foreach (var table in Store.Store.Tables)
            {
                foreach (var row in view.Rows(table))
                {
                    var key = row.AsSpan(0, table.KeyCount);
                    if (table.HasOutput)
                    {
                        Assert.True(Store.TryLookup(table, key, out var output));
                        Assert.Equal(table.OutputIsId ? view.Find(row[table.KeyCount]) : row[table.KeyCount], table.OutputIsId ? view.Find(output) : output);
                    }
                    else
                    {
                        Assert.True(Store.Contains(table, key));
                    }
                }

                foreach (var row in Rows(table))
                {
                    for (var column = 0; column < table.Arity; column++)
                    {
                        var found = false;
                        Store.ScanColumn(table, column, row[column], r => found |= r.SequenceEqual(row));
                        Assert.True(found, $"{table.Name} by column {column} misses ({string.Join(",", row)})");
                    }
                }
            }

            CheckSince();
        }

        void CheckSince()
        {
            var now = new List<(Table Table, ulong[] Row)>();
            foreach (var table in Store.Store.Tables)
                foreach (var row in Rows(table))
                    now.Add((table, row));

            var before = new HashSet<string>(lastRows.Select(r => Key(r.Table, Canonical(r.Table, r.Row))));

            var since = new HashSet<string>();
            foreach (var table in Store.Store.Tables)
                Store.Scan(table, r => since.Add(Key(table, r.ToArray())), lastClock);

            foreach (var (table, row) in now)
            {
                var key = Key(table, row);
                if (before.Contains(key) == false)
                    Assert.True(since.Contains(key), $"a scan since {lastClock} misses the new row {key}");
            }

            lastClock = Store.Store.Clock;
            lastRows = now;
        }

        ulong[] Canonical(Table table, ulong[] row)
        {
            var copy = (ulong[])row.Clone();
            for (var column = 0; column < copy.Length; column++)
                if (table.IsId[column])
                    copy[column] = Store.Find(Id.FromValue(copy[column])).ToValue();

            return copy;
        }

        static string Key(Table table, ulong[] row) => table.Name + "(" + string.Join(",", row) + ")";

        List<ulong[]> Rows(Table table)
        {
            var list = new List<ulong[]>();
            Store.Scan(table, r => list.Add(r.ToArray()));
            return list;
        }

        /// <summary>
        /// Describes a view in terms that do not depend on which identifier each implementation chose to make
        /// canonical: each class is named by the least identifier in it.
        /// </summary>
        static string Describe(IReadOnlyList<Table> tables, List<ulong> ids, Func<ulong, ulong> find, Func<Table, IEnumerable<ulong[]>> rows)
        {
            var names = new Dictionary<ulong, ulong>();
            foreach (var id in ids)
            {
                var root = find(id);
                names[root] = names.TryGetValue(root, out var name) ? Math.Min(name, id) : id;
            }

            var text = new StringBuilder();
            foreach (var id in ids.OrderBy(i => i))
                if (names[find(id)] != id)
                    text.Append(id).Append("=").Append(names[find(id)]).Append(' ');

            text.AppendLine();

            foreach (var table in tables)
            {
                var lines = new List<string>();
                foreach (var row in rows(table))
                {
                    var cells = new string[row.Length];
                    for (var column = 0; column < row.Length; column++)
                        cells[column] = table.IsId[column] ? "#" + names[find(row[column])] : row[column].ToString();

                    lines.Add(string.Join(",", cells));
                }

                lines.Sort(StringComparer.Ordinal);
                text.Append(table.Name).Append(": ").AppendLine(string.Join(" | ", lines));
            }

            return text.ToString();
        }

    }

}
