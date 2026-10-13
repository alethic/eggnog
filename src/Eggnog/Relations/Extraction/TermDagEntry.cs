namespace Eggnog.Relations
{

    /// <summary>
    /// An entry of a <see cref="TermDag"/>'s hash-cons index: a term and the hash of its contents, kept so that growing
    /// the index never rehashes a term.
    /// </summary>
    /// <param name="Hash">The hash of the term's contents.</param>
    /// <param name="Term">The term's index in the DAG.</param>
    internal readonly record struct TermDagEntry(ulong Hash, int Term);

}
