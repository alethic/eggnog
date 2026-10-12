namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A position of a reference atom or step: a variable of the body, or a constant.
    /// </summary>
    internal readonly struct RefSlot
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="variable">The variable's index; -1 for a constant.</param>
        /// <param name="constant">The constant term; <c>null</c> for a variable.</param>
        public RefSlot(int variable, DiffTerm? constant)
        {
            Variable = variable;
            Constant = constant;
        }

        /// <summary>Gets the variable's index; -1 for a constant.</summary>
        public int Variable { get; }

        /// <summary>Gets the constant term; <c>null</c> for a variable.</summary>
        public DiffTerm? Constant { get; }

        /// <summary>Gets whether the slot is a variable.</summary>
        public bool IsVariable => Variable >= 0;

    }

}
