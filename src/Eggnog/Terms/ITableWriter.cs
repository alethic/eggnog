using System;

namespace Eggnog
{

    /// <summary>
    /// Writes to the tables a call runs in: egglog's <c>Write</c> methods, which a write primitive's state offers
    /// (<see cref="PrimitiveAccess.Write"/>), and a rule callback's (<see cref="RuleCallbackContext"/>). A write is staged as
    /// egglog stages an action's: in a rule or a merge with the run's other writes, seen after the run; in a host write
    /// until the end of the fact, then planned as the fact it is. Its table is checked when it is made: a function whose
    /// merge is not a lattice is written only at the root, a deletion only where the scope may delete, a subsumption only
    /// of a row a subsumable table holds, and no scoped view is written; in a rule or a merge no analysis table either,
    /// since an analysis depends only on terms. A host write's other checks, such as of a second value for a key, apply
    /// when it plans the write; and a primitive in a deletion's keys writes nothing but terms, as the deletion's batch only
    /// deletes. Values are of their columns' types, a class a sort value usable in the scope the call runs
    /// in. Valid only during the call.
    /// </summary>
    public interface ITableWriter
    {

        /// <summary>
        /// Inserts a row of a relation: egglog's <c>insert</c> of the row.
        /// </summary>
        /// <param name="relation">The relation, of the call's graph.</param>
        /// <param name="keys">One value per column.</param>
        /// <exception cref="ArgumentException">The relation, or a value, is not one the call may write.</exception>
        void Insert(RelationHandle relation, params ReadOnlySpan<object> keys);

        /// <summary>
        /// Sets a function's value at a key: egglog's <c>insert</c> of the row; two values for one key merge as the
        /// function merges them.
        /// </summary>
        /// <param name="function">The function, of the call's graph.</param>
        /// <param name="keys">One value per key column.</param>
        /// <param name="value">The value, of the output's type.</param>
        /// <exception cref="ArgumentException">The function, or a value, is not one the call may write.</exception>
        void Set(FunctionHandle function, ReadOnlySpan<object> keys, object value);

        /// <summary>
        /// Gets the class of a constructor term, making it when it is new: egglog's lookup of a constructor in an action,
        /// which mints a fresh class on a miss.
        /// </summary>
        /// <typeparam name="TSort">The constructor's sort.</typeparam>
        /// <param name="constructor">The constructor, of the call's graph.</param>
        /// <param name="keys">One value per child.</param>
        /// <returns>The term's class.</returns>
        /// <exception cref="ArgumentException">The constructor, or a value, is not one the call may write.</exception>
        TSort Make<TSort>(ConstructorHandle constructor, params ReadOnlySpan<object> keys) where TSort : struct, ISort<TSort>;

        /// <summary>
        /// Unions two classes: egglog's <c>union</c>.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="a">One class.</param>
        /// <param name="b">The other.</param>
        /// <exception cref="ArgumentException">A class is not usable in the scope the call runs in.</exception>
        void Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>;

        /// <summary>
        /// Deletes the row of a key: egglog's <c>remove</c>. Only where the scope may delete, and not in a host write,
        /// whose batch stages its deletions apart from its writes; <see cref="Scope.Delete(Fact[])"/> deletes there.
        /// </summary>
        /// <param name="table">The relation, function or constructor, of the call's graph.</param>
        /// <param name="keys">One value per key column.</param>
        /// <exception cref="ArgumentException">The table, or a value, is not one the call may write.</exception>
        /// <exception cref="InvalidOperationException">The scope may not delete, or the call is a host write's.</exception>
        void Remove(TableHandle table, params ReadOnlySpan<object> keys);

        /// <summary>
        /// Subsumes the row of a key: egglog's <c>subsume</c>. The table must be subsumable, and hold the row.
        /// </summary>
        /// <param name="table">The relation or constructor, of the call's graph, defined
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <param name="keys">One value per key column.</param>
        /// <exception cref="ArgumentException">The table, or a value, is not one the call may write, or the table is not
        /// subsumable.</exception>
        /// <exception cref="InvalidOperationException">The table holds no row for the key.</exception>
        void Subsume(TableHandle table, params ReadOnlySpan<object> keys);

    }

}
