using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Stages unions into the union-find (rows <c>[a, b, epoch]</c>) and merges, so that a later rebuild has ids to
    /// canonicalize.
    /// </summary>
    sealed class DiffUnionStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="pairs">The pairs of ids to union.</param>
        public DiffUnionStep(uint[][] pairs)
        {
            Pairs = pairs;
        }

        /// <summary>
        /// Gets the pairs of ids to union.
        /// </summary>
        public uint[][] Pairs { get; }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            var batch = new DiffBatch();
            foreach (var pair in Pairs)
                batch.Insert(runner.Program.UnionFind, [pair[0], pair[1], runner.Epoch]);

            runner.Merge(batch, "the unions");
            runner.Advance();
        }

        /// <inheritdoc />
        public override string Format(DiffProgram program) => $"union {string.Join(" ", Pairs.Select(p => $"{p[0]}~{p[1]}"))}";

        /// <inheritdoc />
        public override IEnumerable<DiffStep> Simplify(DiffProgram program)
        {
            if (Pairs.Length > 1)
                for (var i = 0; i < Pairs.Length; i++)
                    yield return new DiffUnionStep(Pairs.Where((_, j) => j != i).ToArray());
        }

    }

}
