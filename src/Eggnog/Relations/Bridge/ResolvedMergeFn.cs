using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A <see cref="MergeFn"/> resolved against an e-graph into the form a table's merge evaluates per conflict: panic
    /// functions created, function references turned into <see cref="TableAction"/>s, and nothing that refers back to the
    /// e-graph. egglog-bridge's <c>ResolvedMergeFn</c> (egglog-bridge/src/lib.rs:1154-1246).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Run"/> is a switch on the node kind, recursing into arguments; a leaf costs a branch. A primitive
    /// node evaluates its arguments into a stack buffer (egglog allocates a vector per call) and makes one virtual call
    /// into the external function; a function node makes one hash probe, and stages a row only for a missing key of a
    /// function with a default.
    /// </para>
    /// <para>
    /// Immutable; evaluated concurrently by parallel merges, each with its own state.
    /// </para>
    /// </remarks>
    internal sealed class ResolvedMergeFn
    {

        /// <summary>
        /// The most arguments evaluated into a stack buffer; wider nodes allocate.
        /// </summary>
        const int MaxStackArgs = 8;

        static readonly ResolvedMergeFn[] NoArgs = Array.Empty<ResolvedMergeFn>();

        readonly MergeFnKind kind;
        readonly uint constant;
        readonly TableId unionFind;
        readonly ExternalFunctionId primitive;
        readonly ExternalFunctionId panic;
        readonly TableAction? function;
        readonly ResolvedMergeFn[] args;
        readonly bool keepsOld;
        readonly int slot;

        ResolvedMergeFn(MergeFnKind kind, uint constant, TableId unionFind, ExternalFunctionId primitive, ExternalFunctionId panic, TableAction? function, ResolvedMergeFn[] args, bool keepsOld = false, int slot = 0)
        {
            this.kind = kind;
            this.constant = constant;
            this.unionFind = unionFind;
            this.primitive = primitive;
            this.panic = panic;
            this.function = function;
            this.args = args;
            this.keepsOld = keepsOld;
            this.slot = slot;
        }

        /// <summary>
        /// Creates a node that produces <paramref name="value"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The node.</returns>
        public static ResolvedMergeFn Const(uint value) => new(MergeFnKind.Const, value, default, default, default, null, NoArgs);

        /// <summary>
        /// Creates a node that produces the e-class id <paramref name="id"/> as the last rebuild pass left it canonical,
        /// which the database keeps in <paramref name="slot"/> (<see cref="Database.AddClassConstant"/>), or the old value
        /// when <paramref name="keepsOld"/> is set and the values agree (<see cref="MergeFn.IdConst"/>).
        /// </summary>
        /// <param name="id">The e-class id.</param>
        /// <param name="slot">The database's slot for the id.</param>
        /// <param name="keepsOld">Whether the node yields the old value when the values agree.</param>
        /// <returns>The node.</returns>
        public static ResolvedMergeFn IdConst(uint id, int slot, bool keepsOld) => new(MergeFnKind.IdConst, id, default, default, default, null, NoArgs, keepsOld, slot);

        /// <summary>
        /// Creates a node that keeps the old value.
        /// </summary>
        /// <returns>The node.</returns>
        public static ResolvedMergeFn Old() => new(MergeFnKind.Old, 0, default, default, default, null, NoArgs);

        /// <summary>
        /// Creates a node that takes the new value.
        /// </summary>
        /// <returns>The node.</returns>
        public static ResolvedMergeFn New() => new(MergeFnKind.New, 0, default, default, default, null, NoArgs);

        /// <summary>
        /// Creates a node that panics through <paramref name="panic"/> when the values differ.
        /// </summary>
        /// <param name="panic">The panic function.</param>
        /// <returns>The node.</returns>
        public static ResolvedMergeFn AssertEq(ExternalFunctionId panic) => new(MergeFnKind.AssertEq, 0, default, default, panic, null, NoArgs);

        /// <summary>
        /// Creates a node that unions differing values in <paramref name="unionFind"/>.
        /// </summary>
        /// <param name="unionFind">The union-find table.</param>
        /// <returns>The node.</returns>
        public static ResolvedMergeFn UnionId(TableId unionFind) => new(MergeFnKind.UnionId, 0, unionFind, default, default, null, NoArgs);

        /// <summary>
        /// Creates a node that applies <paramref name="primitive"/> to its arguments and panics through
        /// <paramref name="panic"/> when it produces nothing.
        /// </summary>
        /// <param name="primitive">The external function.</param>
        /// <param name="args">The argument nodes; owned by the new node.</param>
        /// <param name="panic">The panic function.</param>
        /// <returns>The node.</returns>
        public static ResolvedMergeFn Primitive(ExternalFunctionId primitive, ResolvedMergeFn[] args, ExternalFunctionId panic)
        {
            ArgumentNullException.ThrowIfNull(args);
            return new(MergeFnKind.Primitive, 0, default, primitive, panic, null, args);
        }

        /// <summary>
        /// Creates a node that looks its arguments up in <paramref name="function"/> (inserting the function's default
        /// for a missing key) and panics through <paramref name="panic"/> when that fails.
        /// </summary>
        /// <param name="function">The function's handle.</param>
        /// <param name="args">The argument nodes, one per key column; owned by the new node.</param>
        /// <param name="panic">The panic function.</param>
        /// <returns>The node.</returns>
        public static ResolvedMergeFn Function(TableAction function, ResolvedMergeFn[] args, ExternalFunctionId panic)
        {
            ArgumentNullException.ThrowIfNull(function);
            ArgumentNullException.ThrowIfNull(args);
            if (args.Length != function.Math.KeyCount)
                throw new ArgumentException($"The function takes {function.Math.KeyCount} keys, not {args.Length}.", nameof(args));

            return new(MergeFnKind.Function, 0, default, default, panic, function, args);
        }

        /// <summary>
        /// Gets the node's kind.
        /// </summary>
        public MergeFnKind Kind => kind;

        /// <summary>
        /// Gets whether an <see cref="MergeFnKind.IdConst"/> node yields the old value when the values agree.
        /// </summary>
        public bool KeepsOld => keepsOld;

        /// <summary>
        /// Computes the merged value of <paramref name="current"/> (the old output) and <paramref name="incoming"/> (the
        /// new output). egglog's <c>ResolvedMergeFn::run</c>.
        /// </summary>
        /// <param name="state">The merge's execution state: unions, predicted rows and panics go through it.</param>
        /// <param name="current">The old output.</param>
        /// <param name="incoming">The new output.</param>
        /// <param name="timestamp">The incoming row's timestamp, which a staged union carries.</param>
        /// <returns>The merged output; the old output when the merge panics.</returns>
        public uint Run(ExecutionState state, uint current, uint incoming, uint timestamp)
        {
            switch (kind)
            {
                case MergeFnKind.Const:
                    return constant;
                case MergeFnKind.IdConst:
                    // The read of egglog's global, a function node, which yields the old value when the values agree, at
                    // any depth (lib.rs:1222-1226); not for a function of another sort, whose old value is no class. The
                    // global's row is as the last rebuild pass left it, not as this flush's unions leave the union-find.
                    return keepsOld && current == incoming ? current : state.ReadClassConstant(slot);
                case MergeFnKind.Old:
                    return current;
                case MergeFnKind.New:
                    return incoming;
                case MergeFnKind.AssertEq:
                    if (current != incoming)
                        Panic(state, panic);

                    return current;
                case MergeFnKind.UnionId:
                    return current == incoming ? current : Union(state, current, incoming, timestamp);
                case MergeFnKind.Primitive:
                    return RunPrimitive(state, current, incoming, timestamp);
                default:
                    return RunFunction(state, current, incoming, timestamp);
            }
        }

        /// <summary>
        /// Stages the union <c>[current, incoming, timestamp]</c> and returns the smaller id: egglog's <c>UnionId</c> arm.
        /// THIS MUST MATCH THE UNION-FIND, which unions by minimum (lib.rs:1196-1198).
        /// </summary>
        uint Union(ExecutionState state, uint current, uint incoming, uint timestamp)
        {
            Span<uint> row = stackalloc uint[3];
            row[0] = current;
            row[1] = incoming;
            row[2] = timestamp;
            state.StageInsert(unionFind, row);
            return Math.Min(current, incoming);
        }

        /// <summary>
        /// Evaluates the arguments and calls the primitive: egglog's <c>Primitive</c> arm (lib.rs:1207-1221).
        /// </summary>
        uint RunPrimitive(ExecutionState state, uint current, uint incoming, uint timestamp)
        {
            var count = args.Length;
            Span<uint> values = count <= MaxStackArgs ? stackalloc uint[MaxStackArgs] : new uint[count];
            values = values.Slice(0, count);
            for (var i = 0; i < count; i++)
                values[i] = args[i].Run(state, current, incoming, timestamp);

            if (state.TryCallExternalFunction(primitive, values, out var result))
                return result;

            Panic(state, panic);
            return current;
        }

        /// <summary>
        /// Evaluates the arguments and looks them up, inserting the function's default for a missing key: egglog's
        /// <c>Function</c> arm (lib.rs:1222-1243), which skips everything when the values agree (egglog PR 287) and uses
        /// <c>lookup_or_insert</c> so that a constructor mints and a custom function only reads.
        /// </summary>
        uint RunFunction(ExecutionState state, uint current, uint incoming, uint timestamp)
        {
            if (current == incoming)
                return current;

            var count = args.Length;
            Span<uint> values = count <= MaxStackArgs ? stackalloc uint[MaxStackArgs] : new uint[count];
            values = values.Slice(0, count);
            for (var i = 0; i < count; i++)
                values[i] = args[i].Run(state, current, incoming, timestamp);

            if (function!.TryLookupOrInsert(state, values, out var result))
                return result;

            Panic(state, panic);
            return current;
        }

        /// <summary>
        /// Calls a panic function, which produces nothing (egglog asserts that).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Panic(ExecutionState state, ExternalFunctionId panic)
        {
            var produced = state.TryCallExternalFunction(panic, ReadOnlySpan<uint>.Empty, out _);
            Debug.Assert(produced == false, "a panic function produced a value");
        }

    }

}
