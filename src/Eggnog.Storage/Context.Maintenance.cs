using System;
using System.Collections.Generic;

namespace Eggnog.Storage
{

    public sealed partial class Context
    {

        // Keeping a view closed.
        //
        // Two invariants hold whenever a context is clean:
        //
        //  1. Every row it holds is canonical: each identifier is a root of its union-find, and no two live rows
        //     share a key.
        //  2. Every row of its parent's view that mentions a marked class, or shares a key with one of its own,
        //     has a rewritten copy here. That is what makes shadowing safe.
        //
        // A merge here marks both classes, copying their parent rows in (2), then queues the losing identifier so
        // that rows mentioning it are rewritten (1). Rewriting may make two rows share a key, which queues a merge
        // of their outputs: congruence. Rebuild runs the two queues until both are empty.
        //
        // When an ancestor changes, its log says what changed, and Sync brings this context back in line: an
        // ancestor's merge may make two of this context's parent identifiers one (Rekey), and an ancestor's new row
        // may fall under (2), in which case a copy is taken.

        /// <summary>
        /// Brings ancestors up to date and takes in what they changed, so that this context may be written.
        /// </summary>
        void Prepare()
        {
            if (parent is null)
                return;

            if (parent.frozen == false)
                parent.Rebuild();

            Sync();
        }

        void Sync()
        {
            var merged = false;

            for (var i = 0; i < ancestors.Length; i++)
            {
                var ancestor = ancestors[i];
                for (var k = cursors[i]; k < ancestor.LogEnd; k++)
                {
                    var entry = ancestor.log[(int)(k - ancestor.logBase)];
                    if (entry.IsUnion)
                    {
                        merged = true;
                        losers.Enqueue(entry.Loser);
                    }
                }
            }

            if (merged)
                Rekey();

            for (var i = 0; i < ancestors.Length; i++)
            {
                var ancestor = ancestors[i];
                for (var k = cursors[i]; k < ancestor.LogEnd; k++)
                {
                    var entry = ancestor.log[(int)(k - ancestor.logBase)];
                    if (entry.IsUnion == false)
                        Absorb(i, store.Tables[entry.Table], entry.Slot);
                }

                cursors[i] = ancestor.LogEnd;
            }
        }

        /// <summary>
        /// Takes in a row an ancestor added or changed, if this context needs its own copy of it.
        /// </summary>
        void Absorb(int level, Table table, int slot)
        {
            var set = ancestors[level].Own(table)!;
            if (set.IsAlive(slot) == false)
                return;

            var row = set.Row(slot).ToArray();

            // A level in between that shadows the row has taken its own copy, and logged that instead.
            for (var i = level + 1; i < ancestors.Length; i++)
                if (ancestors[i].Shadows(table, row))
                    return;

            if (MentionsMarked(table, row) || OwnFind(table, row) >= 0)
            {
                Canonicalize(table, row);
                AddOrMerge(table, row, materialize: true);
            }
        }

        /// <summary>
        /// Re-keys the union-find after an ancestor merged classes. Its nodes are parent-canonical identifiers, and
        /// some may no longer be; two of its classes may now share a parent class and become one.
        /// </summary>
        void Rekey()
        {
            if (local!.Count == 0 && marked!.Count == 0)
                return;

            var nodes = new HashSet<ulong>(local.Keys);
            nodes.UnionWith(local.Values);
            nodes.UnionWith(marked!);

            // Group the nodes' current parent identities: two nodes in one class here, or with one identity in
            // the parent now, end up in one group.
            var groups = new Dictionary<ulong, ulong>();
            foreach (var node in nodes)
            {
                var root = node;
                while (local.TryGetValue(root, out var next))
                    root = next;

                Join(groups, parent!.FindRaw(node), parent.FindRaw(root));
            }

            var members = new Dictionary<ulong, List<ulong>>();
            foreach (var identity in new List<ulong>(groups.Keys))
            {
                var root = Lead(groups, identity);
                if (members.TryGetValue(root, out var list) == false)
                    members[root] = list = new List<ulong>();

                list.Add(identity);
            }

            var wasMarked = marked!;
            var nowMarked = new HashSet<ulong>();
            foreach (var node in wasMarked)
                nowMarked.Add(parent!.FindRaw(node));

            local.Clear();
            foreach (var group in members.Values)
            {
                var winner = group[0];
                foreach (var identity in group)
                    winner = Math.Min(winner, identity);

                foreach (var identity in group)
                {
                    if (identity != winner)
                        local[identity] = winner;

                    if (group.Count > 1)
                        nowMarked.Add(identity);
                }
            }

            marked = nowMarked;

            foreach (var identity in nowMarked)
                if (wasMarked.Contains(identity) == false)
                    Materialize(identity);

            // Rows held here may mention an old node that is no longer a parent identifier, or a parent identifier
            // that has just joined one of this context's classes without being its root. Either has stopped being
            // canonical here, which children learn the way they learn of any merge: from the log.
            var retired = new HashSet<ulong>();
            foreach (var node in nodes)
                if (FindRaw(node) != node)
                    retired.Add(node);

            retired.UnionWith(local.Keys);

            foreach (var identity in retired)
            {
                losers.Enqueue(identity);
                Log(new LogEntry(true, 0, 0, identity));
            }
        }

        static void Join(Dictionary<ulong, ulong> groups, ulong a, ulong b)
        {
            if (groups.ContainsKey(a) == false)
                groups[a] = a;
            if (groups.ContainsKey(b) == false)
                groups[b] = b;

            a = Lead(groups, a);
            b = Lead(groups, b);
            if (a != b)
                groups[Math.Max(a, b)] = Math.Min(a, b);
        }

        static ulong Lead(Dictionary<ulong, ulong> groups, ulong a)
        {
            while (groups[a] != a)
                a = groups[a];

            return a;
        }

        bool UnionCore(ulong a, ulong b)
        {
            while (true)
            {
                var ra = FindRaw(a);
                var rb = FindRaw(b);
                if (ra == rb)
                    return false;

                // Marking copies rows in, which may queue merges but never performs one, so the roots cannot move
                // underneath; recomputing them is cheap insurance all the same.
                if (marked is not null && (MarkMerged(ra) | MarkMerged(rb)))
                    continue;

                var winner = Math.Min(ra, rb);
                var loser = Math.Max(ra, rb);
                SetLocalParent(loser, winner);
                losers.Enqueue(loser);
                Log(new LogEntry(true, 0, 0, loser));
                return true;
            }
        }

        /// <summary>
        /// Marks a parent class as merged here, copying in every row of the parent's view that mentions it.
        /// </summary>
        bool MarkMerged(ulong node)
        {
            if (marked!.Add(node) == false)
                return false;

            Materialize(node);
            return true;
        }

        void Materialize(ulong node)
        {
            var found = new List<ulong[]>();

            foreach (var table in store.Tables)
            {
                if (table.IdColumns.Length == 0)
                    continue;

                found.Clear();
                parent!.CollectMentioning(table, node, found, new List<Context>());

                foreach (var row in found)
                {
                    Canonicalize(table, row);
                    AddOrMerge(table, row, materialize: true);
                }
            }
        }

        /// <summary>
        /// Adds a canonical row, or combines it with the row that has its key.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="row">The row, canonical in this context.</param>
        /// <param name="materialize">
        /// Whether the row is a copy of one this context must hold for itself. Otherwise it is a new assertion,
        /// and a row already visible from the parent with the same key absorbs it without a copy where it can.
        /// </param>
        void AddOrMerge(Table table, ulong[] row, bool materialize)
        {
            var set = OwnOrCreate(table);
            var slot = set.Find(row);
            if (slot >= 0)
            {
                MergeInto(table, set, slot, row);
                return;
            }

            // A row here with this key would hide the parent's row with it, so whatever the parent's row says has to
            // be folded in first.
            if (parent is not null && KeyMentionsMarked(table, row) == false && parent.LookupView(table, row, out var existing))
            {
                if (table.HasOutput == false)
                {
                    if (materialize == false)
                        return;
                }
                else if (table.OutputIsId)
                {
                    if (existing != row[table.KeyCount])
                        unions.Enqueue((existing, row[table.KeyCount]));

                    // A new assertion leaves the parent's row visible; the merge marks its class, which copies the
                    // row in anyway.
                    if (materialize == false)
                        return;
                }
                else
                {
                    var combined = table.MergeValues(existing, row[table.KeyCount]);
                    if (materialize == false && combined == existing)
                        return;

                    row[table.KeyCount] = combined;
                }
            }

            slot = set.Add(row, store.Tick());
            Log(new LogEntry(false, table.Index, slot, 0));
        }

        void MergeInto(Table table, RowSet set, int slot, ulong[] row)
        {
            if (table.HasOutput == false)
                return;

            var current = set.Output(slot);
            var incoming = row[table.KeyCount];

            if (table.OutputIsId)
            {
                if (current != incoming)
                    unions.Enqueue((current, incoming));

                return;
            }

            var combined = table.MergeValues(current, incoming);
            if (combined != current)
            {
                set.SetOutput(slot, combined, store.Tick());
                Log(new LogEntry(false, table.Index, slot, 0));
            }
        }

        void ProcessPending()
        {
            ProcessQueues();
            Tidy();
        }

        void ProcessQueues()
        {
            while (true)
            {
                if (unions.Count > 0)
                {
                    var (a, b) = unions.Dequeue();
                    UnionCore(a, b);
                    continue;
                }

                if (losers.Count > 0)
                {
                    Recanonicalize(losers.Dequeue());
                    continue;
                }

                break;
            }
        }

        /// <summary>
        /// Rewrites the rows held here that mention an identifier that is no longer canonical.
        /// </summary>
        void Recanonicalize(ulong id)
        {
            if (FindRaw(id) == id)
                return;

            foreach (var table in store.Tables)
            {
                if (table.IdColumns.Length == 0 || Own(table) is not { } set || set.Uses(id) is not { } list)
                    continue;

                var slots = list.ToArray();
                set.ForgetUses(id);

                foreach (var slot in slots)
                {
                    if (set.IsAlive(slot) == false || Mentions(table, set.Row(slot), id) == false)
                        continue;

                    var row = set.Row(slot).ToArray();
                    set.Kill(slot);
                    Canonicalize(table, row);
                    AddOrMerge(table, row, materialize: true);
                }
            }
        }

        void Log(LogEntry entry)
        {
            if (readers.Count > 0)
                log.Add(entry);
        }

        /// <summary>
        /// Gets the position one past the last log entry. Positions are absolute: they keep counting from where
        /// the log started however much of its front has been discarded.
        /// </summary>
        long LogEnd => logBase + log.Count;

        /// <summary>
        /// Gets the number of log entries still held, for tests.
        /// </summary>
        internal int LogLength => log.Count;

        /// <summary>
        /// Gets the number of slots, live or dead, held for a table, for tests.
        /// </summary>
        internal int SlotsHeld(Table table) => Own(table)?.Count ?? 0;

        /// <summary>
        /// Discards the log entries every reader has taken in, and once nothing in the log remains unread,
        /// compacts tables that are mostly dead slots.
        /// </summary>
        /// <remarks>
        /// A log entry names a row by its slot, so slots may be renumbered only when no entry is left to name
        /// one. A reader that is not rebuilt holds the log back, and with it compaction.
        /// </remarks>
        void Tidy()
        {
            var consumed = LogEnd;
            lock (readers)
                foreach (var (reader, level) in readers)
                    consumed = Math.Min(consumed, reader.cursors[level]);

            if (consumed > logBase)
            {
                log.RemoveRange(0, (int)(consumed - logBase));
                logBase = consumed;
            }

            if (log.Count > 0)
                return;

            foreach (var set in rows)
                if (set is not null && set.ShouldCompact)
                    set.Compact();
        }

    }

}
