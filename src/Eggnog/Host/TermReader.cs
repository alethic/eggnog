using System;

namespace Eggnog
{

    /// <summary>
    /// Host reads: never create terms, never intern, and answer as egglog's <c>check</c> and expression evaluation,
    /// which intern: a value no write interned, a constant or a computation's result, is a value all the same, which
    /// equals only itself and reads back as itself, and as a key it is in no row, as egglog's newly interned one is in
    /// none. Called under the scope's guard.
    /// </summary>
    internal static class TermReader
    {

        /// <summary>
        /// Reads a ground term's cell: a value constant's existing cell, a class's canonical member, or an application's
        /// row output (canonical for a class).
        /// </summary>
        /// <param name="scope">The scope read.</param>
        /// <param name="term">The term.</param>
        /// <param name="cell">The cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the term has a value in the scope.</returns>
        /// <exception cref="ArgumentException">The term is not ground, belongs to another graph, or holds a class value this
        /// scope cannot use.</exception>
        public static bool TryRead(Scope scope, Term term, out uint cell) => TryReadIn(scope, term, null, out cell);

        /// <summary>
        /// Reads a ground term's value: its cell, when it has one, or else, for a value no write interned (a constant or
        /// a computation's result), the value itself.
        /// </summary>
        /// <param name="scope">The scope read.</param>
        /// <param name="term">The term.</param>
        /// <param name="cell">The cell, when the result is <c>true</c> and <paramref name="unencoded"/> is <c>null</c>.</param>
        /// <param name="unencoded">The value, when the result is <c>true</c> and it has no cell; otherwise <c>null</c>.</param>
        /// <returns>Whether the term has a value in the scope.</returns>
        /// <exception cref="ArgumentException">The term is not ground, belongs to another graph, or holds a class value this
        /// scope cannot use.</exception>
        public static bool TryReadValue(Scope scope, Term term, out uint cell, out object? unencoded) =>
            TryReadValueIn(scope, term, null, out cell, out unencoded);

        /// <summary>
        /// <see cref="TryReadValue"/> inside <see cref="Fact.In"/> when <paramref name="enclosing"/> is set.
        /// </summary>
        static bool TryReadValueIn(Scope scope, Term term, ScopeTag? enclosing, out uint cell, out object? unencoded)
        {
            ArgumentNullException.ThrowIfNull(term);
            unencoded = null;
            var shape = term.Shape;
            if (shape.Kind == TermShapeKind.Constant)
            {
                var type = TypeOf(scope, shape);
                if (type.Kind == ColumnKind.Sort)
                    return TryReadIn(scope, term, enclosing, out cell);

                // A container of classes no write interned compares as egglog's interned one would: by its canonical
                // classes.
                if (type.HoldsClasses)
                {
                    if (type.TryEncodeCanonicalObject(shape.Constant!, scope, scope.Engine.Find, out cell) == false)
                        unencoded = type.CanonicalObject(shape.Constant!, scope, scope.Engine.Find);

                    return true;
                }

                if (type.TryEncodeExistingObject(shape.Constant!, scope.ContainerView, out cell) == false)
                    unencoded = shape.Constant!;

                return true;
            }

            if (shape.Kind == TermShapeKind.Computation)
            {
                cell = 0;
                if (TryEvaluate(scope, shape, enclosing, out var value, out var result) == false)
                    return false;

                if (result.Kind == ColumnKind.Sort)
                    return TryClassCell(scope, result, value!, out cell);

                if (result.HoldsClasses)
                {
                    if (result.TryEncodeCanonicalObject(value!, scope, scope.Engine.Find, out cell) == false)
                        unencoded = result.CanonicalObject(value!, scope, scope.Engine.Find);

                    return true;
                }

                if (result.TryEncodeExistingObject(value!, scope.ContainerView, out cell) == false)
                    unencoded = value!;

                return true;
            }

            return TryReadIn(scope, term, enclosing, out cell);
        }

        /// <summary>
        /// Reads a ground term inside <see cref="Fact.In"/>: its table applications read the scope
        /// <paramref name="enclosing"/> resolves to, directly.
        /// </summary>
        /// <param name="scope">The scope read.</param>
        /// <param name="term">The term.</param>
        /// <param name="enclosing">The tag of an enclosing <see cref="Fact.In"/>; <c>null</c> for none.</param>
        /// <param name="cell">The cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the term reads.</returns>
        static bool TryReadIn(Scope scope, Term term, ScopeTag? enclosing, out uint cell)
        {
            ArgumentNullException.ThrowIfNull(term);
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Constant:
                {
                    var type = TypeOf(scope, shape);
                    if (type.Kind == ColumnKind.Sort)
                    {
                        cell = scope.Engine.Find(ClassChecks.Require(scope, type.ClassOf(shape.Constant!), nameof(term)));
                        return true;
                    }

                    if (type.HoldsClasses)
                        return type.TryEncodeCanonicalObject(shape.Constant!, scope, scope.Engine.Find, out cell);

                    return type.TryEncodeExistingObject(shape.Constant!, scope.ContainerView, out cell);
                }
                case TermShapeKind.Application:
                {
                    var table = shape.Table!;
                    CheckTable(scope, table);
                    var read = ReadScopeOf(scope, table, enclosing);
                    var keys = new uint[shape.Arguments.Length];
                    for (var i = 0; i < keys.Length; i++)
                    {
                        if (TryReadIn(scope, shape.Arguments[i], enclosing, out keys[i]) == false)
                        {
                            cell = 0;
                            return false;
                        }
                    }

                    if (read.Engine.TryLookup(table.Function, keys, out cell) == false)
                        return false;

                    // An id canonical in an ancestor names a class of this scope, so a class output reads canonical here.
                    if (ReferenceEquals(read, scope) == false && table.Columns[^1].InvolvesClasses)
                        cell = scope.Engine.Find(cell);

                    return true;
                }
                case TermShapeKind.Variable:
                    throw new ArgumentException($"host reads are ground; '{shape.Name}' is a variable", nameof(term));
                default:
                    return TryCompute(scope, shape, enclosing, out cell);
            }
        }

        /// <summary>
        /// Evaluates a primitive's term as egglog's <c>check</c> does, without interning: over the arguments' values, a
        /// constant as it is, so that a constant no write interned still computes; then the result's cell. Where the
        /// cell is wanted, as a key, a value never interned has none, and the term is absent, as it is when the primitive
        /// has no result: egglog's newly interned value is in no row either.
        /// </summary>
        /// <exception cref="ArgumentException">A class argument or result is not usable in this scope.</exception>
        /// <exception cref="RuleEvaluationException">The primitive threw; the scope is not faulted.</exception>
        static bool TryCompute(Scope scope, TermShape shape, ScopeTag? enclosing, out uint cell)
        {
            cell = 0;
            if (TryEvaluate(scope, shape, enclosing, out var value, out var result) == false)
                return false;

            if (result.Kind == ColumnKind.Sort)
                return TryClassCell(scope, result, value!, out cell);

            if (result.HoldsClasses)
                return result.TryEncodeCanonicalObject(value!, scope, scope.Engine.Find, out cell);

            return result.TryEncodeExistingObject(value!, scope.ContainerView, out cell);
        }

        /// <summary>
        /// Reads a class a primitive returned as the scope sees it: usable there, and canonical.
        /// </summary>
        /// <exception cref="ArgumentException">The class is not usable in this scope.</exception>
        static bool TryClassCell(Scope scope, ColumnType result, object value, out uint cell)
        {
            cell = scope.Engine.Find(ClassChecks.Require(scope, result.ClassOf(value), "term"));
            return true;
        }

        /// <summary>
        /// Evaluates a primitive's term to its CLR value, on the calling thread under <see cref="EngineCallMarker"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The primitive writes tables, which a read does not, as egglog's read context
        /// of a <c>check</c> does not.</exception>
        /// <exception cref="RuleEvaluationException">The primitive threw; the scope is not faulted.</exception>
        static bool TryEvaluate(Scope scope, TermShape shape, ScopeTag? enclosing, out object? value, out ColumnType result)
        {
            value = null;
            var primitive = shape.Primitive!;
            if (Primitive.Allows(PrimitiveAccess.Read, primitive.Access) == false)
                throw new ArgumentException(Primitive.Refusal(primitive, "a host read", PrimitiveAccess.Read), "term");

            var parameters = new ColumnType[primitive.ParameterTypes.Count];
            for (var i = 0; i < parameters.Length; i++)
                parameters[i] = scope.Graph.Values.TypeOf(primitive.ParameterTypes[i]);

            result = scope.Graph.Values.TypeOf(primitive.ResultType);
            var arguments = new object[parameters.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = shape.Arguments[i].Shape;
                if (argument.Kind == TermShapeKind.Constant)
                {
                    arguments[i] = argument.Constant!;
                }
                else if (argument.Kind == TermShapeKind.Computation)
                {
                    if (TryEvaluate(scope, argument, enclosing, out var inner, out _) == false)
                        return false;

                    arguments[i] = inner!;
                }
                else
                {
                    if (TryReadIn(scope, shape.Arguments[i], enclosing, out var argumentCell) == false)
                        return false;

                    arguments[i] = parameters[i].DecodeObject(argumentCell, scope);
                }

                // A class argument must be usable here, and is seen as its canonical id, as a rule sees it; so are the
                // classes a container argument holds.
                if (parameters[i].Kind == ColumnKind.Sort)
                    arguments[i] = parameters[i].DecodeObject(scope.Engine.Find(ClassChecks.Require(scope, parameters[i].ClassOf(arguments[i]), "term")), scope);
                else if (parameters[i].HoldsClasses)
                    arguments[i] = parameters[i].CanonicalObject(arguments[i], scope, scope.Engine.Find);
            }

            bool found;
            EngineCallMarker.Enter();
            try
            {
                if (primitive.Access == PrimitiveAccess.Read || primitive.DispatchesOnContext)
                {
                    // A read primitive reads the scope's view, in egglog's read context of a check, and so does a function
                    // value's application.
                    var tables = new ReaderPrimitiveTables(scope, primitive);
                    try
                    {
                        found = primitive.TryInvokeValueTables(arguments, parameters, tables, out value);
                        if (tables.Refusal is { } refused)
                            throw refused;
                    }
                    finally
                    {
                        tables.End();
                    }
                }
                else
                {
                    found = primitive.TryInvokeValue(arguments, parameters, scope.ContainerView, out value);
                }
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

            return found;
        }

        /// <summary>
        /// Checks a ground fact: a relation row exists, two classes are equal, two values are equal, or a call holds
        /// (<see cref="Fact.Exists"/>): an application has a row, or a primitive's call has a result.
        /// </summary>
        /// <param name="scope">The scope read.</param>
        /// <param name="fact">The fact.</param>
        /// <returns>Whether the fact holds.</returns>
        /// <exception cref="ArgumentException">The fact is a filter, or a term in it is not a valid read.</exception>
        public static bool Check(Scope scope, Fact fact) => CheckIn(scope, fact, null);

        /// <summary>
        /// Checks a ground fact, inside <see cref="Fact.In"/> when <paramref name="enclosing"/> is set.
        /// </summary>
        static bool CheckIn(Scope scope, Fact fact, ScopeTag? enclosing)
        {
            ArgumentNullException.ThrowIfNull(fact);
            switch (fact)
            {
                case RelationFact relation:
                {
                    CheckTable(scope, relation.Table);
                    var read = ReadScopeOf(scope, relation.Table, enclosing);
                    var keys = new uint[relation.Arguments.Length];
                    for (var i = 0; i < keys.Length; i++)
                        if (TryReadIn(scope, relation.Arguments[i], enclosing, out keys[i]) == false)
                            return false;

                    return read.Engine.TryLookup(relation.Table.Function, keys, out _);
                }
                case EqualityFact equality:
                {
                    var left = TryReadValueIn(scope, equality.Left, enclosing, out var x, out var looseX);
                    var right = TryReadValueIn(scope, equality.Right, enclosing, out var y, out var looseY);
                    if (left == false || right == false)
                        return false;

                    if (looseX is null && looseY is null)
                        return x == y;

                    // A value without a cell compares as the codec would intern it: with another such value, or with the
                    // value of the other side's cell, since another thread may intern it between the two reads.
                    if (equality.Left.ClrType != equality.Right.ClrType)
                        return false;

                    var type = scope.Graph.Values.TypeOf(equality.Left.ClrType);
                    return type.ObjectsEqual(looseX ?? type.DecodeObject(x, scope), looseY ?? type.DecodeObject(y, scope));
                }
                case ExistsFact exists:
                    // A primitive's result no write interned still is one, which egglog's check interns.
                    return TryReadValueIn(scope, exists.Application, enclosing, out _, out _);
                case ScopedFact scoped:
                {
                    if (enclosing is not null)
                        throw new ArgumentException("Fact.In cannot be nested.", nameof(fact));

                    scope.Graph.CheckTag(scoped.Scope, nameof(fact));
                    foreach (var inner in scoped.Facts)
                        if (CheckIn(scope, inner, scoped.Scope) == false)
                            return false;

                    return true;
                }
                case NotEqualFact:
                    throw new ArgumentException("host checks are ground facts, not filters; 'IsNot' is a filter", nameof(fact));
                case FilterFact:
                    throw new ArgumentException("host checks are ground facts, not filters; 'True' is a filter", nameof(fact));
                default:
                    throw new ArgumentException($"a {fact.GetType().Name} cannot be checked", nameof(fact));
            }
        }

        /// <summary>
        /// Checks, without reading the scope's contents, that <see cref="Check"/> can test <paramref name="fact"/>: what it
        /// would refuse whatever the scope holds. With no scope, only the fact's shape: a relation row, an equality or an
        /// existence, whose terms are constants, computations and table applications (no variable). With a scope, also
        /// every table and column type is of its graph and every class constant may be used in it. Used for
        /// <c>Until</c> conditions, which <see cref="Check"/> reads only when their node executes.
        /// </summary>
        /// <param name="scope">The scope the fact will be checked in; <c>null</c> to check the shape only.</param>
        /// <param name="fact">The fact.</param>
        /// <returns>Why the fact cannot be checked, or <c>null</c> when it can.</returns>
        public static string? Validate(Scope? scope, Fact fact) => ValidateIn(scope, fact, null);

        /// <summary>
        /// Validates a fact, inside <see cref="Fact.In"/> when <paramref name="enclosing"/> is set.
        /// </summary>
        static string? ValidateIn(Scope? scope, Fact fact, ScopeTag? enclosing)
        {
            ArgumentNullException.ThrowIfNull(fact);
            switch (fact)
            {
                case RelationFact relation:
                {
                    if (scope is not null && ReferenceEquals(relation.Table.Graph, scope.Graph) == false)
                        return $"table '{relation.Table.Name}' belongs to another graph";

                    if (ValidateReadScope(scope, relation.Table, enclosing) is { } scoped)
                        return scoped;

                    foreach (var argument in relation.Arguments)
                        if (ValidateTerm(scope, argument, enclosing) is { } error)
                            return error;

                    return null;
                }
                case EqualityFact equality:
                    return ValidateTerm(scope, equality.Left, enclosing) ?? ValidateTerm(scope, equality.Right, enclosing);
                case ExistsFact exists:
                    return ValidateTerm(scope, exists.Application, enclosing);
                case ScopedFact scoped:
                {
                    if (enclosing is not null)
                        return "Fact.In cannot be nested";

                    if (scope is not null && ReferenceEquals(scoped.Scope.Graph, scope.Graph) == false)
                        return $"scope tag '{scoped.Scope.Name}' belongs to another graph";

                    foreach (var inner in scoped.Facts)
                        if (ValidateIn(scope, inner, scoped.Scope) is { } error)
                            return error;

                    return null;
                }
                case NotEqualFact:
                    return "host checks are ground facts, not filters; 'IsNot' is a filter";
                case FilterFact:
                    return "host checks are ground facts, not filters; 'True' is a filter";
                default:
                    return $"a {fact.GetType().Name} cannot be checked";
            }
        }

        /// <summary>
        /// Gets why a constant container's classes are not usable in a scope; <c>null</c> when they are, or without a
        /// scope.
        /// </summary>
        static string? CheckClasses(Scope? scope, ColumnType type, object value)
        {
            if (scope is null)
                return null;

            try
            {
                type.CanonicalObject(value, scope, static cell => cell);
                return null;
            }
            catch (ArgumentException exception)
            {
                return exception.Message;
            }
        }

        /// <summary>
        /// <see cref="Validate"/> for one term.
        /// </summary>
        static string? ValidateTerm(Scope? scope, Term term, ScopeTag? enclosing)
        {
            ArgumentNullException.ThrowIfNull(term);
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Constant:
                {
                    if (scope is null)
                        return null;

                    ColumnType type;
                    try
                    {
                        type = TypeOf(scope, shape);
                    }
                    catch (ArgumentException exception)
                    {
                        return exception.Message;
                    }

                    if (type.Kind == ColumnKind.Sort)
                        return ClassChecks.Check(scope.Origin, type.ClassOf(shape.Constant!), null);

                    return type.HoldsClasses ? CheckClasses(scope, type, shape.Constant!) : null;
                }
                case TermShapeKind.Application:
                {
                    var table = shape.Table!;
                    if (scope is not null && ReferenceEquals(table.Graph, scope.Graph) == false)
                        return $"table '{table.Name}' belongs to another graph";

                    if (ValidateReadScope(scope, table, enclosing) is { } scoped)
                        return scoped;

                    foreach (var argument in shape.Arguments)
                        if (ValidateTerm(scope, argument, enclosing) is { } error)
                            return error;

                    return null;
                }
                case TermShapeKind.Variable:
                    return $"host reads are ground; '{shape.Name}' is a variable";
                default:
                {
                    if (Primitive.Allows(PrimitiveAccess.Read, shape.Primitive!.Access) == false)
                        return Primitive.Refusal(shape.Primitive, "a host read", PrimitiveAccess.Read);

                    foreach (var argument in shape.Arguments)
                        if (ValidateTerm(scope, argument, enclosing) is { } error)
                            return error;

                    return null;
                }
            }
        }

        /// <summary>
        /// Gets the column type of a constant.
        /// </summary>
        static ColumnType TypeOf(Scope scope, TermShape shape)
        {
            var type = shape.ConstantType ?? scope.Graph.Values.TypeOf(shape.ClrType);
            if (type.Store is not null && ReferenceEquals(type.Store, scope.Graph.Values) == false)
                throw new ArgumentException($"Column type {type.Name} belongs to another graph.");

            return type;
        }

        /// <summary>
        /// Gets the scope an application of <paramref name="table"/> reads: <paramref name="scope"/> itself, or for a
        /// scoped handle or an enclosing <see cref="Fact.In"/>, the nearest ancestor-or-self tagged so, read directly
        /// rather than through a mirror. A scoped table must be value-only.
        /// </summary>
        /// <exception cref="ArgumentException">The handle is scoped to another tag than the enclosing one, or a scoped
        /// table has a sort column.</exception>
        /// <exception cref="InvalidOperationException">The tag does not resolve.</exception>
        static Scope ReadScopeOf(Scope scope, TableHandle table, ScopeTag? enclosing)
        {
            var own = table.ReadScope;
            if (own is not null && enclosing is not null && ReferenceEquals(own, enclosing) == false)
                throw new ArgumentException($"'{table.Name} @{own.Name}' is inside Fact.In('{enclosing.Name}').");

            var tag = own ?? enclosing;
            if (tag is null)
                return scope;

            if (table.IsValueOnly == false)
                throw new ArgumentException($"Scoped reads are limited to tables whose columns are all values; '{table.Name}' has a sort column.");

            return ScopeResolver.Resolve(scope, tag, "A host read");
        }

        /// <summary>
        /// Gets why an application's read scope cannot be read from <paramref name="scope"/>, or <c>null</c>.
        /// </summary>
        static string? ValidateReadScope(Scope? scope, TableHandle table, ScopeTag? enclosing)
        {
            var own = table.ReadScope;
            if (own is not null && enclosing is not null && ReferenceEquals(own, enclosing) == false)
                return $"'{table.Name} @{own.Name}' is inside Fact.In('{enclosing.Name}')";

            var tag = own ?? enclosing;
            if (tag is null)
                return null;

            if (table.IsValueOnly == false)
                return $"scoped reads are limited to tables whose columns are all values; '{table.Name}' has a sort column";

            if (scope is not null && ScopeResolver.TryResolve(scope, tag) is null)
                return $"'{scope.Path}' has no ancestor tagged '{tag.Name}'";

            return null;
        }

        /// <summary>
        /// Checks that a table belongs to the scope's graph.
        /// </summary>
        static void CheckTable(Scope scope, TableHandle table)
        {
            if (ReferenceEquals(table.Graph, scope.Graph) == false)
                throw new ArgumentException($"Table '{table.Name}' belongs to another graph.");
        }

    }

}
