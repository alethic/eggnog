using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A merge that is an expression over the old and the new value (<see cref="FunctionMerge.Expression{T}"/>),
    /// egglog's <c>:merge</c> of any expression: primitives, constants, and calls of functions and constructors, which
    /// the engine's merge looks up or makes as egglog's <c>MergeFn::Function</c> does. A class constant, which must be
    /// from the root, is the class it names as the last rebuild left it canonical, or the old value when the values
    /// agree, whatever the function's sort, as egglog's read of a global is. Its primitives take their classes as they
    /// are, as egglog's do. It is lowered to the engine's merge forms when its function is defined, and named by its
    /// rendering, such as <c>(Join old new)</c>.
    /// </summary>
    /// <typeparam name="T">The value type, or the sort.</typeparam>
    internal sealed class FunctionMergeExpression<T> : FunctionMergeDefinition
    {

        readonly Var<T> old;
        readonly Var<T> @new;
        readonly Term<T> body;
        readonly TableHandle[] tables;
        readonly bool callsTablePrimitive;
        bool readsClasses;

        /// <summary>
        /// Initializes a new instance: builds the expression over the two variables once.
        /// </summary>
        /// <param name="build">Builds the expression from the old and the new value.</param>
        /// <exception cref="ArgumentException">The expression is <c>null</c>.</exception>
        public FunctionMergeExpression(Func<Term<T>, Term<T>, Term<T>> build)
            : this(new Var<T>(new object(), "old"), new Var<T>(new object(), "new"), build)
        {
        }

        FunctionMergeExpression(Var<T> old, Var<T> @new, Func<Term<T>, Term<T>, Term<T>> build)
            : base(Build(old, @new, build, out var body))
        {
            this.old = old;
            this.@new = @new;
            this.body = body;
            var found = new List<TableHandle>();
            CollectTables(body, found);
            tables = found.ToArray();
            callsTablePrimitive = CallsTablePrimitive(body);
        }

        /// <summary>
        /// Gets whether an expression calls a primitive over the tables (<see cref="Primitive.Access"/>), whose writes
        /// only the engine's merge stages.
        /// </summary>
        static bool CallsTablePrimitive(Term term)
        {
            var shape = term.Shape;
            if (shape.Kind == TermShapeKind.Computation && shape.Primitive!.UsesTables)
                return true;

            foreach (var argument in shape.Arguments)
                if (CallsTablePrimitive(argument))
                    return true;

            return false;
        }

        /// <summary>
        /// Collects the tables an expression applies, once each.
        /// </summary>
        static void CollectTables(Term term, List<TableHandle> found)
        {
            var shape = term.Shape;
            if (shape.Kind == TermShapeKind.Application && found.Contains(shape.Table!) == false)
                found.Add(shape.Table!);

            foreach (var argument in shape.Arguments)
                CollectTables(argument, found);
        }

        /// <summary>
        /// Builds the expression and gets its rendering, the merge's name.
        /// </summary>
        static string Build(Var<T> old, Var<T> @new, Func<Term<T>, Term<T>, Term<T>> build, out Term<T> body)
        {
            body = build(old, @new) ?? throw new ArgumentException("A merge expression is a term, not null.", nameof(build));
            return SExpression.Render(body);
        }

        /// <inheritdoc />
        public override bool DecodesValues => false;

        /// <inheritdoc />
        public override bool ComparesValues => true;

        /// <inheritdoc />
        /// <remarks>Set when the expression is lowered with a class constant.</remarks>
        public override bool ReadsClasses => readsClasses;

        /// <inheritdoc />
        /// <remarks>Not when the expression calls tables, or a primitive over them, whose writes the engine's merge
        /// stages.</remarks>
        public override bool CanSimulate => tables.Length == 0 && callsTablePrimitive == false;

        /// <inheritdoc />
        public override bool WritesTables => callsTablePrimitive;

        /// <inheritdoc />
        public override IReadOnlyCollection<TableHandle> Tables => tables;

        /// <inheritdoc />
        /// <exception cref="ArgumentException">The expression holds a variable other than the old and the new value, a
        /// class constant that is not from the root, a table of another graph, or a scoped view.</exception>
        public override Relations.MergeFn CreateMergeFn(EGraph graph, string functionName, ColumnType type, List<Relations.ExternalFunctionId> registered) =>
            Lower(graph, functionName, body, registered);

        /// <summary>
        /// Evaluates an expression of primitives and constants over the two cells, as the engine's merge will, on the
        /// calling thread (the host batch holds <see cref="EngineCallMarker"/>), its primitives taking their classes as
        /// they are; a primitive that throws or has no result throws, and a write primitive's unions go to
        /// <paramref name="union"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">The expression calls tables (<see cref="CanSimulate"/>).</exception>
        public override uint MergeCells(Scope scope, ColumnType type, uint stored, uint incoming, Action<uint, uint> union)
        {
            if (CanSimulate == false)
                throw new InvalidOperationException($"The merge {Name} calls tables, so only the engine evaluates it.");

            // As the engine's merge primitive (MergePrimitiveCall): no canonicalizer, not even an outer call's.
            var canonicalizer = ClassCanonicalizer.Enter(null);
            try
            {
                return Evaluate(scope, body, stored, incoming, union);
            }
            finally
            {
                ClassCanonicalizer.Exit(canonicalizer);
            }
        }

        /// <summary>
        /// Evaluates one term of an expression without applications.
        /// </summary>
        uint Evaluate(Scope scope, Term term, uint stored, uint incoming, Action<uint, uint> union)
        {
            var graph = scope.Graph;
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Variable:
                    return ReferenceEquals(term, old) ? stored : incoming;
                case TermShapeKind.Constant:
                {
                    var type = TypeOf(graph, shape.ConstantType, shape.ClrType);
                    if (type.Kind != ColumnKind.Sort)
                        return type.EncodeObject(shape.Constant!);

                    // A class constant, as the engine's merge runs it: the old value when the values agree, whatever its
                    // sort, and otherwise the class as the last rebuild left it canonical, which is as the scope finds it
                    // now: the batch flushes and rebuilds after its merge of classes before such a write.
                    return stored == incoming ? stored : scope.Engine.Find(type.ClassOf(shape.Constant!).Cell);
                }
                default:
                {
                    var arguments = new uint[shape.Arguments.Length];
                    var parameters = new ColumnType[arguments.Length];
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        arguments[i] = Evaluate(scope, shape.Arguments[i], stored, incoming, union);
                        parameters[i] = TypeOf(graph, shape.Arguments[i].Shape.ConstantType, shape.Arguments[i].ClrType);
                    }

                    var result = TypeOf(graph, null, term.ClrType);
                    if (shape.Primitive!.IsWrite)
                        return shape.Primitive.TryInvokeWrite(arguments, parameters, result, union, scope.ContainerView, out var written)
                            ? written
                            : throw Primitive.NoResult(shape.Primitive.Name);

                    return shape.Primitive.InvokeCore(arguments, parameters, result, scope);
                }
            }
        }

        /// <summary>
        /// Lowers one term of the expression to an engine merge.
        /// </summary>
        Relations.MergeFn Lower(EGraph graph, string functionName, Term term, List<Relations.ExternalFunctionId> registered)
        {
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Variable:
                    if (ReferenceEquals(term, old))
                        return Relations.MergeFn.Old;

                    if (ReferenceEquals(term, @new))
                        return Relations.MergeFn.New;

                    throw new ArgumentException($"The merge {Name} of '{functionName}' holds the variable '{shape.Name}', which is not its old or new value.", "merge");
                case TermShapeKind.Constant:
                {
                    var constantType = TypeOf(graph, shape.ConstantType, shape.ClrType);
                    if (constantType.Kind == ColumnKind.Sort)
                    {
                        // egglog's read of the global keeps the old value when the values agree, of whatever sort.
                        var cell = ClassCell(graph, functionName, constantType.ClassOf(shape.Constant!));
                        readsClasses = true;
                        return Relations.MergeFn.IdConst(new Relations.Value(cell));
                    }

                    if (constantType.HoldsClasses)
                        throw new ArgumentException($"The merge {Name} of '{functionName}' holds a constant {constantType.Name}, which holds classes; build it with its primitives over terms, as egglog has no literal for one.", "merge");

                    return Relations.MergeFn.Const(new Relations.Value(constantType.EncodeObject(shape.Constant!)));
                }
                case TermShapeKind.Application:
                {
                    var table = shape.Table!;
                    if (ReferenceEquals(table.Graph, graph) == false)
                        throw new ArgumentException($"The merge {Name} of '{functionName}' calls '{table.Name}', a table of another graph.", "merge");

                    if (table.ReadScope is { } tag)
                        throw new ArgumentException($"The merge {Name} of '{functionName}' reads '{table.Name} @{tag.Name}'; a merge reads its own scope.", "merge");

                    return Relations.MergeFn.Function(table.Function, LowerAll(graph, functionName, shape.Arguments, registered));
                }
                default:
                {
                    var primitive = shape.Primitive!;

                    // egglog's merge runs in its write context: pure and write primitives, never one that reads.
                    if (Primitive.Allows(PrimitiveAccess.Write, primitive.Access) == false)
                        throw new ArgumentException(
                            $"The merge {Name} of '{functionName}': {Primitive.Refusal(primitive, "a merge expression", PrimitiveAccess.Write)}.", "merge");

                    var parameters = new ColumnType[shape.Arguments.Length];
                    for (var i = 0; i < parameters.Length; i++)
                    {
                        var argument = shape.Arguments[i].Shape;
                        parameters[i] = TypeOf(graph, argument.ConstantType, shape.Arguments[i].ClrType);
                    }

                    var result = TypeOf(graph, null, term.ClrType);

                    var arguments = LowerAll(graph, functionName, shape.Arguments, registered);
                    var call = graph.Engine.RegisterExternalFunction(new MergePrimitiveCall(graph, functionName, Name, primitive, parameters, result));
                    registered.Add(call);
                    return Relations.MergeFn.Primitive(call, arguments);
                }
            }
        }

        /// <summary>
        /// Gets the cell of a class constant, which egglog would hold as a global: it names a class of the graph, made by
        /// a batch that committed, and is from the root, as a global rule's class constants are, so that it is a class
        /// in every scope the merge runs in.
        /// </summary>
        uint ClassCell(EGraph graph, string functionName, ClassId id)
        {
            if (ClassChecks.CheckRuleConstant(null, graph, id, "a merge may hold only classes from the root") is { } refusal)
                throw new ArgumentException($"The merge {Name} of '{functionName}' holds a class constant it may not: {refusal}.", "merge");

            return id.Cell;
        }

        /// <summary>
        /// Lowers the arguments of a call, in order.
        /// </summary>
        Relations.MergeFn[] LowerAll(EGraph graph, string functionName, IReadOnlyList<Term> arguments, List<Relations.ExternalFunctionId> registered)
        {
            var lowered = new Relations.MergeFn[arguments.Count];
            for (var i = 0; i < lowered.Length; i++)
                lowered[i] = Lower(graph, functionName, arguments[i], registered);

            return lowered;
        }

        /// <summary>
        /// Gets the column type of a term's type in the graph.
        /// </summary>
        static ColumnType TypeOf(EGraph graph, ColumnType? constantType, Type clrType)
        {
            var type = constantType ?? graph.Values.TypeOf(clrType);
            if (type.Store is not null && ReferenceEquals(type.Store, graph.Values) == false)
                throw new ArgumentException($"Column type {type.Name} belongs to another graph.", "merge");

            return type;
        }

    }

}
