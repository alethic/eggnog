using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// How a function resolves a functional-dependency conflict, two rows with the same key and different outputs: an
    /// expression tree over the old output, the new output and constants, egglog-bridge's <c>MergeFn</c>
    /// (egglog-bridge/src/lib.rs:1010-1030). Constructors use <see cref="UnionId"/>; custom functions use
    /// <see cref="AssertEq"/> unless they declare <c>:merge</c>, which the frontend translates into
    /// <see cref="Old"/>, <see cref="New"/>, <see cref="Const"/>, <see cref="Primitive(ExternalFunctionId, MergeFn[])"/>
    /// and <see cref="Function(FunctionId, MergeFn[])"/> nodes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Immutable. <see cref="EGraph.AddTable"/> resolves the tree once, against the e-graph's tables and panic functions,
    /// into the reference-free form the table's merge runs per conflict (egglog's <c>ResolvedMergeFn</c>).
    /// </para>
    /// <para>
    /// Every merge function declares whether it is a lattice merge (associative, commutative and idempotent), which
    /// decides whether a context with ancestors may write the table: <see cref="AssertEq"/> and <see cref="UnionId"/>
    /// are; <see cref="Old"/>, <see cref="New"/> and <see cref="Const"/> are not (the result depends on arrival order,
    /// or is not idempotent); a <see cref="Primitive(ExternalFunctionId, MergeFn[])"/> or
    /// <see cref="Function(FunctionId, MergeFn[])"/> merge is not unless declared with <see cref="AsLattice"/>
    /// (<c>min</c>, <c>max</c> or <c>set-union</c> over the old and new values, say). egglog documents the lattice
    /// requirement without recording it. Only the root's declaration matters: the arguments of a lattice primitive are
    /// typically <see cref="Old"/> and <see cref="New"/>.
    /// </para>
    /// </remarks>
    internal sealed class MergeFn
    {

        static readonly MergeFn[] NoArgs = Array.Empty<MergeFn>();
        static readonly MergeFn assertEq = new(MergeFnKind.AssertEq, default, default, default, NoArgs, true);
        static readonly MergeFn unionId = new(MergeFnKind.UnionId, default, default, default, NoArgs, true);
        static readonly MergeFn old = new(MergeFnKind.Old, default, default, default, NoArgs, false);
        static readonly MergeFn @new = new(MergeFnKind.New, default, default, default, NoArgs, false);

        readonly MergeFn[] args;

        MergeFn(MergeFnKind kind, Value constant, ExternalFunctionId primitive, FunctionId function, MergeFn[] args, bool isLattice)
        {
            Kind = kind;
            Constant = constant;
            PrimitiveId = primitive;
            FunctionId = function;
            this.args = args;
            IsLattice = isLattice;
        }

        /// <summary>
        /// Gets the merge that requires the old and new outputs to agree: egglog's <c>MergeFn::AssertEq</c>. A conflict
        /// is an egglog-level panic ("Illegal merge attempted for function ...") and the old output stays. A lattice
        /// merge.
        /// </summary>
        public static MergeFn AssertEq => assertEq;

        /// <summary>
        /// Gets the merge of constructors: union the old and new e-class ids in the union-find and keep the smaller,
        /// which is the id the union-find keeps canonical. egglog's <c>MergeFn::UnionId</c>. A lattice merge.
        /// </summary>
        public static MergeFn UnionId => unionId;

        /// <summary>
        /// Gets the merge that keeps the old output: egglog's <c>MergeFn::Old</c>. Not a lattice merge.
        /// </summary>
        public static MergeFn Old => old;

        /// <summary>
        /// Gets the merge that takes the new output: egglog's <c>MergeFn::New</c>. Not a lattice merge.
        /// </summary>
        public static MergeFn New => @new;

        /// <summary>
        /// Creates the merge that produces <paramref name="constant"/>: egglog's <c>MergeFn::Const</c>, mostly a leaf of a
        /// larger merge (a clamp, say). Not a lattice merge: it is not idempotent.
        /// </summary>
        /// <param name="constant">The value.</param>
        /// <returns>The merge.</returns>
        public static MergeFn Const(Value constant) => new(MergeFnKind.Const, constant, default, default, NoArgs, false);

        /// <summary>
        /// Creates the merge that applies the external function <paramref name="primitive"/> to the results of
        /// <paramref name="args"/>: egglog's <c>MergeFn::Primitive</c>. When the function produces nothing, the merge is an
        /// egglog-level panic ("Merge function for ... primitive call failed") and the old output stays. Not a lattice
        /// merge unless declared with <see cref="AsLattice"/>.
        /// </summary>
        /// <param name="primitive">The external function, called with the merge's (write-context) execution state.</param>
        /// <param name="args">The argument merges; copied.</param>
        /// <returns>The merge.</returns>
        public static MergeFn Primitive(ExternalFunctionId primitive, params MergeFn[] args)
        {
            return new(MergeFnKind.Primitive, default, primitive, default, CopyArgs(args), false);
        }

        /// <summary>
        /// Creates the merge that looks up the results of <paramref name="args"/> as a key of <paramref name="function"/>:
        /// egglog's <c>MergeFn::Function</c>. A function with a default inserts a missing key (a constructor mints a fresh
        /// id); one without fails, which is an egglog-level panic ("Lookup on ... failed in the merge function for ...")
        /// keeping the old output. When the old and new outputs are equal the lookup is skipped (egglog PR 287). Not a
        /// lattice merge unless declared with <see cref="AsLattice"/>.
        /// </summary>
        /// <param name="function">The function to look up; declared before the function this merge belongs to.</param>
        /// <param name="args">The argument merges, one per key column of <paramref name="function"/>; copied.</param>
        /// <returns>The merge.</returns>
        public static MergeFn Function(FunctionId function, params MergeFn[] args)
        {
            return new(MergeFnKind.Function, default, default, function, CopyArgs(args), false);
        }

        /// <summary>
        /// Gets which case this is.
        /// </summary>
        public MergeFnKind Kind { get; }

        /// <summary>
        /// Gets the constant of a <see cref="MergeFnKind.Const"/> merge.
        /// </summary>
        public Value Constant { get; }

        /// <summary>
        /// Gets the external function of a <see cref="MergeFnKind.Primitive"/> merge.
        /// </summary>
        public ExternalFunctionId PrimitiveId { get; }

        /// <summary>
        /// Gets the function of a <see cref="MergeFnKind.Function"/> merge.
        /// </summary>
        public FunctionId FunctionId { get; }

        /// <summary>
        /// Gets the argument merges of a <see cref="MergeFnKind.Primitive"/> or <see cref="MergeFnKind.Function"/> merge;
        /// empty for the others.
        /// </summary>
        public ReadOnlySpan<MergeFn> Args => args;

        /// <summary>
        /// Gets whether the merge is declared a lattice merge: associative, commutative and idempotent, so that the
        /// order and repetition of conflicting writes do not change the result.
        /// </summary>
        public bool IsLattice { get; }

        /// <summary>
        /// Returns this <see cref="MergeFnKind.Primitive"/> or <see cref="MergeFnKind.Function"/> merge declared a lattice
        /// merge. The caller vouches for the declaration; the engine relies on it to let contexts with ancestors write
        /// the table.
        /// </summary>
        /// <returns>The declared merge; this instance if it is already declared.</returns>
        /// <exception cref="InvalidOperationException">The merge is not a primitive or function merge, whose lattice
        /// property is fixed.</exception>
        public MergeFn AsLattice()
        {
            if (Kind != MergeFnKind.Primitive && Kind != MergeFnKind.Function)
                throw new InvalidOperationException($"A {Kind} merge cannot be declared a lattice merge; only primitive and function merges can.");

            return IsLattice ? this : new MergeFn(Kind, Constant, PrimitiveId, FunctionId, args, true);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var builder = new StringBuilder();
            Format(builder);
            return builder.ToString();
        }

        /// <summary>
        /// Appends the expression to <paramref name="builder"/>, egglog-style.
        /// </summary>
        void Format(StringBuilder builder)
        {
            switch (Kind)
            {
                case MergeFnKind.Const:
                    builder.Append("(const ").Append(Constant.Rep).Append(')');
                    return;
                case MergeFnKind.Primitive:
                case MergeFnKind.Function:
                    builder.Append('(').Append(Kind == MergeFnKind.Primitive ? PrimitiveId.ToString() : FunctionId.ToString());
                    foreach (var arg in args)
                    {
                        builder.Append(' ');
                        arg.Format(builder);
                    }

                    builder.Append(')');
                    return;
                default:
                    builder.Append(Kind switch
                    {
                        MergeFnKind.AssertEq => "assert-eq",
                        MergeFnKind.UnionId => "union",
                        MergeFnKind.Old => "old",
                        _ => "new",
                    });
                    return;
            }
        }

        /// <summary>
        /// Copies an argument array, rejecting null entries.
        /// </summary>
        static MergeFn[] CopyArgs(MergeFn[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            if (args.Length == 0)
                return NoArgs;

            var copy = new MergeFn[args.Length];
            for (var i = 0; i < args.Length; i++)
                copy[i] = args[i] ?? throw new ArgumentNullException(nameof(args), $"Argument {i} is null.");

            return copy;
        }

    }

}
