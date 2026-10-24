using System.Collections.Generic;
using System.Diagnostics;

namespace Eggnog.Relations
{

    internal sealed partial class Lifter
    {

        /// <summary>
        /// The parent ids of the blocks the current round withheld (<see cref="LiftEqualities"/>): container ids, whose
        /// equality the parent would not hold for its containers. <see cref="TryCommonParentId"/> passes over them, so
        /// no row whose output rests on such an equality is lifted.
        /// </summary>
        readonly HashSet<uint> containerMerged = new();

        /// <summary>
        /// Stages the meet's equalities: for every block of parent ids equal in every live case, the union of its first
        /// member with each other member. Every equality between parent ids that holds in every live case is in some
        /// block.
        /// </summary>
        /// <remarks>
        /// A block of container ids in the parent's view (<see cref="ContainerView.IsContainerId"/>) is withheld, and
        /// its members recorded in <see cref="containerMerged"/>: such ids become equal in a case only because the case
        /// rebuilt the containers' contents equal, and their union in the parent would equate containers whose contents
        /// the parent keeps apart. A block never mixes container ids with classes, since a union of a container's id
        /// with a class's is no rebuild's (asserted in DEBUG). The test runs only while a registered container type
        /// holds ids: a container of values never changes, so its id joins no case's union.
        /// </remarks>
        /// <param name="parent">The parent lifted into; clean.</param>
        /// <param name="seed">The seed case's index in the live cases.</param>
        /// <param name="report">Counts the unions.</param>
        void LiftEqualities(EGraphContext parent, int seed, LiftReport report)
        {
            blocks.Clear();
            containerMerged.Clear();
            ClassPartition.Meet(maps[seed], live, seed, blocks);
            var database = parent.Context.Database;
            var view = database.Containers.HasEqContainers ? ContainerView.Of(database) : default;
            foreach (var block in blocks)
            {
                if (view.Values is not null && view.IsContainerId(block[0]))
                {
                    foreach (var member in block)
                    {
                        Debug.Assert(view.IsContainerId(member), $"the lift's block of {block[0]} mixes container ids with classes");
                        containerMerged.Add(member);
                    }

                    continue;
                }

                AssertNoContainerIds(view, block);
                for (var i = 1; i < block.Length; i++)
                    writes.AddUnion(block[0], block[i]);

                report.Unions += block.Length - 1;
            }

            blocks.Clear();
        }

        /// <summary>
        /// DEBUG builds: asserts that no member of a block of classes is a container id in the parent's view.
        /// </summary>
        [Conditional("DEBUG")]
        static void AssertNoContainerIds(ContainerView view, uint[] block)
        {
            if (view.Values is null)
                return;

            foreach (var member in block)
                Debug.Assert(view.IsContainerId(member) == false, $"the lift's block of {block[0]} mixes classes with container ids");
        }

    }

}
