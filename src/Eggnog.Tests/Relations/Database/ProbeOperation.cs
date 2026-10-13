using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="ITableOperation"/> that records, for each table it runs on, which tables the view could see at
    /// that moment, and reports a change for the tables in <see cref="Changed"/>.
    /// </summary>
    readonly struct ProbeOperation : ITableOperation
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="watched">The tables whose visibility is recorded.</param>
        /// <param name="changed">The tables the operation reports as changed.</param>
        public ProbeOperation(TableId[] watched, TableId[] changed)
        {
            Watched = watched;
            Changed = changed;
            Log = new ConcurrentBag<(TableId Table, TableId Watched, bool Visible)>();
            Listed = new ConcurrentBag<(TableId Table, TableId[] Visible)>();
            Inconsistencies = new ConcurrentBag<string>();
        }

        /// <summary>
        /// Gets the tables whose visibility is recorded.
        /// </summary>
        public TableId[] Watched { get; }

        /// <summary>
        /// Gets the tables reported as changed.
        /// </summary>
        public TableId[] Changed { get; }

        /// <summary>
        /// Gets what each run saw of the watched tables.
        /// </summary>
        public ConcurrentBag<(TableId Table, TableId Watched, bool Visible)> Log { get; }

        /// <summary>
        /// Gets the table ids the view enumerated during each run.
        /// </summary>
        public ConcurrentBag<(TableId Table, TableId[] Visible)> Listed { get; }

        /// <summary>
        /// Gets the cases where the view's getters disagreed with each other.
        /// </summary>
        public ConcurrentBag<string> Inconsistencies { get; }

        /// <inheritdoc />
        public bool Run(TableId table, TableInfo info, DatabaseView view)
        {
            foreach (var watched in Watched)
            {
                var visible = view.TryGetTableInfo(watched, out _);
                Log.Add((table, watched, visible));

                bool threw;
                try
                {
                    view.GetTable(watched);
                    threw = false;
                }
                catch (KeyNotFoundException)
                {
                    threw = true;
                }

                if (threw == visible)
                    Inconsistencies.Add($"{table}: {watched} visible={visible} but GetTable threw={threw}");
            }

            Listed.Add((table, view.TableIds.ToArray()));
            return System.Array.IndexOf(Changed, table) >= 0;
        }

    }

}
