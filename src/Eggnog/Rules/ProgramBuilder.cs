using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Eggnog
{

    /// <summary>
    /// The mutable state of one compilation (§6.3): the registers and their union-find, the body's atoms and steps, the
    /// class constants, and the head. <see cref="RuleCompiler"/> drives it; every error is a
    /// <see cref="RuleDefinitionException"/> naming the rule, with the exact messages of §6.3.
    /// </summary>
    /// <remarks>
    /// Before the body is resolved, operands name raw registers, which unification merges into classes; a class may be
    /// bound to a constant. Resolution renumbers the classes that stay registers (body registers first, then head
    /// registers), substitutes constants, places the steps, and checks range restriction. One instance per compilation;
    /// not thread-safe.
    /// </remarks>
    internal sealed class ProgramBuilder
    {

        readonly EGraph graph;
        readonly RuleDraft draft;
        readonly Scope? owner;
        readonly string ruleName;

        readonly List<ColumnType> rawTypes = new();
        readonly List<string> rawNames = new();
        readonly List<RegisterKind> rawKinds = new();
        readonly List<int> parent = new();
        readonly List<Operand?> constants = new();
        readonly Dictionary<Term, int> declared = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<Term, string> makeVariables = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<Term, int> made = new(ReferenceEqualityComparer.Instance);
        readonly List<(TableHandle Table, Operand[] Columns)> rawAtoms = new();
        readonly List<(ProgramStepKind Kind, Primitive? Primitive, Operand[] Arguments, Operand Target)> rawSteps = new();
        readonly Dictionary<string, int> bodyMemo = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> headMemo = new(StringComparer.Ordinal);
        readonly Dictionary<Primitive, int> primitiveIds = new(ReferenceEqualityComparer.Instance);
        readonly List<uint> idCells = new();
        readonly List<ScopeOrigin> idOrigins = new();
        readonly Dictionary<uint, int> idIndex = new();
        readonly List<HeadOp> head = new();

        List<ProgramRegister> registers = new();
        int[] finalOf = Array.Empty<int>();
        bool[] bound = Array.Empty<bool>();
        ProgramAtom[] atoms = Array.Empty<ProgramAtom>();
        ProgramStep[] steps = Array.Empty<ProgramStep>();
        int fresh;

        /// <summary>
        /// Initializes a compilation of <paramref name="draft"/>.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="draft">The draft.</param>
        /// <param name="owner">The rule's owner scope; <c>null</c> for a global rule (always in R1).</param>
        public ProgramBuilder(EGraph graph, RuleDraft draft, Scope? owner)
        {
            this.graph = graph;
            this.draft = draft;
            this.owner = owner;
            ruleName = draft.Name;
        }

        /// <summary>
        /// Compiles the draft into a program: variables (step 1), body normalization (step 3), range restriction and step
        /// order (step 4), the head (step 8) and the analysis-writer check (step 7).
        /// </summary>
        /// <returns>The program.</returns>
        /// <exception cref="RuleDefinitionException">The draft is not a valid rule.</exception>
        public RuleProgram Build()
        {
            DeclareVariables();
            foreach (var fact in draft.Body)
                AddBodyFact(fact);

            ResolveBody();
            foreach (var item in draft.Head)
                AddHeadItem(item);

            var writesAnalysis = CheckAnalysisWriters();
            var named = new List<int>();
            foreach (var (_, _, variable) in draft.Variables)
            {
                var operand = Resolve(Operand.Register(declared[variable], rawTypes[declared[variable]]));
                named.Add(operand.IsRegister ? operand.Index : -1);
            }

            foreach (var item in draft.Head)
                if (item.Kind == HeadItemKind.Make)
                    named.Add(made.TryGetValue(item.Variable!, out var register) ? register : -1);

            return new RuleProgram(ruleName, registers.ToArray(), atoms, steps, head.ToArray(), idCells.ToArray(), idOrigins.ToArray(),
                named.ToArray(), writesAnalysis);
        }

        /// <summary>
        /// Step 1: a register per declared variable, in declaration order; duplicate names and unknown types are errors.
        /// </summary>
        void DeclareVariables()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, type, variable) in draft.Variables)
            {
                if (names.Add(name) == false)
                    throw Error($"variable '{name}' is declared twice");

                var resolved = type ?? ResolveType(variable.ClrType);
                declared.Add(variable, NewRegister(resolved, name, RegisterKind.Declared));
            }

            foreach (var item in draft.Head)
            {
                if (item.Kind != HeadItemKind.Make)
                    continue;

                var name = item.Variable!.Shape.Name!;
                if (names.Add(name) == false)
                    throw Error($"variable '{name}' is declared twice");

                ResolveType(item.Variable.ClrType);
                makeVariables.Add(item.Variable, name);
            }
        }

        /// <summary>
        /// Step 3: normalizes one body fact into atoms, unifications, constant bindings and steps.
        /// </summary>
        void AddBodyFact(Fact fact)
        {
            switch (fact)
            {
                case RelationFact relation:
                {
                    CheckTable(relation.Table);
                    var columns = new Operand[relation.Arguments.Length];
                    for (var i = 0; i < columns.Length; i++)
                        columns[i] = FlattenBody(relation.Arguments[i]);

                    rawAtoms.Add((relation.Table, columns));
                    break;
                }
                case EqualityFact equality:
                    Equate(FlattenBody(equality.Left), FlattenBody(equality.Right));
                    break;
                case NotEqualFact notEqual:
                    rawSteps.Add((ProgramStepKind.NotEqual, null, new[] { FlattenBody(notEqual.Left), FlattenBody(notEqual.Right) }, default));
                    break;
                case ExistsFact exists:
                    FlattenBody(exists.Application);
                    break;
                case FilterFact filter:
                {
                    var shape = filter.Condition.Shape;
                    if (shape.Kind == TermShapeKind.Computation)
                    {
                        var primitive = shape.Primitive!;
                        var (parameters, result) = CheckPrimitive(primitive);
                        if (result.ClrType != typeof(bool))
                            throw Error($"primitive '{primitive.Name}' returns {result.Name}, not bool, so it cannot filter");

                        var arguments = FlattenArguments(shape.Arguments, parameters, primitive, body: true);
                        rawSteps.Add((ProgramStepKind.Filter, primitive, arguments, default));
                    }
                    else
                    {
                        rawSteps.Add((ProgramStepKind.Filter, Primitives.IsTrue, new[] { FlattenBody(filter.Condition) }, default));
                    }

                    break;
                }
                default:
                    throw Error($"a {fact.GetType().Name} cannot appear in a rule");
            }
        }

        /// <summary>
        /// Step 3: turns a body term into an operand, creating atoms for applications and lets for computations.
        /// </summary>
        Operand FlattenBody(Term term)
        {
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Variable:
                {
                    CheckOwner(shape);
                    if (declared.TryGetValue(term, out var register))
                        return Operand.Register(register, rawTypes[register]);

                    if (makeVariables.TryGetValue(term, out var made))
                        throw Error($"head variable '{made}' made by Make appears in When");

                    throw Error($"variable '{shape.Name}' belongs to another rule");
                }
                case TermShapeKind.Constant:
                    return ConstantOperand(shape);
                case TermShapeKind.Application:
                {
                    var table = shape.Table!;
                    CheckTable(table);
                    var columns = new Operand[shape.Arguments.Length + 1];
                    for (var i = 0; i < shape.Arguments.Length; i++)
                        columns[i] = FlattenBody(shape.Arguments[i]);

                    var key = "a" + table.Index.ToString(CultureInfo.InvariantCulture) + Key(columns.AsSpan(0, shape.Arguments.Length));
                    if (bodyMemo.TryGetValue(key, out var shared))
                        return Operand.Register(shared, rawTypes[shared]);

                    var output = NewRegister(table.Output!, "$" + table.Name + (fresh++).ToString(CultureInfo.InvariantCulture), RegisterKind.Fresh);
                    columns[^1] = Operand.Register(output, table.Output!);
                    rawAtoms.Add((table, columns));
                    bodyMemo.Add(key, output);
                    return columns[^1];
                }
                default:
                {
                    var primitive = shape.Primitive!;
                    var (parameters, result) = CheckPrimitive(primitive);
                    var arguments = FlattenArguments(shape.Arguments, parameters, primitive, body: true);
                    var key = "c" + PrimitiveId(primitive).ToString(CultureInfo.InvariantCulture) + Key(arguments);
                    if (bodyMemo.TryGetValue(key, out var shared))
                        return Operand.Register(shared, rawTypes[shared]);

                    var target = NewRegister(result, "$" + primitive.Name + (fresh++).ToString(CultureInfo.InvariantCulture), RegisterKind.Fresh);
                    var operand = Operand.Register(target, result);
                    rawSteps.Add((ProgramStepKind.Let, primitive, arguments, operand));
                    bodyMemo.Add(key, target);
                    return operand;
                }
            }
        }

        /// <summary>
        /// Flattens a computation's arguments, checking each against its parameter's type.
        /// </summary>
        Operand[] FlattenArguments(Term[] terms, ColumnType[] parameters, Primitive primitive, bool body)
        {
            var arguments = new Operand[terms.Length];
            for (var i = 0; i < terms.Length; i++)
            {
                arguments[i] = body ? FlattenBody(terms[i]) : EvalHead(terms[i]);
                if (ReferenceEquals(arguments[i].Type, parameters[i]) == false)
                    throw Error($"primitive '{primitive.Name}' takes {parameters[i].Name} as argument {i + 1}, not {arguments[i].Type.Name}");
            }

            return arguments;
        }

        /// <summary>
        /// Step 3: equates two operands: unifies registers, binds a constant, or evaluates two constants now.
        /// </summary>
        void Equate(Operand left, Operand right)
        {
            if (left.IsRegister && right.IsRegister)
                Unify(left.Index, right.Index);
            else if (left.IsRegister)
                Bind(left.Index, right);
            else if (right.IsRegister)
                Bind(right.Index, left);
            else if (ReferenceEquals(left.Type, right.Type) == false)
                throw Error($"variables '{Literal(left)}' ({left.Type.Name}) and '{Literal(right)}' ({right.Type.Name}) are equated");
            else if (SameConstant(left, right) == false)
                throw Error(NeverMatches(left, right));
        }

        /// <summary>
        /// Merges the classes of two raw registers; their types must agree, and their constants, if both have one.
        /// </summary>
        void Unify(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb)
                return;

            if (ReferenceEquals(rawTypes[ra], rawTypes[rb]) == false)
                throw Error($"variables '{rawNames[a]}' ({rawTypes[a].Name}) and '{rawNames[b]}' ({rawTypes[b].Name}) are equated");

            var ca = constants[ra];
            var cb = constants[rb];
            if (ca is { } x && cb is { } y && SameConstant(x, y) == false)
                throw Error(NeverMatches(x, y));

            var root = Math.Min(ra, rb);
            var child = Math.Max(ra, rb);
            parent[child] = root;
            constants[root] = ca ?? cb;
        }

        /// <summary>
        /// Binds a raw register's class to a constant.
        /// </summary>
        void Bind(int register, Operand constant)
        {
            var root = Find(register);
            if (ReferenceEquals(rawTypes[root], constant.Type) == false)
                throw Error($"variables '{rawNames[register]}' ({rawTypes[register].Name}) and '{Literal(constant)}' ({constant.Type.Name}) are equated");

            if (constants[root] is { } existing && SameConstant(existing, constant) == false)
                throw Error(NeverMatches(existing, constant));

            constants[root] = constant;
        }

        /// <summary>
        /// Step 4: resolves the register classes, places the steps in rounds, and checks that every step's inputs are
        /// bound.
        /// </summary>
        void ResolveBody()
        {
            var count = rawTypes.Count;
            finalOf = new int[count];
            Array.Fill(finalOf, -1);
            registers = new List<ProgramRegister>();
            for (var i = 0; i < count; i++)
            {
                var root = Find(i);
                if (constants[root] is not null)
                    continue;

                if (finalOf[root] < 0)
                {
                    finalOf[root] = registers.Count;
                    registers.Add(new ProgramRegister(rawTypes[root], rawNames[root], rawKinds[root]));
                }

                finalOf[i] = finalOf[root];
            }

            atoms = new ProgramAtom[rawAtoms.Count];
            for (var i = 0; i < atoms.Length; i++)
                atoms[i] = new ProgramAtom(rawAtoms[i].Table, rawAtoms[i].Columns.Select(Resolve).ToArray());

            var pending = new List<ProgramStep>(rawSteps.Count);
            foreach (var (kind, primitive, arguments, target) in rawSteps)
                pending.Add(new ProgramStep(kind, primitive, arguments.Select(Resolve).ToArray(), kind == ProgramStepKind.Let ? Resolve(target) : default));

            bound = new bool[registers.Count];
            foreach (var atom in atoms)
                foreach (var column in atom.Columns)
                    if (column.IsRegister)
                        bound[column.Index] = true;

            // Rounds: each scans the pending steps in declaration order and places every step whose inputs were bound when
            // the round began; the targets the round's lets bind count from the next round.
            var placed = new List<ProgramStep>(pending.Count);
            var round = new List<ProgramStep>();
            while (pending.Count > 0)
            {
                round.Clear();
                foreach (var step in pending)
                    if (step.Arguments.All(IsBound))
                        round.Add(step);

                if (round.Count == 0)
                    break;

                foreach (var step in round)
                {
                    if (step.Kind == ProgramStepKind.Let && step.Target.IsRegister)
                        bound[step.Target.Index] = true;

                    placed.Add(step);
                    pending.Remove(step);
                }
            }

            if (pending.Count > 0)
                throw Error($"variable '{UnboundName(pending[0], pending, 0)}' is not bound by any When clause");

            steps = placed.ToArray();
        }

        /// <summary>
        /// Names the first unbound input of an unplaced step, following generated registers to the step that would have
        /// bound them.
        /// </summary>
        string UnboundName(ProgramStep step, List<ProgramStep> pending, int depth)
        {
            foreach (var argument in step.Arguments)
            {
                if (IsBound(argument))
                    continue;

                var register = registers[argument.Index];
                if (register.Kind == RegisterKind.Fresh && depth < pending.Count)
                {
                    foreach (var other in pending)
                        if (other.Kind == ProgramStepKind.Let && other.Target.IsRegister && other.Target.Index == argument.Index)
                            return UnboundName(other, pending, depth + 1);
                }

                return register.Name;
            }

            return "?";
        }

        /// <summary>
        /// Step 8: processes one head item.
        /// </summary>
        void AddHeadItem(HeadItem item)
        {
            switch (item.Kind)
            {
                case HeadItemKind.Then:
                    AddHeadFact(item.Fact!);
                    break;
                case HeadItemKind.Make:
                {
                    var operand = EvalHead(item.Left!);
                    made[item.Variable!] = operand.Index;
                    break;
                }
                default:
                    AddHeadFact(new EqualityFact(item.Variable!, item.Right!));
                    break;
            }
        }

        /// <summary>
        /// Step 8: turns a head fact into writes and unions.
        /// </summary>
        void AddHeadFact(Fact fact)
        {
            switch (fact)
            {
                case RelationFact relation:
                {
                    CheckTable(relation.Table);
                    var inputs = new Operand[relation.Arguments.Length];
                    for (var i = 0; i < inputs.Length; i++)
                        inputs[i] = EvalHead(relation.Arguments[i]);

                    head.Add(new HeadOp(HeadOpKind.Insert, relation.Table, inputs, -1, null));
                    break;
                }
                case EqualityFact equality:
                {
                    var left = equality.Left.Shape;
                    var right = equality.Right.Shape;
                    if (IsApplication(left, TableKind.Function))
                        Write(left, equality.Right);
                    else if (IsApplication(right, TableKind.Function))
                        Write(right, equality.Left);
                    else if (IsApplication(left, TableKind.Constructor) && IsApplication(right, TableKind.Constructor) == false)
                        Write(left, equality.Right);
                    else if (IsApplication(right, TableKind.Constructor) && IsApplication(left, TableKind.Constructor) == false)
                        Write(right, equality.Left);
                    else if (ResolveType(equality.Left.ClrType).Kind == ColumnKind.Sort)
                        head.Add(new HeadOp(HeadOpKind.Union, null, new[] { EvalHead(equality.Left), EvalHead(equality.Right) }, -1, null));
                    else
                        throw Error($"equality of two values that is not a function write: {equality.Left} = {equality.Right}");

                    break;
                }
                case NotEqualFact:
                    throw Error("head facts are writes; 'IsNot' is a filter");
                case ExistsFact:
                    throw Error("head facts are writes; 'Exists' is a filter");
                case FilterFact:
                    throw Error("head facts are writes; 'True' is a filter");
                default:
                    throw Error($"a {fact.GetType().Name} cannot appear in a rule");
            }
        }

        /// <summary>
        /// Writes a function or constructor row: the application's keys, then <paramref name="value"/>.
        /// </summary>
        void Write(TermShape application, Term value)
        {
            var table = application.Table!;
            CheckTable(table);
            var inputs = new Operand[application.Arguments.Length + 1];
            for (var i = 0; i < application.Arguments.Length; i++)
                inputs[i] = EvalHead(application.Arguments[i]);

            inputs[^1] = EvalHead(value);
            head.Add(new HeadOp(HeadOpKind.Write, table, inputs, -1, null));
        }

        /// <summary>
        /// Step 8: turns a head term into an operand: a bound register, a constant, a get-or-add or a computation.
        /// </summary>
        Operand EvalHead(Term term)
        {
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Variable:
                {
                    CheckOwner(shape);
                    if (declared.TryGetValue(term, out var raw))
                    {
                        var operand = Resolve(Operand.Register(raw, rawTypes[raw]));
                        if (operand.IsRegister && bound[operand.Index] == false)
                            throw Error($"variable '{shape.Name}' is not bound by any When clause");

                        return operand;
                    }

                    if (makeVariables.TryGetValue(term, out var name))
                    {
                        if (made.TryGetValue(term, out var register))
                            return Operand.Register(register, registers[register].Type);

                        throw Error($"head variable '{name}' is used before its Make");
                    }

                    throw Error($"variable '{shape.Name}' belongs to another rule");
                }
                case TermShapeKind.Constant:
                    return ConstantOperand(shape);
                case TermShapeKind.Application:
                {
                    var table = shape.Table!;
                    CheckTable(table);
                    if (table.Kind != TableKind.Constructor)
                        throw Error($"actions do not read; match '{table.Name}' in When");

                    var inputs = new Operand[shape.Arguments.Length];
                    for (var i = 0; i < inputs.Length; i++)
                        inputs[i] = EvalHead(shape.Arguments[i]);

                    var key = "g" + table.Index.ToString(CultureInfo.InvariantCulture) + Key(inputs);
                    if (headMemo.TryGetValue(key, out var shared))
                        return Operand.Register(shared, registers[shared].Type);

                    var target = NewHeadRegister(table.Output!, "$" + table.Name);
                    head.Add(new HeadOp(HeadOpKind.GetOrAdd, table, inputs, target, null));
                    headMemo.Add(key, target);
                    return Operand.Register(target, table.Output!);
                }
                default:
                {
                    var primitive = shape.Primitive!;
                    var (parameters, result) = CheckPrimitive(primitive);
                    var inputs = FlattenArguments(shape.Arguments, parameters, primitive, body: false);
                    var key = "c" + PrimitiveId(primitive).ToString(CultureInfo.InvariantCulture) + Key(inputs);
                    if (headMemo.TryGetValue(key, out var shared))
                        return Operand.Register(shared, registers[shared].Type);

                    var target = NewHeadRegister(result, "$" + primitive.Name);
                    head.Add(new HeadOp(HeadOpKind.Compute, null, inputs, target, primitive));
                    headMemo.Add(key, target);
                    return Operand.Register(target, result);
                }
            }
        }

        /// <summary>
        /// Step 7 (invariant A): a rule that writes an analysis table reads no fact table.
        /// </summary>
        /// <returns>Whether the rule writes an analysis table.</returns>
        bool CheckAnalysisWriters()
        {
            TableHandle? written = null;
            foreach (var op in head)
            {
                if (op.Kind is HeadOpKind.Insert or HeadOpKind.Write && op.Table!.Role == TableRole.Analysis)
                {
                    written = op.Table;
                    break;
                }
            }

            if (written is null)
                return false;

            foreach (var atom in atoms)
                if (atom.Table.Role == TableRole.Fact)
                    throw Error($"rule writes analysis table '{written.Name}' and may read only term and analysis tables; it reads '{atom.Table.Name}'");

            return true;
        }

        /// <summary>
        /// Step 3: turns a constant into a value constant or a class constant, checking a class (D39).
        /// </summary>
        Operand ConstantOperand(TermShape shape)
        {
            var type = shape.ConstantType ?? ResolveType(shape.ClrType);
            CheckStore(type);
            if (type.Kind == ColumnKind.Sort)
            {
                var id = type.ClassOf(shape.Constant!);
                var origin = id.Origin;
                if (origin is null)
                    throw Error("a class constant is default(ClassId), which names no class");

                if (ReferenceEquals(origin.Graph, graph) == false)
                    throw Error($"class {id} belongs to another graph");

                if (origin.IsBatch && origin.Committed == false)
                    throw Error($"class {id} was made by a Write batch that did not commit");

                var scope = origin.IsBatch ? origin.Parent! : origin;
                if (scope.Disposed)
                    throw Error($"class {id} came from scope '{scope.Path}', which is disposed");

                if (ClassChecks.AllowedInRule(owner, origin) == false)
                {
                    if (owner is null)
                        throw Error($"class {id} came from scope '{scope.Path}'; a global rule may hold only classes from the root; define the rule on that scope");

                    throw Error($"class {id} came from scope '{scope.Path}', which is not '{owner.Path}' or an ancestor of it");
                }

                if (idIndex.TryGetValue(id.Cell, out var index) == false)
                {
                    index = idCells.Count;
                    idCells.Add(id.Cell);
                    idOrigins.Add(origin);
                    idIndex.Add(id.Cell, index);
                }

                return Operand.IdConstant(index, type);
            }

            try
            {
                return Operand.Constant(type, type.EncodeObject(shape.Constant!));
            }
            catch (ArgumentException exception)
            {
                throw Error(exception.Message, exception);
            }
        }

        /// <summary>
        /// Gets the column types of a primitive's parameters and result; step 5: none may be a sort.
        /// </summary>
        (ColumnType[] Parameters, ColumnType Result) CheckPrimitive(Primitive primitive)
        {
            var parameters = new ColumnType[primitive.ParameterTypes.Count];
            for (var i = 0; i < parameters.Length; i++)
                parameters[i] = ResolveType(primitive.ParameterTypes[i]);

            var result = ResolveType(primitive.ResultType);
            if (result.Kind == ColumnKind.Sort || parameters.Any(p => p.Kind == ColumnKind.Sort))
                throw Error($"primitive '{primitive.Name}' takes or returns a sort; classes are compared only with Is and IsNot");

            return (parameters, result);
        }

        /// <summary>
        /// Gets the column type of a CLR type; step 1: an unknown type is an error.
        /// </summary>
        ColumnType ResolveType(Type clrType)
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
        /// Checks that a table belongs to the graph.
        /// </summary>
        void CheckTable(TableHandle table)
        {
            if (ReferenceEquals(table.Graph, graph) == false)
                throw Error($"table '{table.Name}' belongs to another graph");
        }

        /// <summary>
        /// Checks that a column type belongs to the graph.
        /// </summary>
        void CheckStore(ColumnType type)
        {
            if (type.Store is not null && ReferenceEquals(type.Store, graph.Values) == false)
                throw Error($"column type {type.Name} belongs to another graph");
        }

        /// <summary>
        /// Step 1: a variable belongs to this rule's builder.
        /// </summary>
        void CheckOwner(TermShape shape)
        {
            if (ReferenceEquals(shape.Owner, draft.Owner) == false)
                throw Error($"variable '{shape.Name}' belongs to another rule");
        }

        /// <summary>
        /// Gets whether a shape is an application of a table of <paramref name="kind"/>.
        /// </summary>
        static bool IsApplication(TermShape shape, TableKind kind) => shape.Kind == TermShapeKind.Application && shape.Table!.Kind == kind;

        /// <summary>
        /// Gets whether an operand is bound: a constant, or a bound register.
        /// </summary>
        bool IsBound(Operand operand) => operand.IsRegister == false || bound[operand.Index];

        /// <summary>
        /// Maps a raw operand to its final form: its class's constant, or the class's final register.
        /// </summary>
        Operand Resolve(Operand operand)
        {
            if (operand.IsRegister == false)
                return operand;

            var root = Find(operand.Index);
            if (constants[root] is { } constant)
                return constant;

            return Operand.Register(finalOf[root], rawTypes[root]);
        }

        /// <summary>
        /// Finds a raw register's class, compressing the path.
        /// </summary>
        int Find(int register)
        {
            var root = register;
            while (parent[root] != root)
                root = parent[root];

            while (parent[register] != root)
            {
                var next = parent[register];
                parent[register] = root;
                register = next;
            }

            return root;
        }

        /// <summary>
        /// Adds a raw body register.
        /// </summary>
        int NewRegister(ColumnType type, string name, RegisterKind kind)
        {
            var index = rawTypes.Count;
            rawTypes.Add(type);
            rawNames.Add(name);
            rawKinds.Add(kind);
            parent.Add(index);
            constants.Add(null);
            return index;
        }

        /// <summary>
        /// Adds a head register, after the body registers.
        /// </summary>
        int NewHeadRegister(ColumnType type, string name)
        {
            var index = registers.Count;
            registers.Add(new ProgramRegister(type, name + index.ToString(CultureInfo.InvariantCulture), RegisterKind.Head));
            return index;
        }

        /// <summary>
        /// Gets a per-compilation id for a primitive instance, for memo keys.
        /// </summary>
        int PrimitiveId(Primitive primitive)
        {
            if (primitiveIds.TryGetValue(primitive, out var id) == false)
            {
                id = primitiveIds.Count;
                primitiveIds.Add(primitive, id);
            }

            return id;
        }

        /// <summary>
        /// Makes a memo key of operands.
        /// </summary>
        static string Key(ReadOnlySpan<Operand> operands)
        {
            var builder = new StringBuilder();
            foreach (var operand in operands)
                builder.Append('|').Append((int)operand.Kind).Append(':').Append(operand.Index).Append(':').Append(operand.Bits);

            return builder.ToString();
        }

        /// <summary>
        /// Gets whether two constant operands are equal now (§6.3, <c>c1.Is(c2)</c>: evaluated now): value constants by
        /// cell (invariant V), class constants by their canonical forms in the root, which is where a global rule's class
        /// constants come from (D39). Two class constants that are already one class are therefore equal, and the rule
        /// keeps the first; constants of different classes never match.
        /// </summary>
        bool SameConstant(Operand x, Operand y)
        {
            if (x.Kind != y.Kind)
                return false;

            if (x.Kind == OperandKind.Constant)
                return x.Bits == y.Bits;

            return x.Index == y.Index || graph.Root.Engine.Find(idCells[x.Index]) == graph.Root.Engine.Find(idCells[y.Index]);
        }

        /// <summary>
        /// Renders a constant operand.
        /// </summary>
        string Literal(Operand operand) => operand.Kind switch
        {
            OperandKind.Constant => operand.Type.FormatCell(operand.Bits),
            OperandKind.IdConstant => "#" + idCells[operand.Index].ToString(CultureInfo.InvariantCulture),
            _ => rawNames[operand.Index],
        };

        /// <summary>
        /// The message of two different constants equated.
        /// </summary>
        string NeverMatches(Operand x, Operand y) => $"never matches: {Literal(x)} = {Literal(y)} is false";

        /// <summary>
        /// Makes a definition error naming the rule.
        /// </summary>
        RuleDefinitionException Error(string message, Exception? inner = null) => new(ruleName, message, inner);

    }

}
