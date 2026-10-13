using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Stages removals of keys from one table and merges, leaving tombstones behind (and eventually compaction).
    /// </summary>
    sealed class DiffRemoveStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table; not the union-find.</param>
        /// <param name="keys">The keys, present or not.</param>
        public DiffRemoveStep(int table, uint[][] keys)
        {
            Table = table;
            Keys = keys;
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public int Table { get; }

        /// <summary>
        /// Gets the keys.
        /// </summary>
        public uint[][] Keys { get; }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            var batch = new DiffBatch();
            foreach (var key in Keys)
                batch.Remove(Table, key);

            runner.Merge(batch, "the removals");
            runner.Advance();
        }

        /// <inheritdoc />
        public override string Format(DiffProgram program) => $"remove {program.Tables[Table].Name} {string.Join(" ", Keys.Select(TableTestUtil.Show))}";

        /// <inheritdoc />
        public override IEnumerable<DiffStep> Simplify(DiffProgram program)
        {
            if (Keys.Length > 1)
                for (var i = 0; i < Keys.Length; i++)
                    yield return new DiffRemoveStep(Table, Keys.Where((_, j) => j != i).ToArray());
        }

    }

}
