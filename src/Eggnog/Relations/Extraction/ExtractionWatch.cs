using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Tells whether what an <see cref="Extractor"/> reads changed since it was created: the rows of its functions'
    /// tables and the union-find, at every level of its context's chain that was not frozen when it was created. Never
    /// the clock, so a rule run, a query or a write batch that changes nothing keeps an extractor current.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The watched levels are the context itself and its ancestors up to the first frozen one, deepest first. Frozen
    /// levels form a prefix of the chain (a context freezes only under a frozen parent) and never change, so they are not
    /// watched; a watched level that freezes later is still watched, at no risk, since a frozen level stops changing. A
    /// context frozen when the extractor is created watches nothing, so any number of extractors may run on it from any
    /// threads without touching shared state.
    /// </para>
    /// <para>
    /// Per watched level it records each table's <see cref="ExtractionTableMark"/> (the root's table at depth 0, the
    /// level's own layer in a child) and the union-find table's <see cref="TableVersion"/>: the root's
    /// <see cref="DisplacedTable"/>, whose row count grows with every union, or a child's journal, which grows with every
    /// own union and every parent displacement it imports. Every change a level can make to what the extractor reads moves
    /// one of them: rows merged in, rewritten or deleted; hides and clears; compaction; unions. Changes to other tables do
    /// not.
    /// </para>
    /// <para>
    /// Cost: creating it reads O(levels × tables) versions; <see cref="FindChange"/> reads them again, O(levels × tables),
    /// once per extraction query, never per node. Immutable after construction; <see cref="FindChange"/> must run where
    /// the watched levels may be read (on their thread domain while they are not frozen).
    /// </para>
    /// </remarks>
    internal sealed class ExtractionWatch
    {

        /// <summary>
        /// The result of <see cref="FindChange"/> when the union-find changed.
        /// </summary>
        public const int UnionFindChanged = -2;

        /// <summary>
        /// The result of <see cref="FindChange"/> when nothing changed.
        /// </summary>
        public const int NoChange = -1;

        readonly Context[] levels;
        readonly TableId[] tables;
        readonly ExtractionTableMark[] marks;
        readonly TableVersion[] unionFinds;

        /// <summary>
        /// Records the current state of <paramref name="tables"/> and of the union-find at every level of
        /// <paramref name="context"/>'s chain that is not frozen.
        /// </summary>
        /// <param name="context">The context the extractor reads.</param>
        /// <param name="tables">The tables it reads, one per extractor slot; copied.</param>
        public ExtractionWatch(Context context, ReadOnlySpan<TableId> tables)
        {
            ArgumentNullException.ThrowIfNull(context);

            var count = 0;
            for (var level = context; level is not null && level.IsFrozen == false; level = level.Parent)
                count++;

            levels = new Context[count];
            var at = 0;
            for (var level = context; at < count; level = level.Parent!)
                levels[at++] = level;

            this.tables = tables.ToArray();
            marks = new ExtractionTableMark[count * tables.Length];
            unionFinds = new TableVersion[count];
            for (var l = 0; l < count; l++)
            {
                var level = levels[l];
                unionFinds[l] = level.UnionFindTable.Version;
                for (var t = 0; t < tables.Length; t++)
                    marks[l * tables.Length + t] = Mark(level, tables[t]);
            }
        }

        /// <summary>
        /// Gets the number of levels watched: 0 when the context was frozen.
        /// </summary>
        public int LevelCount => levels.Length;

        /// <summary>
        /// Finds a change since the watch was created: <see cref="UnionFindChanged"/> when a watched level's union-find
        /// grew, else the index of a table that changed at a watched level, else <see cref="NoChange"/>. The union-find is
        /// checked first at each level. O(levels × tables); nothing for a frozen context.
        /// </summary>
        /// <returns>The change found, or <see cref="NoChange"/>.</returns>
        public int FindChange()
        {
            var width = tables.Length;
            for (var l = 0; l < levels.Length; l++)
            {
                var level = levels[l];
                if (level.UnionFindTable.Version != unionFinds[l])
                    return UnionFindChanged;

                var row = l * width;
                for (var t = 0; t < width; t++)
                    if (Mark(level, tables[t]) != marks[row + t])
                        return t;
            }

            return NoChange;
        }

        /// <summary>
        /// Gets the mark of <paramref name="table"/> at <paramref name="level"/>: the root's table, or the child's own
        /// layer (<c>default</c> when it has none). O(1).
        /// </summary>
        static ExtractionTableMark Mark(Context level, TableId table)
        {
            if (level.Tables is not { } layers)
            {
                var root = level.Database.GetTable(table);
                var version = root.Version;
                return new ExtractionTableMark(version.Major, version.Minor, root.Count, 0);
            }

            if (layers.TryGetLayer(table) is not { } layer)
                return default;

            var own = layer.Own;
            var ownVersion = own.Version;
            return new ExtractionTableMark(ownVersion.Major, ownVersion.Minor, own.Count, layer.Version);
        }

    }

}
