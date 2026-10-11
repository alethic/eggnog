namespace Eggnog.Relations
{

    /// <summary>
    /// Binds the current value of <see cref="Counter"/> to <see cref="Destination"/> in every lane, live or not. egglog's
    /// <c>Instr::ReadCounter</c> (core-relations/src/action/mod.rs:982-988, 875-880): the bridge reads its timestamp
    /// counter first in every rule.
    /// </summary>
    internal sealed class ReadCounterInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <param name="destination">The variable defined.</param>
        public ReadCounterInstr(CounterId counter, Variable destination)
            : base(InstrKind.ReadCounter)
        {
            Counter = counter;
            Destination = CheckDestination(destination, nameof(destination));
        }

        /// <summary>
        /// Gets the counter.
        /// </summary>
        public CounterId Counter { get; }

        /// <summary>
        /// Gets the variable defined.
        /// </summary>
        public Variable Destination { get; }

        /// <inheritdoc />
        public override string ToString() => Destination + " <- ReadCounter " + Counter;

    }

}
