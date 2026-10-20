using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Eggnog
{

    /// <summary>
    /// One host write batch: <c>Make</c>, <c>Assume</c>, <c>Union</c> and <c>Subsume</c> are batches of one call;
    /// <see cref="Scope.Write"/> makes larger ones. Each call is planned as it is made, reading only (plus minting fresh
    /// ids); <see cref="Commit"/> stages and flushes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 1 (planning) validates facts, checks class values, encodes constants (interning), evaluates primitives and
    /// reads function values, as egglog's top-level actions do (a read sees the batch's earlier writes, merged, as
    /// egglog's next command would; after the batch merges classes, a read of a function with a sort key is refused,
    /// since the merges are staged after the writes), resolves existing terms by lookup, mints a fresh id per new term,
    /// records only the rows, subsumptions and unions that do not hold already, and checks conflicts of functions
    /// without a lattice, including those a merge of the batch would cause (see <c>HostBatch.Merges.cs</c>). An
    /// exception discards the batch: nothing is staged and <see cref="Origin"/> never commits. A
    /// <see cref="ScopeWriter"/> call that throws marks the batch <see cref="IsRejected"/>, and
    /// <see cref="Scope.Write"/> then commits nothing, even when its callback caught the exception.
    /// </para>
    /// <para>
    /// Phase 2 (<see cref="Commit"/>) stages the new terms' rows children first (subsumed where the batch subsumes the
    /// term), then the rows, then the subsumed rows, then the unions, and flushes, which merges and rebuilds. A batch
    /// whose facts all hold stages nothing and is never flushed, so it moves neither the scope's version nor the clock.
    /// Not thread-safe: used under the scope's guard, except <see cref="Reject"/>, which any thread may call.
    /// </para>
    /// </remarks>
    internal sealed partial class HostBatch
    {

        readonly Scope scope;
        readonly EngineScope engine;
        readonly EGraph graph;
        readonly List<PendingTerm> pending = new();
        readonly Dictionary<string, PendingTerm> pendingByKey = new(StringComparer.Ordinal);
        readonly HashSet<uint> fresh = new();
        readonly List<(TableHandle Table, uint[] Row)> rows = new();
        readonly HashSet<string> rowKeys = new(StringComparer.Ordinal);
        readonly Dictionary<string, uint> functionValues = new(StringComparer.Ordinal);
        readonly Dictionary<string, uint> currentValues = new(StringComparer.Ordinal);
        readonly List<(TableHandle Table, uint[] Row)> subsumes = new();
        readonly HashSet<string> subsumeKeys = new(StringComparer.Ordinal);
        readonly HashSet<string> subsumedTerms = new(StringComparer.Ordinal);
        readonly List<(uint A, uint B)> unions = new();
        readonly HashSet<(uint, uint)> unionKeys = new();
        volatile bool rejected;

        /// <summary>
        /// Initializes an empty batch of <paramref name="scope"/>.
        /// </summary>
        /// <param name="scope">The scope written.</param>
        public HostBatch(Scope scope)
        {
            this.scope = scope;
            engine = scope.Engine;
            graph = scope.Graph;
            Origin = new ScopeOrigin(graph, scope.Origin, scope.Path, true);
        }

        /// <summary>
        /// Gets the batch's origin: the values its <c>Make</c> calls return are valid in later calls of the batch,
        /// and once it commits.
        /// </summary>
        public ScopeOrigin Origin { get; }

        /// <summary>
        /// Gets whether there is nothing to stage: every fact held already.
        /// </summary>
        public bool IsEmpty => pending.Count == 0 && rows.Count == 0 && subsumes.Count == 0 && unions.Count == 0;

        /// <summary>
        /// Gets whether a call of the batch was rejected (<see cref="Reject"/>): the batch must then write nothing.
        /// </summary>
        public bool IsRejected => rejected;

        /// <summary>
        /// Marks the batch rejected: a call of it threw, so nothing of it may be written (see
        /// <see cref="Scope.Write"/>). Any thread may call it, including one that a guard refused.
        /// </summary>
        public void Reject() => rejected = true;

        /// <summary>
        /// Plans a ground term and gets its class or value: an existing class (looked up), or a fresh id minted now for a
        /// new term, memoised by head and keys so a batch mints each new term once; a function's value, read as the batch
        /// has written it so far; or a primitive's result, interned if it is new.
        /// </summary>
        /// <param name="term">The term.</param>
        /// <returns>The cell: canonical, or fresh.</returns>
        /// <exception cref="ArgumentException">The term is not ground, applies a relation, belongs to another graph, or
        /// holds a class value this scope cannot use.</exception>
        /// <exception cref="InvalidOperationException">A function it reads has no value for its keys.</exception>
        /// <exception cref="RuleEvaluationException">A primitive it computes threw, or has no result.</exception>
        public uint PlanTerm(Term term)
        {
            ArgumentNullException.ThrowIfNull(term);
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Constant:
                {
                    var type = TypeOf(shape);
                    if (type.Kind == ColumnKind.Sort)
                    {
                        var cell = ClassChecks.Require(scope, type.ClassOf(shape.Constant!), nameof(term), Origin);
                        return fresh.Contains(cell) ? cell : engine.Find(cell);
                    }

                    return type.EncodeObject(shape.Constant!);
                }
                case TermShapeKind.Application:
                {
                    var table = shape.Table!;
                    CheckTable(table);
                    if (table.Kind == TableKind.Relation)
                        throw new ArgumentException($"'{table.Name}' is a relation, whose rows are facts, not terms", nameof(term));

                    var keys = PlanKeys(table, shape.Arguments, out var existing);
                    if (table.Kind == TableKind.Function)
                        return ReadFunction(table, keys, existing);

                    if (existing && engine.TryLookup(table.Function, keys, out var output))
                        return output;

                    return PlanNewTerm((ConstructorHandle)table, keys, Key(table, keys));
                }
                case TermShapeKind.Variable:
                    throw new ArgumentException($"host terms are ground; '{shape.Name}' is a variable", nameof(term));
                default:
                    return Compute(shape);
            }
        }

        /// <summary>
        /// Reads a function's value for planned keys, as egglog's top-level lookup does: the value the batch's earlier
        /// writes left, merged as the engine will merge them, else the stored one.
        /// </summary>
        /// <exception cref="InvalidOperationException">The function has no value for the keys; or it has a sort key, and
        /// the batch merged classes before the read.</exception>
        uint ReadFunction(TableHandle table, uint[] keys, bool existing)
        {
            // egglog's next command would read the function after the merge's rebuild, which merges its rows whose keys
            // become equal; the batch stages its merges after its writes, so it cannot read the function that way.
            if (mergedClasses && HasSortKey(table))
                throw new InvalidOperationException(
                    $"'{table.Name}' is read after this batch merged classes, and its keys are classes; read it in a later call, which sees the merge as egglog's next command does.");

            if (currentValues.TryGetValue(Key(table, keys), out var current))
                return current;

            if (existing && engine.TryLookup(table.Function, keys, out var stored))
                return stored;

            var text = new StringBuilder("(").Append(table.Name);
            for (var i = 0; i < keys.Length; i++)
                text.Append(' ').Append(table.KeyArray[i].FormatCell(keys[i]));

            throw new InvalidOperationException(text.Append(") has no value here.").ToString());
        }

        /// <summary>
        /// Evaluates a primitive over its planned arguments, as egglog's top-level actions do, on the calling thread under
        /// <see cref="EngineCallMarker"/>; the result is interned if it is new, as any value a write holds.
        /// </summary>
        /// <exception cref="ArgumentException">The primitive takes or returns a class.</exception>
        /// <exception cref="RuleEvaluationException">The primitive threw, or has no result; nothing is written and the
        /// scope is not faulted.</exception>
        uint Compute(TermShape shape)
        {
            var primitive = shape.Primitive!;
            var parameters = new ColumnType[primitive.ParameterTypes.Count];
            for (var i = 0; i < parameters.Length; i++)
                parameters[i] = ValueTypeOf(primitive.ParameterTypes[i], primitive);

            var result = ValueTypeOf(primitive.ResultType, primitive);
            var arguments = new uint[parameters.Length];
            for (var i = 0; i < arguments.Length; i++)
                arguments[i] = PlanTerm(shape.Arguments[i]);

            bool found;
            uint cell;
            EngineCallMarker.Enter();
            try
            {
                found = primitive.TryInvokeCore(arguments, parameters, result, out cell);
            }
            catch (Exception exception)
            {
                throw new RuleEvaluationException(null, null,
                    $"primitive '{primitive.Name}' threw {exception.GetType().Name}: {exception.Message}", exception);
            }
            finally
            {
                EngineCallMarker.Exit();
            }

            if (found == false)
                throw new RuleEvaluationException(null, null, $"primitive '{primitive.Name}' has no result for these arguments", null);

            return cell;
        }

        /// <summary>
        /// Gets the value type of a primitive's parameter or result.
        /// </summary>
        /// <exception cref="ArgumentException">The type is a sort: primitives never take or return classes.</exception>
        ColumnType ValueTypeOf(Type clrType, Primitive primitive)
        {
            var type = graph.Values.TypeOf(clrType);
            if (type.Kind == ColumnKind.Sort)
                throw new ArgumentException($"primitive '{primitive.Name}' takes or returns the sort {type.Name}; primitives are over values only");

            return type;
        }

        /// <summary>
        /// Plans a ground fact: a relation row, a function value, a constructor output, or a union. Facts that
        /// hold already plan nothing.
        /// </summary>
        /// <param name="fact">The fact.</param>
        /// <exception cref="ArgumentException">The fact is not a write.</exception>
        /// <exception cref="FactConflictException">A function without a lattice would get a second value.</exception>
        /// <exception cref="RuleEvaluationException">A lattice's merge threw while checking whether the fact
        /// holds.</exception>
        public void PlanFact(Fact fact)
        {
            ArgumentNullException.ThrowIfNull(fact);
            switch (fact)
            {
                case RelationFact relation:
                {
                    CheckTable(relation.Table);
                    var keys = PlanKeys(relation.Table, relation.Arguments, out var existing);
                    if (existing && engine.TryLookup(relation.Table.Function, keys, out _))
                        return;

                    var row = new uint[keys.Length + 1];
                    keys.CopyTo(row, 0);
                    row[^1] = graph.Unit.Unit;
                    AddRow(relation.Table, row);
                    break;
                }
                case EqualityFact equality:
                {
                    var left = equality.Left.Shape;
                    var right = equality.Right.Shape;
                    if (IsApplication(left, TableKind.Function) && IsValue(right))
                        PlanFunctionWrite(left, equality.Right);
                    else if (IsApplication(right, TableKind.Function) && IsValue(left))
                        PlanFunctionWrite(right, equality.Left);
                    else if (IsApplication(left, TableKind.Constructor) && IsClassConstant(right))
                        PlanConstructorWrite(left, equality.Right);
                    else if (IsApplication(right, TableKind.Constructor) && IsClassConstant(left))
                        PlanConstructorWrite(right, equality.Left);
                    else if (TypeOf(equality.Left.ClrType).Kind == ColumnKind.Sort && IsApplication(left, TableKind.Function) == false && IsApplication(right, TableKind.Function) == false)
                        PlanUnion(PlanTerm(equality.Left), PlanTerm(equality.Right));
                    else
                        throw new ArgumentException($"value equality is not a write: {equality.Left} = {equality.Right}", nameof(fact));

                    break;
                }
                case NotEqualFact:
                    throw new ArgumentException("host writes are ground facts; 'IsNot' is a filter", nameof(fact));
                case ExistsFact:
                    throw new ArgumentException("host writes are ground facts; 'Exists' is a check, not a write", nameof(fact));
                case FilterFact:
                    throw new ArgumentException("host writes are ground facts; 'True' is a filter", nameof(fact));
                case ScopedFact scoped:
                    throw new ArgumentException($"host writes go to the scope written; Fact.In('{scoped.Scope.Name}') is read-only", nameof(fact));
                default:
                    throw new ArgumentException($"a {fact.GetType().Name} is not a host write", nameof(fact));
            }
        }

        /// <summary>
        /// Plans the subsumption of a ground constructor term (egglog's <c>subsume</c>): a term whose row is not
        /// subsumed yet gets a subsumed copy of its row, which the constructor's merge makes sticky; a new term is
        /// planned as by <see cref="PlanTerm"/> and staged subsumed; a term already subsumed plans nothing.
        /// </summary>
        /// <param name="term">The term: an application of a subsumable constructor.</param>
        /// <exception cref="ArgumentException">The constructor is not subsumable, or a subterm reads a function,
        /// computes, is a variable, belongs to another graph, or holds a class value this scope cannot use.</exception>
        public void PlanSubsume(Term term)
        {
            ArgumentNullException.ThrowIfNull(term);
            var shape = term.Shape;
            if (shape.Kind != TermShapeKind.Application || shape.Table!.Kind != TableKind.Constructor)
                throw new ArgumentException($"Subsume takes a constructor term; {term} is not one", nameof(term));

            var table = shape.Table;
            CheckTable(table);
            CheckSubsumable(table, nameof(term));
            var keys = PlanKeys(table, shape.Arguments, out var existing);
            if (existing && engine.TryLookup(table.Function, keys, out var output, out var subsumed))
            {
                if (subsumed == false)
                    AddSubsume(table, Row(keys, output));

                return;
            }

            var key = Key(table, keys);
            if (pendingByKey.ContainsKey(key) == false)
                PlanNewTerm((ConstructorHandle)table, keys, key);

            subsumedTerms.Add(key);
        }

        /// <summary>
        /// Plans the subsumption of a ground relation row (egglog's <c>subsume</c>): a row that is not subsumed yet, or
        /// is missing, gets a subsumed row, which the relation's merge makes sticky; a row already subsumed plans
        /// nothing.
        /// </summary>
        /// <param name="fact">The row: <c>r.Of(a, b)</c> of a subsumable relation.</param>
        /// <exception cref="ArgumentException">The fact is not a relation row of the scope written, the relation is not
        /// subsumable, or a term in it is not a ground value or class this scope can use.</exception>
        public void PlanSubsume(Fact fact)
        {
            ArgumentNullException.ThrowIfNull(fact);
            if (fact is not RelationFact relation)
            {
                throw fact is ScopedFact scoped
                    ? new ArgumentException($"host writes go to the scope written; Fact.In('{scoped.Scope.Name}') is read-only", nameof(fact))
                    : new ArgumentException($"Subsume(rows) takes relation rows, r.Of(...); {fact} is not one (subsume a term with Subsume(term))", nameof(fact));
            }

            var table = relation.Table;
            CheckTable(table);
            CheckSubsumable(table, nameof(fact));
            var keys = PlanKeys(table, relation.Arguments, out var existing);
            if (existing && engine.TryLookup(table.Function, keys, out _, out var subsumed) && subsumed)
                return;

            AddSubsume(table, Row(keys, graph.Unit.Unit));
        }

        /// <summary>
        /// Plans a union, unless the classes are equal already; repeated unions are planned once. A union that would
        /// merge two keys of a function without a lattice holding different values is a conflict.
        /// </summary>
        /// <param name="a">One class: canonical or fresh.</param>
        /// <param name="b">The other class.</param>
        /// <exception cref="FactConflictException">The union would give a function without a lattice two values for
        /// one key.</exception>
        public void PlanUnion(uint a, uint b)
        {
            var x = fresh.Contains(a) ? a : engine.Find(a);
            var y = fresh.Contains(b) ? b : engine.Find(b);
            if (x == y)
                return;

            var key = x < y ? (x, y) : (y, x);
            if (unionKeys.Add(key))
            {
                unions.Add(key);
                NoteMerge(x, y);
            }
        }

        /// <summary>
        /// Stages the new terms' rows children first (subsumed where the batch subsumes the term), then the rows, then
        /// the subsumed rows, then the unions; flushes; commits <see cref="Origin"/>. Does nothing when
        /// <see cref="IsEmpty"/>.
        /// </summary>
        /// <returns>Whether the scope changed.</returns>
        /// <exception cref="Relations.PanicException">A merge recorded a failure.</exception>
        public bool Commit()
        {
            if (IsEmpty)
                return false;

            foreach (var term in pending)
            {
                var row = new uint[term.Keys.Length + 1];
                term.Keys.CopyTo(row, 0);
                row[^1] = term.Id;
                var subsume = subsumedTerms.Count > 0 && subsumedTerms.Contains(Key(term.Head, term.Keys));
                engine.StageRow(term.Head.Function, row, subsume);
            }

            foreach (var (table, row) in rows)
                engine.StageRow(table.Function, row);

            foreach (var (table, row) in subsumes)
                engine.StageRow(table.Function, row, subsume: true);

            foreach (var (a, b) in unions)
                engine.StageUnion(a, b);

            var changed = engine.Flush();
            Origin.Committed = true;
            return changed;
        }

        /// <summary>
        /// Plans <c>F.Of(k).Is(v)</c> for a function F and a constant v. A second value for a key of a function without
        /// a lattice, stored or planned in the batch, is a conflict; a value the stored one absorbs under the lattice
        /// plans nothing.
        /// </summary>
        void PlanFunctionWrite(TermShape application, Term value)
        {
            var table = (FunctionHandle)application.Table!;
            CheckTable(table);
            var keys = PlanKeys(table, application.Arguments, out var existing);
            var cell = PlanTerm(value);
            var key = Key(table, keys);
            if (functionValues.TryGetValue(key, out var earlier))
            {
                if (earlier == cell)
                    return;

                if (table.Lattice is null)
                    throw new FactConflictException(table, DecodeKeys(table, keys), Decode(table.Output!, earlier), Decode(table.Output!, cell));
            }
            else if (existing && engine.TryLookup(table.Function, keys, out var stored))
            {
                if (table.Lattice is null)
                {
                    if (stored == cell)
                        return;

                    throw new FactConflictException(table, DecodeKeys(table, keys), Decode(table.Output!, stored), Decode(table.Output!, cell));
                }

                if (Absorbs(table, stored, cell))
                    return;
            }

            // The value a later read in the batch sees: the incoming one, merged by the lattice with the value before it.
            if (table.Lattice is null)
                currentValues[key] = cell;
            else if (currentValues.TryGetValue(key, out var before) || (existing && engine.TryLookup(table.Function, keys, out before)))
                currentValues[key] = MergeCells(table, before, cell);
            else
                currentValues[key] = cell;

            functionValues[key] = cell;
            var row = new uint[keys.Length + 1];
            keys.CopyTo(row, 0);
            row[^1] = cell;
            AddRow(table, row);
        }

        /// <summary>
        /// Merges two values of a function with a lattice, on the calling thread under <see cref="EngineCallMarker"/>.
        /// </summary>
        /// <exception cref="RuleEvaluationException">The merge threw; nothing is written and the scope is not
        /// faulted.</exception>
        uint MergeCells(FunctionHandle table, uint stored, uint incoming)
        {
            EngineCallMarker.Enter();
            try
            {
                return table.Lattice!.MergeCells(table.Output!, stored, incoming);
            }
            catch (Exception exception) when (exception is not RuleEvaluationException)
            {
                throw new RuleEvaluationException(null, null,
                    $"lattice '{table.Lattice!.Name}' of '{table.Name}' threw {exception.GetType().Name}: {exception.Message}", exception);
            }
            finally
            {
                EngineCallMarker.Exit();
            }
        }

        /// <summary>
        /// Plans <c>C.Of(k).Is(x)</c> for a constructor C and a class constant x: a new key becomes a pending term
        /// whose class is x; an existing key with another class is a row that the union-id merge unions with x.
        /// </summary>
        void PlanConstructorWrite(TermShape application, Term value)
        {
            var table = (ConstructorHandle)application.Table!;
            CheckTable(table);
            var keys = PlanKeys(table, application.Arguments, out var existing);
            var cell = PlanTerm(value);
            if (existing && engine.TryLookup(table.Function, keys, out var stored))
            {
                if (stored == cell)
                    return;

                var row = new uint[keys.Length + 1];
                keys.CopyTo(row, 0);
                row[^1] = cell;
                AddRow(table, row);
                NoteMerge(stored, cell);
                return;
            }

            var key = Key(table, keys);
            if (pendingByKey.TryGetValue(key, out var planned))
            {
                PlanUnion(planned.Id, cell);
                return;
            }

            var term = new PendingTerm(table, keys, cell);
            pending.Add(term);
            pendingByKey.Add(key, term);
            TrackRow(table, keys, cell);
        }

        /// <summary>
        /// Plans a new term: mints a fresh id for it, memoised by <paramref name="key"/> so that a batch mints each new
        /// term once.
        /// </summary>
        /// <returns>The fresh id.</returns>
        uint PlanNewTerm(ConstructorHandle table, uint[] keys, string key)
        {
            if (pendingByKey.TryGetValue(key, out var planned))
                return planned.Id;

            var id = engine.FreshId();
            fresh.Add(id);
            var term = new PendingTerm(table, keys, id);
            pending.Add(term);
            pendingByKey.Add(key, term);
            TrackRow(table, keys, id);
            return id;
        }

        /// <summary>
        /// Records a subsumed row once, and adds it to the congruence check as <see cref="AddRow"/> does.
        /// </summary>
        void AddSubsume(TableHandle table, uint[] row)
        {
            if (subsumeKeys.Add(Key(table, row)))
            {
                subsumes.Add((table, row));
                TrackRow(table, row);
            }
        }

        /// <summary>
        /// Checks that a table may be subsumed.
        /// </summary>
        static void CheckSubsumable(TableHandle table, string parameter)
        {
            if (table.Subsumable == false)
                throw new ArgumentException($"'{table.Name}' is not subsumable; define it with TableOptions.Subsumable to subsume its rows.", parameter);
        }

        /// <summary>
        /// Makes a row of keys then an output.
        /// </summary>
        static uint[] Row(uint[] keys, uint output)
        {
            var row = new uint[keys.Length + 1];
            keys.CopyTo(row, 0);
            row[^1] = output;
            return row;
        }

        /// <summary>
        /// Plans the key terms of an application and reports whether every sort key names an existing class (a key that
        /// is a fresh id of this batch cannot have a row yet).
        /// </summary>
        uint[] PlanKeys(TableHandle table, Term[] arguments, out bool existing)
        {
            var keys = new uint[arguments.Length];
            existing = true;
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = PlanTerm(arguments[i]);
                if (table.KeyArray[i].Kind == ColumnKind.Sort && fresh.Contains(keys[i]))
                    existing = false;
            }

            return keys;
        }

        /// <summary>
        /// Records a row once.
        /// </summary>
        void AddRow(TableHandle table, uint[] row)
        {
            if (rowKeys.Add(Key(table, row)))
            {
                rows.Add((table, row));
                TrackRow(table, row);
            }
        }

        /// <summary>
        /// Gets whether a lattice write of <paramref name="incoming"/> leaves <paramref name="stored"/> unchanged, running
        /// the lattice's merge on the calling thread under <see cref="EngineCallMarker"/>.
        /// </summary>
        /// <exception cref="RuleEvaluationException">The merge threw; nothing is written and the scope is not
        /// faulted.</exception>
        bool Absorbs(FunctionHandle table, uint stored, uint incoming)
        {
            EngineCallMarker.Enter();
            try
            {
                return table.Lattice!.Absorbs(table.Output!, stored, incoming);
            }
            catch (Exception exception)
            {
                throw new RuleEvaluationException(null, null,
                    $"the lattice '{table.Lattice!.Name}' of function '{table.Name}' threw {exception.GetType().Name}: {exception.Message}", exception);
            }
            finally
            {
                EngineCallMarker.Exit();
            }
        }

        /// <summary>
        /// Gets the column type of a constant.
        /// </summary>
        ColumnType TypeOf(TermShape shape)
        {
            var type = shape.ConstantType ?? graph.Values.TypeOf(shape.ClrType);
            if (type.Store is not null && ReferenceEquals(type.Store, graph.Values) == false)
                throw new ArgumentException($"Column type {type.Name} belongs to another graph.");

            return type;
        }

        /// <summary>
        /// Gets the column type of a CLR type.
        /// </summary>
        ColumnType TypeOf(Type clrType) => graph.Values.TypeOf(clrType);

        /// <summary>
        /// Checks that a table belongs to the scope's graph and is not a read-only scoped view.
        /// </summary>
        void CheckTable(TableHandle table)
        {
            if (ReferenceEquals(table.Graph, graph) == false)
                throw new ArgumentException($"Table '{table.Name}' belongs to another graph.");

            // Writes are never scoped: a host write goes to the scope written, never through a scoped view.
            if (table.ReadScope is { } tag)
                throw new ArgumentException($"'{table.Name}@{tag.Name}' is read-only; write '{table.Name}' in the scope itself.");
        }

        /// <summary>
        /// Gets whether a shape is a class constant.
        /// </summary>
        bool IsClassConstant(TermShape shape) => shape.Kind == TermShapeKind.Constant && TypeOf(shape).Kind == ColumnKind.Sort;

        /// <summary>
        /// Gets whether a shape is an application of a table of <paramref name="kind"/>.
        /// </summary>
        static bool IsApplication(TermShape shape, TableKind kind) => shape.Kind == TermShapeKind.Application && shape.Table!.Kind == kind;

        /// <summary>
        /// Gets whether a shape stands for a value a function may be written: a constant, a computation, or a function's
        /// value read.
        /// </summary>
        static bool IsValue(TermShape shape) =>
            shape.Kind is TermShapeKind.Constant or TermShapeKind.Computation || IsApplication(shape, TableKind.Function);

        /// <summary>
        /// Decodes key cells for an exception.
        /// </summary>
        object[] DecodeKeys(TableHandle table, uint[] keys)
        {
            var decoded = new object[keys.Length];
            for (var i = 0; i < keys.Length; i++)
                decoded[i] = Decode(table.KeyArray[i], keys[i]);

            return decoded;
        }

        /// <summary>
        /// Decodes a cell, a class with the scope's origin.
        /// </summary>
        object Decode(ColumnType type, uint cell) => type.DecodeObject(cell, scope);

        /// <summary>
        /// Makes a memo key of a table and cells.
        /// </summary>
        static string Key(TableHandle table, uint[] cells)
        {
            var builder = new StringBuilder();
            builder.Append(table.Index.ToString(CultureInfo.InvariantCulture));
            foreach (var cell in cells)
                builder.Append(':').Append(cell.ToString(CultureInfo.InvariantCulture));

            return builder.ToString();
        }

    }

}
