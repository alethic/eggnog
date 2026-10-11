namespace Eggnog.Relations
{

    /// <summary>
    /// A frame of the extractor's iterative term reconstruction: a value to build, and whether its children have been
    /// scheduled. Reconstruction is a post-order walk over an explicit stack, so deep terms cannot overflow the call stack
    /// (egglog recurses, src/extract.rs:503-567).
    /// </summary>
    /// <param name="Sort">The value's sort index in the extractor.</param>
    /// <param name="Value">The value: canonical class id, base value or container id.</param>
    /// <param name="Expanded">Whether the children have been pushed.</param>
    internal readonly record struct ExtractionFrame(int Sort, uint Value, bool Expanded);

}
