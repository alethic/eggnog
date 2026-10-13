using System;
using System.Collections.Generic;

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
            var source = Source(scope, table);
            var columns = table.Columns;
            var width = columns.Length;
            using var cells = new TupleBuffer(width);
            var row = new uint[width];

            // Copied out during the visit, which must not call the engine; canonicalized afterwards, in place, which can
            // only make rows equal in a scope that is not clean.
            source.Engine.ForEach(table.Function, visited =>
            {
                var values = visited.Values;
                for (var i = 0; i < width; i++)
                    row[i] = values[i].Rep;

                cells.Add(row);
            });

            var sorts = new List<int>();
            for (var column = 0; column < width; column++)
                if (columns[column].Kind == ColumnKind.Sort)
                    sorts.Add(column);

            if (scope.Engine.CanonicalizeColumns(cells.Cells, width, sorts.ToArray()))
                cells.Deduplicate();

            cells.SortByContent(columns);
            var keyCount = table.KeyArray.Length;
            var rows = new TableRow[cells.Count];
            for (var i = 0; i < rows.Length; i++)
            {
                var tuple = cells[i];
                var keys = new object[keyCount];
                for (var column = 0; column < keyCount; column++)
                    keys[column] = Decode(scope, columns[column], tuple[column]);

                var output = table.IsRelation ? null : Decode(scope, columns[keyCount], tuple[keyCount]);
                rows[i] = new TableRow(table, keys, output);
            }

            return Array.AsReadOnly(rows);
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
