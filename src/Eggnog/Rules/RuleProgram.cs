using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The engine-independent normal form of a rule: its registers, atoms, body steps and head operations, and its class
    /// constants. Immutable once built.
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
        /// <param name="idConstantOrigins">The class constants' origins (the scopes that returned them), same
        /// indexing.</param>
        /// <param name="namedRegisters">The registers of the declared body variables, then of the <c>Make</c> variables;
        /// -1 for a variable bound to a constant or unused.</param>
        /// <param name="writesAnalysis">Whether the head writes an analysis table.</param>
        /// <param name="readScopes">The distinct tags the scoped atoms read, in first-use order.</param>
        public RuleProgram(string name, ProgramRegister[] registers, ProgramAtom[] atoms, ProgramStep[] steps, HeadOp[] head,
            uint[] writtenIdConstants, ScopeOrigin[] idConstantOrigins, int[] namedRegisters, bool writesAnalysis, ScopeTag[] readScopes)
        {
            ReadScopes = readScopes;
            Name = name;
            Registers = registers;
            Atoms = atoms;
            Steps = steps;
            Head = head;
            WrittenIdConstants = writtenIdConstants;
            IdConstantOrigins = idConstantOrigins;
            NamedRegisters = namedRegisters;
            WritesAnalysis = writesAnalysis;
            List<TableHandle>? deletes = null;
            foreach (var op in head)
            {
                if (op.Kind is HeadOpKind.Write or HeadOpKind.Delete && op.Table is FunctionHandle { Merge: not null } function)
                    RootOnlyWrite ??= function;

                if (op.Kind == HeadOpKind.Compute && op.Primitive is { IsWrite: true } primitive)
                    WritePrimitive ??= primitive;

                // A scheduler's action rule deletes its worklist row, which is context-private bookkeeping.
                if (op.Kind == HeadOpKind.Delete && op.Table!.IsWorklist == false && (deletes ??= new()).Contains(op.Table) == false)
                    deletes.Add(op.Table);
            }

            Deletes = deletes?.ToArray() ?? Array.Empty<TableHandle>();
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
        /// Gets the class constants' origins (the scopes that returned them), same indexing.
        /// </summary>
        public ScopeOrigin[] IdConstantOrigins { get; }

        /// <summary>
        /// Gets the registers of the declared body variables, then of the <c>Make</c> variables; -1 for a variable bound
        /// to a constant or unused.
        /// </summary>
        public int[] NamedRegisters { get; }

        /// <summary>
        /// Gets the distinct tags the scoped atoms read, in first-use order: the tags whose mirrors are synced before the
        /// program runs and that must resolve from the running scope.
        /// </summary>
        public ScopeTag[] ReadScopes { get; }

        /// <summary>
        /// Gets the first function the head writes or deletes from whose merge is not a lattice
        /// (<see cref="FunctionMerge"/>), which only the root may write, so that the program runs only there; <c>null</c>
        /// when there is none.
        /// </summary>
        public FunctionHandle? RootOnlyWrite { get; }

        /// <summary>
        /// Gets the first write primitive the head computes (<see cref="Primitive.IsWrite"/>), egglog's
        /// <c>vec-union</c>, which unions classes that containers hold, which only the root rebuilds, so that the program
        /// runs only there; <c>null</c> when there is none.
        /// </summary>
        public Primitive? WritePrimitive { get; }

        /// <summary>
        /// Gets the distinct tables the head deletes rows of, in first-use order: the program runs only where the scope
        /// may delete, and a run reconciles the scope's mirrors of them under its own tag after each iteration.
        /// </summary>
        public TableHandle[] Deletes { get; }

        /// <summary>
        /// Gets whether the head writes an analysis table; such a program reads no fact table and no scoped atom, so
        /// analyses depend only on terms.
        /// </summary>
        public bool WritesAnalysis { get; }

        /// <summary>
        /// Gets whether the atoms match subsumed rows too: a scheduler's query, as egglog at 5e655cae builds it
        /// (<c>src/scheduler.rs</c>, <c>query(&amp;rule.body, true)</c>); every other program matches only rows that are not
        /// subsumed, or a mirror copy rule's only subsumed ones.
        /// </summary>
        public bool IncludeSubsumed { get; init; }

        /// <summary>
        /// Gets the context of the body's primitives, egglog's: <see cref="PrimitiveAccess.Pure"/> for a semi-naive rule's
        /// body and a split rule's, <see cref="PrimitiveAccess.Read"/> for a naive rule's and a query's.
        /// </summary>
        public PrimitiveAccess BodyContext { get; init; } = PrimitiveAccess.Pure;

        /// <summary>
        /// Gets the context of the head's primitives, egglog's: <see cref="PrimitiveAccess.Write"/> for a semi-naive rule's
        /// head, <see cref="PrimitiveAccess.Full"/> for a naive rule's.
        /// </summary>
        public PrimitiveAccess HeadContext { get; init; } = PrimitiveAccess.Write;

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
