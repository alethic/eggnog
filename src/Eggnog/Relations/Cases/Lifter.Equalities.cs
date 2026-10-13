namespace Eggnog.Relations
{

    internal sealed partial class Lifter
    {

        /// <summary>
        /// Stages the meet's equalities (colored-contexts spec §11.1, §11.2, I33 completeness of equalities): for every
        /// block of parent ids equal in every live case, the union of its first member with each other member.
        /// </summary>
        /// <param name="seed">The seed case's index in the live cases.</param>
        /// <param name="report">Counts the unions.</param>
        void LiftEqualities(int seed, LiftReport report)
        {
            blocks.Clear();
            ClassPartition.Meet(maps[seed], live, seed, blocks);
            foreach (var block in blocks)
            {
                for (var i = 1; i < block.Length; i++)
                    writes.AddUnion(block[0], block[i]);

                report.Unions += block.Length - 1;
            }

            blocks.Clear();
        }

    }

}
