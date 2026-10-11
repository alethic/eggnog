namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The current sizes of a <see cref="PlanInterpreter"/>, as the executor's trie-node subset sizes and
    /// materialization key counts.
    /// </summary>
    readonly struct InterpreterSizes : IStageSizeSource
    {

        readonly PlanInterpreter owner;

        public InterpreterSizes(PlanInterpreter owner) => this.owner = owner;

        public int GetAtomSize(AtomId atom) => owner.AtomSize(atom);

        public int GetMaterializationSize(MatId mat) => owner.MatSize(mat);

    }

}
