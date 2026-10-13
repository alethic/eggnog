using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The reference state of a differential program: one <see cref="DiffModelTable"/> per sorted-writes table and a
    /// <see cref="DiffUnionFindModel"/> for the union-find, updated by the same writes as the engine's database and
    /// compared with it after every step.
    /// </summary>
    sealed class DiffModel
    {

        readonly DiffModelTable?[] tables;

        /// <summary>
        /// Initializes the empty state of <paramref name="program"/>.
        /// </summary>
        public DiffModel(DiffProgram program)
        {
            Program = program;
            tables = new DiffModelTable?[program.Tables.Count];
            foreach (var t in program.Tables)
            {
                if (t.IsUnionFind)
                    UnionFind = new DiffUnionFindModel();
                else
                    tables[t.Index] = new DiffModelTable(t);
            }
        }

        /// <summary>
        /// Gets the program.
        /// </summary>
        public DiffProgram Program { get; }

        /// <summary>
        /// Gets the union-find, or <c>null</c> when the program has none.
        /// </summary>
        public DiffUnionFindModel? UnionFind { get; private set; }

        /// <summary>
        /// Gets the model of sorted-writes table <paramref name="table"/>.
        /// </summary>
        public DiffModelTable Table(int table) => tables[table] ?? throw new InvalidOperationException($"Table {table} is the union-find.");

        /// <summary>
        /// Empties <paramref name="table"/>, as <see cref="Database.ClearTable"/> does; for the union-find, forgets every
        /// union.
        /// </summary>
        public void Clear(int table)
        {
            if (tables[table] is { } t)
                tables[table] = new DiffModelTable(t.Table);
            else
                UnionFind = new DiffUnionFindModel();
        }

        /// <summary>
        /// Lists the rows of <paramref name="table"/>; for the union-find, with the canonical column read now.
        /// </summary>
        public List<uint[]> Rows(int table) => tables[table] is { } t ? t.Rows() : UnionFind!.Rows();

        /// <summary>
        /// Looks up the row with key <paramref name="key"/> in sorted-writes table <paramref name="table"/>.
        /// </summary>
        public uint[]? Lookup(int table, uint[] key) => Table(table).Get(key);

        /// <summary>
        /// Applies <paramref name="batch"/> as <see cref="Database.MergeAll"/> would and returns whether anything changed
        /// by the engine's definition: a merge added a key or changed a row, a merge function staged a write, or a union
        /// was effective. Removals alone are not changes.
        /// </summary>
        /// <param name="batch">The staged writes.</param>
        /// <param name="epoch">The timestamp the merge states carry.</param>
        /// <returns>Whether anything changed.</returns>
        public bool Merge(DiffBatch batch, uint epoch)
        {
            var changed = false;
            var unions = new List<uint[]>();
            foreach (var (table, (removals, inserts)) in batch.Tables)
            {
                if (tables[table] is { } t)
                {
                    var staged = false;
                    changed |= t.Apply(removals, inserts, unions, epoch, ref staged);
                    changed |= staged;
                }
                else
                {
                    if (removals.Count > 0)
                        throw new InvalidOperationException("The union-find does not support removals.");

                    unions.AddRange(inserts);
                }
            }

            if (unions.Count > 0)
            {
                var uf = UnionFind ?? throw new InvalidOperationException("A union was staged but the program has no union-find.");
                foreach (var u in unions)
                    changed |= uf.Union(u[0], u[1], u[2]);
            }

            return changed;
        }

        /// <summary>
        /// One pass of <see cref="Database.ApplyRebuild"/> over every rebuilding table: each rewrites, against the
        /// union-find as it is now, the rows whose rebuild columns are not canonical (removing the row's key and
        /// inserting the canonical row stamped with <paramref name="epoch"/>), then everything merges.
        /// </summary>
        /// <param name="epoch">The pass's timestamp.</param>
        /// <returns>Whether the merge changed anything.</returns>
        public bool RebuildPass(uint epoch)
        {
            var uf = UnionFind ?? throw new InvalidOperationException("The program has no union-find.");
            var batch = new DiffBatch();
            foreach (var t in Program.Tables)
            {
                if (t.Rebuilds == false)
                    continue;

                foreach (var row in Table(t.Index).Rows())
                {
                    var rewritten = (uint[])row.Clone();
                    var changed = false;
                    foreach (var c in t.RebuildColumns)
                    {
                        var canonical = uf.Find(row[c]);
                        changed |= canonical != row[c];
                        rewritten[c] = canonical;
                    }

                    if (changed == false)
                        continue;

                    if (t.IsSorted)
                        rewritten[t.TimestampColumn] = epoch;

                    batch.Remove(t.Index, Table(t.Index).KeyOf(row));
                    batch.Insert(t.Index, rewritten);
                }
            }

            return Merge(batch, epoch);
        }

    }

}
