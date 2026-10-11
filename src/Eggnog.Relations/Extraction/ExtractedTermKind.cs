namespace Eggnog.Relations
{

    /// <summary>
    /// What an <see cref="ExtractedTerm"/> is.
    /// </summary>
    public enum ExtractedTermKind : byte
    {

        /// <summary>
        /// An e-class, extracted as its chosen node: a function applied to the terms of the node's children.
        /// </summary>
        Node,

        /// <summary>
        /// A base value, a leaf.
        /// </summary>
        Base,

        /// <summary>
        /// A container, applied to the terms of its elements.
        /// </summary>
        Container,

    }

}
