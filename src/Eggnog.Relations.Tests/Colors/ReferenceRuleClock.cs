namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The logical clock and id counter one program's <see cref="ReferenceRuleContext"/>s share (colored-contexts spec
    /// §17 M10.3): every assertion, rule run and child creation takes the next tick, in the order the harness performs
    /// it on the engine, so tick order is the engine's epoch order wherever a watermark compares them; the reference's
    /// minted ids (family B) come from the counter, far from the engine's.
    /// </summary>
    sealed class ReferenceRuleClock
    {

        /// <summary>
        /// The first id the reference mints.
        /// </summary>
        public const ulong FirstId = 1UL << 32;

        ulong nextId = FirstId;

        /// <summary>
        /// Gets the last tick taken; 0 before the first.
        /// </summary>
        public long Now { get; private set; }

        /// <summary>
        /// Gets or sets the number of rule runs in children whose watermark was inherited from an ancestor (a coverage
        /// count).
        /// </summary>
        public long InheritedRuns { get; set; }

        /// <summary>
        /// Takes the next tick.
        /// </summary>
        public long Tick() => ++Now;

        /// <summary>
        /// Mints a reference id.
        /// </summary>
        public ulong MintId() => nextId++;

    }

}
