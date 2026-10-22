using System;

namespace Eggnog
{

    /// <summary>
    /// Reads of the tables a call sees: egglog's <c>Read</c> methods, which a read primitive's state offers
    /// (<see cref="PrimitiveAccess.Read"/>), and a full rule callback's (<see cref="FullRuleCallbackContext"/>). Values
    /// are of their columns' types, a class a sort value usable in the scope the call runs in. Valid only during the call.
    /// </summary>
    public interface ITableReader
    {

        /// <summary>
        /// Reads a function's value, or a constructor's class, at a key, without inserting: egglog's <c>lookup</c>.
        /// </summary>
        /// <typeparam name="T">The output's type.</typeparam>
        /// <param name="table">The function or constructor, of the call's graph.</param>
        /// <param name="keys">One value per key column.</param>
        /// <param name="value">The value, when the key has a row.</param>
        /// <returns>Whether the key has a row.</returns>
        /// <exception cref="ArgumentException">The table is a relation or not of the call's graph, or a value is not of
        /// its column's type.</exception>
        bool TryLookup<T>(TableHandle table, ReadOnlySpan<object> keys, out T value);

        /// <summary>
        /// Gets whether a relation holds a row.
        /// </summary>
        /// <param name="relation">The relation, of the call's graph.</param>
        /// <param name="keys">One value per column.</param>
        /// <returns>Whether the row is there.</returns>
        /// <exception cref="ArgumentException">The relation is not of the call's graph, or a value is not of its column's
        /// type.</exception>
        bool Contains(RelationHandle relation, params ReadOnlySpan<object> keys);

        /// <summary>
        /// Gets the number of rows of a table the call sees: egglog's <c>table_size</c>.
        /// </summary>
        /// <param name="table">The table, of the call's graph.</param>
        /// <returns>The number of rows.</returns>
        /// <exception cref="ArgumentException">The table is not of the call's graph.</exception>
        long TableSize(TableHandle table);

    }

}
