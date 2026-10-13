namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Fixed atom and materialization sizes for <see cref="StageOrdering"/> tests.
    /// </summary>
    readonly struct FixedSizes : IStageSizeSource
    {

        readonly int[] atoms;
        readonly int[] mats;

        public FixedSizes(int[] atoms, int[]? mats = null)
        {
            this.atoms = atoms;
            this.mats = mats ?? [];
        }

        public int GetAtomSize(AtomId atom) => atoms[atom.Index];

        public int GetMaterializationSize(MatId mat) => mats[mat.Index];

    }

}
