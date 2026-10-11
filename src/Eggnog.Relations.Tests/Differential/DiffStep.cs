using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One step of a differential program (<see cref="DiffProgram"/>). <see cref="Execute"/> applies it to the engine
    /// and the model through the runner and checks what the step can check (matches, change flags); the runner compares
    /// every table after each step.
    /// </summary>
    abstract class DiffStep
    {

        /// <summary>
        /// Applies the step to the engine and the model.
        /// </summary>
        /// <param name="runner">The runner holding both.</param>
        /// <exception cref="DiffMismatchException">The engine disagrees with the model.</exception>
        public abstract void Execute(DiffRunner runner);

        /// <summary>
        /// Formats the step completely, for the listing of a failing program: the program can be rebuilt from it.
        /// </summary>
        public abstract string Format(DiffProgram program);

        /// <summary>
        /// Formats the step for an informational listing of a program that its seed reproduces, which may abbreviate it.
        /// The same as <see cref="Format"/> unless a step overrides it.
        /// </summary>
        public virtual string Summarize(DiffProgram program) => Format(program);

        /// <summary>
        /// Lists smaller variants of the step for the shrinker, most aggressive first.
        /// </summary>
        public virtual IEnumerable<DiffStep> Simplify(DiffProgram program) => Enumerable.Empty<DiffStep>();

    }

}
