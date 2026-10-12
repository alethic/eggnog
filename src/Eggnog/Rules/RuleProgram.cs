using System;

namespace Eggnog
{

    /// <summary>
    /// The engine-independent normal form of a rule (§6.3): its registers, atoms, body steps and head operations, and its
    /// class constants. Immutable once built.
    /// </summary>
    internal sealed class RuleProgram
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The rule's name.</param>
        /// <param name="registers">The registers: body registers first, then head registers.</param>
        /// <param name="atoms">The atoms, in flattening order: the engine's variant order.</param>
        /// <param name="steps">The body steps, in a valid placement order.</param>
        /// <param name="head">The head operations, in builder-call order.</param>
        /// <param name="writtenIdConstants">The class constants' cells as written, by <see cref="OperandKind.IdConstant"/>
        /// index.</param>
        /// <param name="idConstantOrigins">The class constants' origins (D39), same indexing.</param>
        /// <param name="namedRegisters">The registers of the declared body variables, then of the <c>Make</c> variables;
        /// -1 for a variable bound to a constant or unused.</param>
        /// <param name="writesAnalysis">Whether the head writes an analysis table.</param>
        public RuleProgram(string name, ProgramRegister[] registers, ProgramAtom[] atoms, ProgramStep[] steps, HeadOp[] head,
            uint[] writtenIdConstants, ScopeOrigin[] idConstantOrigins, int[] namedRegisters, bool writesAnalysis)
        {
            Name = name;
            Registers = registers;
            Atoms = atoms;
            Steps = steps;
            Head = head;
            WrittenIdConstants = writtenIdConstants;
            IdConstantOrigins = idConstantOrigins;
            NamedRegisters = namedRegisters;
            WritesAnalysis = writesAnalysis;
        }

        /// <summary>
        /// Gets the rule's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the registers: body registers first, then head registers.
        /// </summary>
        public ProgramRegister[] Registers { get; }

        /// <summary>
        /// Gets the atoms, in flattening order: the engine's variant order.
        /// </summary>
        public ProgramAtom[] Atoms { get; }

        /// <summary>
        /// Gets the body steps (lets, filters and not-equals), in a valid placement order.
        /// </summary>
        public ProgramStep[] Steps { get; }

        /// <summary>
        /// Gets the head operations, in builder-call order.
        /// </summary>
        public HeadOp[] Head { get; }

        /// <summary>
        /// Gets the class constants' cells as written, by <see cref="OperandKind.IdConstant"/> index.
        /// </summary>
        public uint[] WrittenIdConstants { get; }

        /// <summary>
        /// Gets the class constants' origins (D39), same indexing.
        /// </summary>
        public ScopeOrigin[] IdConstantOrigins { get; }

        /// <summary>
        /// Gets the registers of the declared body variables, then of the <c>Make</c> variables; -1 for a variable bound
        /// to a constant or unused.
        /// </summary>
        public int[] NamedRegisters { get; }

        /// <summary>
        /// Gets whether the head writes an analysis table (invariant A).
        /// </summary>
        public bool WritesAnalysis { get; }

        /// <summary>
        /// Gets the number of body registers: the head registers follow them.
        /// </summary>
        public int BodyRegisterCount
        {
            get
            {
                var count = 0;
                foreach (var register in Registers)
                    if (register.Kind != RegisterKind.Head)
                        count++;

                return count;
            }
        }

        /// <inheritdoc />
        public override string ToString() => $"{Name}: {Atoms.Length} atoms, {Steps.Length} steps, {Head.Length} head operations";

    }

}
