using System;

namespace Eggnog
{

    /// <summary>
    /// One match of a query: a full binding of its declared variables, decoded on demand, a class canonical when it is
    /// read. Valid only during the <see cref="QueryCallback"/> call that received it.
    /// </summary>
    public readonly ref struct QueryMatch
    {

        readonly CompiledQuery? query;
        readonly Scope? scope;
        readonly ReadOnlySpan<uint> tuple;

        /// <summary>
        /// Initializes a match of <paramref name="query"/> in <paramref name="scope"/>.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <param name="scope">The scope queried, whose origin decoded classes carry.</param>
        /// <param name="tuple">The match's cells, one per declared variable.</param>
        internal QueryMatch(CompiledQuery query, Scope scope, ReadOnlySpan<uint> tuple)
        {
            this.query = query;
            this.scope = scope;
            this.tuple = tuple;
        }

        /// <summary>
        /// Gets a variable's value in this match: decoded; a class is canonical in the queried scope as the call reads it,
        /// even after a union an earlier callback made, and has that scope as its origin (D39).
        /// </summary>
        /// <typeparam name="T">The variable's type.</typeparam>
        /// <param name="variable">A declared variable of the query.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The variable is not one of the query's.</exception>
        /// <exception cref="InvalidOperationException">The match is <c>default</c>.</exception>
        public T Get<T>(Var<T> variable)
        {
            ArgumentNullException.ThrowIfNull(variable);
            var (compiled, queried) = Require();
            var column = compiled.ColumnOf(variable);
            if (compiled.Columns[column] is not ColumnType<T> type)
                throw new ArgumentException($"Variable '{variable.Name}' is not a {typeof(T).Name}.", nameof(variable));

            return type.Decode(Cell(type, queried, column), queried);
        }

        /// <summary>
        /// The untyped read (D13): a variable's value in this match, decoded, a class canonical as in
        /// <see cref="Get{T}(Var{T})"/>.
        /// </summary>
        /// <param name="variable">A declared variable of the query, typed or untyped.</param>
        /// <returns>The value; a class as its sort's value.</returns>
        /// <exception cref="ArgumentException">The variable is not one of the query's.</exception>
        internal object Get(Term variable)
        {
            ArgumentNullException.ThrowIfNull(variable);
            var (compiled, queried) = Require();
            var column = compiled.ColumnOf(variable);
            var type = compiled.Columns[column];
            return type.DecodeObject(Cell(type, queried, column), queried);
        }

        /// <summary>
        /// Gets a column's cell: a class canonical in the queried scope now. The matches were canonical when collected,
        /// but the callback may have written since (a union makes a delivered class a non-canonical member). One engine
        /// call per class read; a value cell is returned as collected.
        /// </summary>
        uint Cell(ColumnType type, Scope queried, int column) =>
            type.Kind == ColumnKind.Sort ? queried.Engine.Find(tuple[column]) : tuple[column];

        /// <summary>
        /// Gets the query and the scope of a match that is not <c>default</c>.
        /// </summary>
        (CompiledQuery Query, Scope Scope) Require()
        {
            if (query is null || scope is null)
                throw new InvalidOperationException("This match is default(QueryMatch), which belongs to no query.");

            return (query, scope);
        }

    }

}
