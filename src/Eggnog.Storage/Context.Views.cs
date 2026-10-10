using System;
using System.Collections.Generic;

namespace Eggnog.Storage
{

    public sealed partial class Context
    {

        // Reading a view.
        //
        // A context's view is its own rows, plus every row of its parent's view that it does not shadow. It
        // shadows a row by holding a row with the same key, or because the row mentions a class it has *marked*:
        // a parent class it has merged with another. A marked class's rows have all been copied in, rewritten, so
        // the parent's copies would be stale.
        //
        // An unshadowed parent row needs no rewriting: every identifier in it belongs to a class this context
        // left alone, whose canonical identifier is the parent's. That is what lets a scan pass ancestors' rows
        // through untouched and a lookup ask the parent with the same key.

        /// <summary>
        /// Gets the canonical identifier in this context, without the readability check.
        /// </summary>
        internal ulong FindRaw(ulong id)
        {
            if (parent is not null)
                id = parent.FindRaw(id);

            return LocalFind(id);
        }

        /// <summary>
        /// Maps a parent-canonical identifier (or, at the root, any identifier) through this context's own
        /// union-find. Compresses paths unless the context is frozen, so that frozen contexts are safe to read
        /// from many threads.
        /// </summary>
        ulong LocalFind(ulong id)
        {
            if (local is null)
            {
                var array = forest;
                if (id >= (ulong)array.Length)
                    return id;

                var root = id;
                while (array[root] != root)
                    root = array[root];

                if (frozen == false)
                {
                    while (array[id] != root)
                    {
                        var next = array[id];
                        array[id] = (uint)root;
                        id = next;
                    }
                }

                return root;
            }
            else
            {
                var root = id;
                while (local.TryGetValue(root, out var next))
                    root = next;

                if (frozen == false)
                {
                    while (local.TryGetValue(id, out var next) && next != root)
                    {
                        local[id] = root;
                        id = next;
                    }
                }

                return root;
            }
        }

        /// <summary>
        /// Points a class's canonical identifier at another's.
        /// </summary>
        void SetLocalParent(ulong loser, ulong winner)
        {
            if (local is not null)
            {
                local[loser] = winner;
                return;
            }

            if (loser >= (ulong)forest.Length)
            {
                var length = Math.Max(forest.Length * 2, 64);
                while ((ulong)length <= loser)
                    length *= 2;

                var start = forest.Length;
                Array.Resize(ref forest, length);
                for (var i = start; i < length; i++)
                    forest[i] = (uint)i;
            }

            forest[loser] = (uint)winner;
        }

        /// <summary>
        /// Points every node of the union-find straight at its root, so that nothing is left to compress.
        /// </summary>
        void CompressAll()
        {
            if (local is null)
            {
                for (ulong i = 0; i < (ulong)forest.Length; i++)
                    forest[i] = (uint)LocalFind(i);
            }
            else
            {
                foreach (var key in new List<ulong>(local.Keys))
                    local[key] = LocalFind(key);
            }
        }

        /// <summary>
        /// Rewrites every identifier column of a row to its canonical identifier in this context.
        /// </summary>
        void Canonicalize(Table table, ulong[] row)
        {
            foreach (var column in table.IdColumns)
                if (column < row.Length)
                    row[column] = FindRaw(row[column]);
        }

        RowSet? Own(Table table) => table.Index < rows.Length ? rows[table.Index] : null;

        RowSet OwnOrCreate(Table table)
        {
            if (table.Index >= rows.Length)
                Array.Resize(ref rows, store.Tables.Count);

            return rows[table.Index] ??= new RowSet(table);
        }

        int OwnFind(Table table, ReadOnlySpan<ulong> row) => Own(table)?.Find(row) ?? -1;

        bool MentionsMarked(Table table, ReadOnlySpan<ulong> row)
        {
            if (marked is null || marked.Count == 0)
                return false;

            foreach (var column in table.IdColumns)
                if (column < row.Length && marked.Contains(row[column]))
                    return true;

            return false;
        }

        bool KeyMentionsMarked(Table table, ReadOnlySpan<ulong> row)
        {
            if (marked is null || marked.Count == 0)
                return false;

            foreach (var column in table.KeyIdColumns)
                if (marked.Contains(row[column]))
                    return true;

            return false;
        }

        /// <summary>
        /// Gets whether this context hides a row of its parent's view.
        /// </summary>
        bool Shadows(Table table, ReadOnlySpan<ulong> row) => MentionsMarked(table, row) || OwnFind(table, row) >= 0;

        static bool ShadowedBy(List<Context> viewers, Table table, ReadOnlySpan<ulong> row)
        {
            for (var i = 0; i < viewers.Count; i++)
                if (viewers[i].Shadows(table, row))
                    return true;

            return false;
        }

        /// <summary>
        /// Finds the row of this context's view with the given key, which must be canonical here. The output is
        /// returned as stored, canonical at the level that stored it.
        /// </summary>
        bool LookupView(Table table, ReadOnlySpan<ulong> key, out ulong output)
        {
            if (Own(table) is { } set)
            {
                var slot = set.Find(key);
                if (slot >= 0)
                {
                    output = table.HasOutput ? set.Output(slot) : 0;
                    return true;
                }
            }

            // A key mentioning a marked class can only be held here: every ancestor row with it was copied in.
            if (parent is not null && KeyMentionsMarked(table, key) == false)
                return parent.LookupView(table, key, out output);

            output = 0;
            return false;
        }

        /// <summary>
        /// Visits this level's rows that no viewer below it shadows, then the levels above.
        /// </summary>
        void ScanLevel(Table table, long since, RowAction action, List<Context> viewers)
        {
            if (Own(table) is { } set)
            {
                for (var slot = 0; slot < set.Count; slot++)
                {
                    if (set.IsAlive(slot) == false || set.StampOf(slot) <= since)
                        continue;

                    var row = set.Row(slot);
                    if (ShadowedBy(viewers, table, row) == false)
                        action(row);
                }
            }

            if (parent is not null)
            {
                viewers.Add(this);
                parent.ScanLevel(table, since, action, viewers);
                viewers.RemoveAt(viewers.Count - 1);
            }
        }

        void ScanColumnLevel(Table table, int column, ulong value, long since, RowAction action, List<Context> viewers)
        {
            if (Own(table) is { } set && set.Column(column, value) is { } slots)
            {
                foreach (var slot in slots)
                {
                    if (set.IsAlive(slot) == false || set.StampOf(slot) <= since)
                        continue;

                    var row = set.Row(slot);
                    if (row[column] == value && ShadowedBy(viewers, table, row) == false)
                        action(row);
                }
            }

            // Every ancestor row mentioning a class marked here is shadowed, so there is no need to look.
            if (parent is not null && (table.IsId[column] == false || marked!.Contains(value) == false))
            {
                viewers.Add(this);
                parent.ScanColumnLevel(table, column, value, since, action, viewers);
                viewers.RemoveAt(viewers.Count - 1);
            }
        }

        /// <summary>
        /// Collects copies of the rows of this context's view that mention <paramref name="id"/>, which must be
        /// canonical here, in any identifier column.
        /// </summary>
        void CollectMentioning(Table table, ulong id, List<ulong[]> found, List<Context> viewers)
        {
            if (Own(table) is { } set && set.Uses(id) is { } slots)
            {
                foreach (var slot in slots)
                {
                    if (set.IsAlive(slot) == false)
                        continue;

                    var row = set.Row(slot);
                    if (Mentions(table, row, id) && ShadowedBy(viewers, table, row) == false)
                        found.Add(row.ToArray());
                }
            }

            if (parent is not null && marked!.Contains(id) == false)
            {
                viewers.Add(this);
                parent.CollectMentioning(table, id, found, viewers);
                viewers.RemoveAt(viewers.Count - 1);
            }
        }

        static bool Mentions(Table table, ReadOnlySpan<ulong> row, ulong id)
        {
            foreach (var column in table.IdColumns)
                if (row[column] == id)
                    return true;

            return false;
        }

    }

}
