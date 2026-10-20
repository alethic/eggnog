using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog
{

    /// <summary>
    /// <see cref="Scope.CountRows(TableHandle)"/> and <see cref="Scope.Rows(TableHandle)"/>: a table's rows in a
    /// scope's view, counted or decoded. Called under the scope's guard; never interns and never creates terms.
    /// </summary>
    internal static class RowReader
    {

        /// <summary>
        /// Counts the rows of <paramref name="table"/> in <paramref name="scope"/>'s view. O(1).
        /// </summary>
        /// <param name="scope">The scope read.</param>
        /// <param name="table">The table.</param>
        /// <returns>The row count.</returns>
        /// <exception cref="ArgumentException">The table belongs to another graph.</exception>
        public static long Count(Scope scope, TableHandle table)
        {
            return Source(scope, table).Engine.TableSize(table.Function);
        }

        /// <summary>
        /// Reads the rows of <paramref name="table"/> in <paramref name="scope"/>'s view, decoded (a sort cell as a
        /// <see cref="ClassId"/> canonical in the scope, with its origin) and in content order: keys, then the output, column
        /// by column in each column type's order. O(rows log rows).
        /// </summary>
        /// <param name="scope">The scope read.</param>
        /// <param name="table">The table.</param>
        /// <returns>The rows.</returns>
        /// <exception cref="ArgumentException">The table belongs to another graph.</exception>
        public static IReadOnlyList<TableRow> Read(Scope scope, TableHandle table)
        {
            var columns = table.Columns;
            var keyCount = table.KeyArray.Length;
            var cells = ReadCells(scope, table);
            var rows = new List<TableRow>(cells.Count);
            foreach (var (tuple, subsumed) in cells)
            {
                var keys = new object[keyCount];
                for (var column = 0; column < keyCount; column++)
                    keys[column] = Decode(scope, columns[column], tuple[column]);

                var output = table.IsRelation ? null : Decode(scope, columns[keyCount], tuple[keyCount]);
                rows.Add(new TableRow(table, keys, output, subsumed));
            }

            return rows.AsReadOnly();
        }

        /// <summary>
        /// Reads the rows of <paramref name="table"/> in <paramref name="scope"/>'s view as cells, a class canonical in
        /// the scope, in content order, each with whether it is subsumed: what <see cref="Read"/> decodes, and what
        /// <see cref="Scope.Serialize"/> serializes. O(rows log rows).
        /// </summary>
        /// <param name="scope">The scope read.</param>
        /// <param name="table">The table.</param>
        /// <returns>The rows: their cells (keys, then the output unless the table is a relation) and subsumption.</returns>
        /// <exception cref="ArgumentException">The table belongs to another graph, or is a mirror.</exception>
        public static List<(uint[] Cells, bool Subsumed)> ReadCells(Scope scope, TableHandle table)
        {
            var source = Source(scope, table);
            var columns = table.Columns;
            var width = columns.Length;

            // A subsumable table's tuples carry the subsumption flag in one more cell, encoded as a bool is (0 or 1), so
            // that it sorts last, after the row's content.
            var subsumable = table.Subsumable;
            var cellWidth = subsumable ? width + 1 : width;
            using var cells = new TupleBuffer(cellWidth);
            var row = new uint[cellWidth];

            // Copied out during the visit, which must not call the engine; canonicalized afterwards, in place, which can
            // only make rows equal in a scope that is not clean.
            source.Engine.ForEach(table.Function, visited =>
            {
                var values = visited.Values;
                for (var i = 0; i < width; i++)
                    row[i] = values[i].Rep;

                if (subsumable)
                    row[width] = visited.Subsumed ? 1u : 0u;

                cells.Add(row);
            });

            var sorts = new List<int>();
            for (var column = 0; column < width; column++)
                if (columns[column].Kind == ColumnKind.Sort)
                    sorts.Add(column);

            if (scope.Engine.CanonicalizeColumns(cells.Cells, cellWidth, sorts.ToArray()))
                cells.Deduplicate();

            if (subsumable)
            {
                var order = new ColumnType[cellWidth];
                columns.CopyTo(order, 0);
                order[width] = scope.Graph.Values.TypeOf<bool>();
                cells.SortByContent(order);
            }
            else
            {
                cells.SortByContent(columns);
            }

            var rows = new List<(uint[] Cells, bool Subsumed)>(cells.Count);
            for (var i = 0; i < cells.Count; i++)
            {
                var tuple = cells[i];

                // Two rows that canonicalized to the same content differ only in the flag, which sorts last, so they are
                // adjacent: one row, subsumed if either is, as the merge's combine_subsumed keeps the larger cell.
                if (subsumable && i > 0 && tuple[..width].SequenceEqual(cells[i - 1][..width]))
                {
                    if (tuple[width] != 0)
                        rows[^1] = (rows[^1].Cells, true);

                    continue;
                }

                rows.Add((tuple[..width].ToArray(), subsumable && tuple[width] != 0));
            }

            return rows;
        }

        /// <summary>
        /// Reads the rows of <paramref name="table"/> in <paramref name="scope"/>'s view as cells, a class canonical in
        /// the scope, in the engine's table order, the order of its storage, which egglog's <c>for_each_while</c> walks;
        /// two rows that canonicalize alike are one, at the first's place, subsumed if either is. What
        /// <see cref="Scope.Serialize"/> numbers its rows by. O(rows).
        /// </summary>
        /// <param name="scope">The scope read.</param>
        /// <param name="table">The table.</param>
        /// <returns>The rows: their cells (keys, then the output unless the table is a relation) and subsumption.</returns>
        /// <exception cref="ArgumentException">The table belongs to another graph, or is a mirror.</exception>
        public static List<(uint[] Cells, bool Subsumed)> ReadCellsInTableOrder(Scope scope, TableHandle table)
        {
            var source = Source(scope, table);
            var columns = table.Columns;
            var width = columns.Length;
            var subsumable = table.Subsumable;
            using var cells = new TupleBuffer(width);
            var subsumed = new List<bool>();
            var row = new uint[width];
            source.Engine.ForEach(table.Function, visited =>
            {
                var values = visited.Values;
                for (var i = 0; i < width; i++)
                    row[i] = values[i].Rep;

                cells.Add(row);
                subsumed.Add(subsumable && visited.Subsumed);
            });

            var sorts = new List<int>();
            for (var column = 0; column < width; column++)
                if (columns[column].Kind == ColumnKind.Sort)
                    sorts.Add(column);

            scope.Engine.CanonicalizeColumns(cells.Cells, width, sorts.ToArray());
            var rows = new List<(uint[] Cells, bool Subsumed)>(cells.Count);
            var seen = new Dictionary<uint[], int>(CellsComparer.Instance);
            for (var i = 0; i < cells.Count; i++)
            {
                var tuple = cells[i].ToArray();
                if (seen.TryGetValue(tuple, out var at))
                {
                    if (subsumed[i])
                        rows[at] = (rows[at].Cells, true);

                    continue;
                }

                seen.Add(tuple, rows.Count);
                rows.Add((tuple, subsumed[i]));
            }

            return rows;
        }

        /// <summary>
        /// Decodes a cell: a class as a <see cref="ClassId"/> with the scope's origin, a value as its CLR value.
        /// </summary>
        static object Decode(Scope scope, ColumnType type, uint cell) =>
            type.Kind == ColumnKind.Sort ? new ClassId(cell, scope.Origin) : type.DecodeObject(cell, scope);

        /// <summary>
        /// Gets the scope whose view of a table is read: the scope itself, or for a scoped view the nearest
        /// ancestor-or-self tagged so, read directly. A table of another graph and a mirror handle are refused.
        /// </summary>
        /// <exception cref="ArgumentException">The table belongs to another graph, or is a mirror.</exception>
        /// <exception cref="InvalidOperationException">A scoped view's tag does not resolve.</exception>
        static Scope Source(Scope scope, TableHandle table)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (ReferenceEquals(table.Graph, scope.Graph) == false)
                throw new ArgumentException($"Table '{table.Name}' belongs to another graph.", nameof(table));

            if (table.IsMirror)
                throw new ArgumentException($"'{table.Name}' is a mirror table, which only the library reads.", nameof(table));

            return table.ReadScope is { } tag ? ScopeResolver.Resolve(scope, tag, $"Reading '{table}'") : scope;
        }

    }

}
