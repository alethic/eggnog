using System;

namespace Eggnog
{

    /// <summary>
    /// Host reads (§6.6.3): never create terms, never intern (D15). A constant that was never interned makes its fact
    /// false or its term absent. Called under the scope's guard.
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
        /// <exception cref="ArgumentException">The term is not ground, computes, belongs to another graph, or holds a class
        /// value this scope cannot use.</exception>
        public static bool TryRead(Scope scope, Term term, out uint cell) => TryReadIn(scope, term, null, out cell);

        /// <summary>
        /// Reads a ground term inside <see cref="Fact.In"/>: its table applications read the scope
        /// <paramref name="enclosing"/> resolves to, directly (§6.6.3).
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

                    return type.TryEncodeExistingObject(shape.Constant!, out cell);
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
                    if (ReferenceEquals(read, scope) == false && table.Columns[^1].Kind == ColumnKind.Sort)
                        cell = scope.Engine.Find(cell);

                    return true;
                }
                case TermShapeKind.Variable:
                    throw new ArgumentException($"host reads are ground; '{shape.Name}' is a variable", nameof(term));
                default:
                    throw new ArgumentException($"host reads do not compute; '{shape.Primitive!.Name}' is a computation", nameof(term));
            }
        }

        /// <summary>
        /// Checks a ground fact (§6.6.3): a relation row exists, two classes are equal, two values are equal, or an
        /// application has a row.
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
                    var left = TryReadIn(scope, equality.Left, enclosing, out var x);
                    var right = TryReadIn(scope, equality.Right, enclosing, out var y);
                    return left && right && x == y;
                }
                case ExistsFact exists:
                    return TryReadIn(scope, exists.Application, enclosing, out _);
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
        /// existence, whose terms are constants and table applications (no variable, no computation). With a scope, also
        /// every table and column type is of its graph and every class constant may be used in it (D39). Used for
        /// <c>Until</c> conditions, which <see cref="Check"/> reads only when their node executes (§6.5.3).
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

                    return type.Kind == ColumnKind.Sort ? ClassChecks.Check(scope.Origin, type.ClassOf(shape.Constant!), null) : null;
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
                    return $"host reads do not compute; '{shape.Primitive!.Name}' is a computation";
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
        /// Gets the scope an application of <paramref name="table"/> reads (§6.6.3): <paramref name="scope"/> itself, or for
        /// a scoped handle or an enclosing <see cref="Fact.In"/>, the nearest ancestor-or-self tagged so, read directly
        /// rather than through a mirror. A scoped table must be value-only (D1).
        /// </summary>
        /// <exception cref="ArgumentException">The handle is scoped to another tag than the enclosing one, or a scoped
        /// table has a sort column.</exception>
        /// <exception cref="InvalidOperationException">The tag does not resolve.</exception>
        static Scope ReadScopeOf(Scope scope, TableHandle table, ScopeTag? enclosing)
        {
            var own = table.ReadScope;
            if (own is not null && enclosing is not null && ReferenceEquals(own, enclosing) == false)
                throw new ArgumentException($"'{table.Name}@{own.Name}' is inside Fact.In('{enclosing.Name}').");

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
                return $"'{table.Name}@{own.Name}' is inside Fact.In('{enclosing.Name}')";

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
