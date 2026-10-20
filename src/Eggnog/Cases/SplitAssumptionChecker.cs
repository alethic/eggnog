using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The definition-time checks of a split rule's assumptions (<see cref="SplitAssumptions.Compile"/>): each fact is a
    /// host write once its variables are bound, and every term is ground then and reads no table. It records the declared
    /// variables the facts use, in first-use order, and the column types of each computation. One instance per definition;
    /// not thread-safe.
    /// </summary>
    internal sealed class SplitAssumptionChecker
    {

        readonly EGraph graph;
        readonly string name;
        readonly object owner;
        readonly ISet<Term> declared;
        readonly Term element;
        readonly List<Term> parameters;
        readonly Dictionary<Term, (ColumnType[] Parameters, ColumnType Result)> computations;

        /// <summary>
        /// Initializes the checks of one split rule's assumptions.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="name">The split rule's name, for messages.</param>
        /// <param name="owner">The builder whose variables the facts may use.</param>
        /// <param name="declared">The declared body variables.</param>
        /// <param name="element">The case variable.</param>
        /// <param name="parameters">Receives the declared variables used, in first-use order.</param>
        /// <param name="computations">Receives each computation's parameter and result types.</param>
        public SplitAssumptionChecker(EGraph graph, string name, object owner, ISet<Term> declared, Term element, List<Term> parameters,
            Dictionary<Term, (ColumnType[] Parameters, ColumnType Result)> computations)
        {
            this.graph = graph;
            this.name = name;
            this.owner = owner;
            this.declared = declared;
            this.element = element;
            this.parameters = parameters;
            this.computations = computations;
        }


        /// <summary>
        /// Checks one assumption: a write of host-write shape.
        /// </summary>
        public void CheckFact(Fact fact)
        {
            switch (fact)
            {
                case RelationFact relation:
                {
                    CheckTable(relation.Table);
                    for (var i = 0; i < relation.Arguments.Length; i++)
                        CheckTerm(relation.Arguments[i], relation.Table.KeyArray[i]);

                    break;
                }
                case EqualityFact equality:
                {
                    var left = equality.Left.Shape;
                    var right = equality.Right.Shape;
                    if (IsApplication(left, TableKind.Function) && (right.Kind != TermShapeKind.Application || IsClassWrite(left, right)))
                        CheckWrite(equality.Left, equality.Right);
                    else if (IsApplication(right, TableKind.Function) && (left.Kind != TermShapeKind.Application || IsClassWrite(right, left)))
                        CheckWrite(equality.Right, equality.Left);
                    else if (IsApplication(left, TableKind.Constructor) && IsClassValue(equality.Right))
                        CheckWrite(equality.Left, equality.Right);
                    else if (IsApplication(right, TableKind.Constructor) && IsClassValue(equality.Left))
                        CheckWrite(equality.Right, equality.Left);
                    else if (TypeOf(equality.Left.ClrType).Kind == ColumnKind.Sort && ReferenceEquals(TypeOf(equality.Right.ClrType), TypeOf(equality.Left.ClrType)))
                    {
                        CheckTerm(equality.Left, null);
                        CheckTerm(equality.Right, null);
                    }
                    else
                    {
                        throw Error($"assumptions are writes; a value equality that is not a function write: {equality.Left} = {equality.Right}");
                    }

                    break;
                }
                case NotEqualFact:
                    throw Error("assumptions are writes; 'IsNot' is a filter");
                case ExistsFact:
                    throw Error("assumptions are writes; 'Exists' is a check");
                case FilterFact:
                    throw Error("assumptions are writes; 'True' is a filter");
                case ScopedFact scoped:
                    throw Error($"assumptions are writes; Fact.In('{scoped.Scope.Name}') is read-only");
                default:
                    throw Error($"a {fact.GetType().Name} cannot be assumed");
            }
        }

        /// <summary>
        /// Checks a write of a function's value or a constructor's output: the application's keys, then the value.
        /// </summary>
        void CheckWrite(Term application, Term value)
        {
            var shape = application.Shape;
            var table = shape.Table!;
            CheckTable(table);
            for (var i = 0; i < shape.Arguments.Length; i++)
                CheckTerm(shape.Arguments[i], table.KeyArray[i]);

            CheckTerm(value, table.Output);
        }

        /// <summary>
        /// Checks one term in an assumption: of the expected type when one is given; ground once its variables are
        /// bound; reading no table.
        /// </summary>
        void CheckTerm(Term term, ColumnType? expected)
        {
            var shape = term.Shape;
            if (expected is not null && expected.ClrType != term.ClrType)
                throw Error($"{term} is a {ValueStore.DisplayName(term.ClrType)}, where {expected.Name} is expected");

            switch (shape.Kind)
            {
                case TermShapeKind.Variable:
                {
                    if (ReferenceEquals(shape.Owner, owner) == false)
                        throw Error($"variable '{shape.Name}' belongs to another rule");

                    if (ReferenceEquals(term, element))
                        return;

                    if (declared.Contains(term) == false)
                        throw Error($"variable '{shape.Name}' belongs to another rule");

                    if (parameters.Contains(term) == false)
                        parameters.Add(term);

                    return;
                }
                case TermShapeKind.Constant:
                    CheckConstant(shape);
                    return;
                case TermShapeKind.Application:
                {
                    var table = shape.Table!;
                    CheckTable(table);
                    if (table.Kind != TableKind.Constructor)
                        throw Error($"assumptions do not read; match '{table.Name}' in When");

                    for (var i = 0; i < shape.Arguments.Length; i++)
                        CheckTerm(shape.Arguments[i], table.KeyArray[i]);

                    return;
                }
                default:
                {
                    var primitive = shape.Primitive!;
                    var types = new ColumnType[primitive.ParameterTypes.Count];
                    for (var i = 0; i < types.Length; i++)
                        types[i] = TypeOf(primitive.ParameterTypes[i]);

                    var result = TypeOf(primitive.ResultType);
                    if (result.Kind == ColumnKind.Sort || Array.Exists(types, p => p.Kind == ColumnKind.Sort))
                        throw Error($"primitive '{primitive.Name}' takes or returns a sort; classes are compared only with Is and IsNot");

                    for (var i = 0; i < shape.Arguments.Length; i++)
                        CheckTerm(shape.Arguments[i], types[i]);

                    computations[term] = (types, result);
                    return;
                }
            }
        }

        /// <summary>
        /// Checks a constant: of this graph's types; a class from the root, as for a global rule.
        /// </summary>
        void CheckConstant(TermShape shape)
        {
            var type = shape.ConstantType ?? TypeOf(shape.ClrType);
            if (type.Store is not null && ReferenceEquals(type.Store, graph.Values) == false)
                throw Error($"column type {type.Name} belongs to another graph");

            if (type.Kind != ColumnKind.Sort)
                return;

            var id = type.ClassOf(shape.Constant!);
            if (id.Origin is not { } origin)
                throw Error("a class constant is default(ClassId), which names no class");

            if (ReferenceEquals(origin.Graph, graph) == false)
                throw Error($"class {id} belongs to another graph");

            if (ClassChecks.AllowedInRule(null, origin) == false)
            {
                var scope = origin.IsBatch ? origin.Parent! : origin;
                throw Error($"class {id} came from scope '{scope.Path}'; a split rule may hold only classes from the root");
            }
        }

        /// <summary>
        /// Checks that a table is this graph's and is not a read-only scoped view: writes never target a scoped handle. Nor
        /// a function whose merge is not a lattice: a case is a child, and only the root writes one.
        /// </summary>
        void CheckTable(TableHandle table)
        {
            if (ReferenceEquals(table.Graph, graph) == false)
                throw Error($"table '{table.Name}' belongs to another graph");

            if (table.ReadScope is { } tag)
                throw Error($"'{table.Name}@{tag.Name}' is read-only");

            if (table is FunctionHandle { Merge: { } merge })
                throw Error($"'{table.Name}' has the merge '{merge.Name}', which is not a lattice, so only the root may write it, and a case is a child");
        }

        /// <summary>
        /// Gets whether a term is a class value that is not an application: a variable or constant of a sort.
        /// </summary>
        bool IsClassValue(Term term) => term.Shape.Kind != TermShapeKind.Application && TypeOf(term.ClrType).Kind == ColumnKind.Sort;

        /// <summary>
        /// Gets the column type of a CLR type; an unknown type is an error.
        /// </summary>
        ColumnType TypeOf(Type clrType)
        {
            try
            {
                return graph.Values.TypeOf(clrType);
            }
            catch (ArgumentException exception)
            {
                throw Error($"type {ValueStore.DisplayName(clrType)} is not registered; call {ValueStore.RegisterMethod(clrType)}", exception);
            }
        }

        /// <summary>
        /// Gets whether a shape is an application of a table of <paramref name="kind"/>.
        /// </summary>
        static bool IsApplication(TermShape shape, TableKind kind) => shape.Kind == TermShapeKind.Application && shape.Table!.Kind == kind;

        /// <summary>
        /// Gets whether <paramref name="value"/> is a constructor term written as the class of a function of a sort, as
        /// egglog's <c>set</c> of a function of an e-class sort makes it.
        /// </summary>
        static bool IsClassWrite(TermShape application, TermShape value) =>
            application.Table!.Output!.Kind == ColumnKind.Sort && IsApplication(value, TableKind.Constructor);

        /// <summary>
        /// Makes a definition error naming the split rule.
        /// </summary>
        RuleDefinitionException Error(string message, Exception? inner = null) => new(name, message, inner);

    }

}
