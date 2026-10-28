namespace Eggnog.Tests.Values
{

    /// <summary>
    /// What one step of a random container program does (<see cref="ContainerProgramStep"/>).
    /// </summary>
    enum ContainerProgramStepKind
    {

        /// <summary>Creates the step's scope as a child of its parent.</summary>
        Create,

        /// <summary>Disposes the step's scope.</summary>
        Dispose,

        /// <summary>Freezes the step's scope, whose parent is frozen or which is the root.</summary>
        Freeze,

        /// <summary>Applies an operation in the step's scope.</summary>
        Operation,

        /// <summary>Records the step's scope's view, which the runner compares with a fresh root's replay.</summary>
        Snapshot,

    }

}
