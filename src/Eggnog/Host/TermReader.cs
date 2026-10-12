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
        public static bool TryRead(Scope scope, Term term, out uint cell)
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
                    var keys = new uint[shape.Arguments.Length];
                    for (var i = 0; i < keys.Length; i++)
                    {
                        if (TryRead(scope, shape.Arguments[i], out keys[i]) == false)
                        {
                            cell = 0;
                            return false;
                        }
                    }

                    return scope.Engine.TryLookup(table.Function, keys, out cell);
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
        public static bool Check(Scope scope, Fact fact)
        {
            ArgumentNullException.ThrowIfNull(fact);
            switch (fact)
            {
                case RelationFact relation:
                {
                    CheckTable(scope, relation.Table);
                    var keys = new uint[relation.Arguments.Length];
                    for (var i = 0; i < keys.Length; i++)
                        if (TryRead(scope, relation.Arguments[i], out keys[i]) == false)
                            return false;

                    return scope.Engine.TryLookup(relation.Table.Function, keys, out _);
                }
                case EqualityFact equality:
                {
                    var left = TryRead(scope, equality.Left, out var x);
                    var right = TryRead(scope, equality.Right, out var y);
                    return left && right && x == y;
                }
                case ExistsFact exists:
                    return TryRead(scope, exists.Application, out _);
                case NotEqualFact:
                    throw new ArgumentException("host checks are ground facts, not filters; 'IsNot' is a filter", nameof(fact));
                case FilterFact:
                    throw new ArgumentException("host checks are ground facts, not filters; 'True' is a filter", nameof(fact));
                default:
                    throw new ArgumentException($"a {fact.GetType().Name} cannot be checked", nameof(fact));
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
        /// Checks that a table belongs to the scope's graph.
        /// </summary>
        static void CheckTable(Scope scope, TableHandle table)
        {
            if (ReferenceEquals(table.Graph, scope.Graph) == false)
                throw new ArgumentException($"Table '{table.Name}' belongs to another graph.");
        }

    }

}
