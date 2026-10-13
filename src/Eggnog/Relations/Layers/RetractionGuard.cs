using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Refuses the retractions a context may not make: a context with live descendants retracts no row of a shared
    /// table, since its descendants read those rows; and a child deletes or clears a shared table's rows only under a
    /// frozen parent, since a writable parent could re-assert a row after the child's delete, which per-row hides
    /// cannot order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules, for a table that is not context-private (a context-private table is always allowed; the union-find is
    /// never private):
    /// </para>
    /// <list type="table">
    /// <listheader><term>Operation</term><description>Refused when</description></listheader>
    /// <item><term><see cref="CheckRemove"/></term><description>the context has live descendants; or it is a child
    /// whose parent is not frozen.</description></item>
    /// <item><term><see cref="CheckClear"/></term><description>as <see cref="CheckRemove"/>.</description></item>
    /// <item><term><see cref="CheckSubsume"/></term><description>the context has live descendants.</description></item>
    /// </list>
    /// <para>
    /// Every context owns one: the root's is <see cref="LayeredSchema.RootGuard"/>, a child's
    /// <see cref="ContextTables.Guard"/>. Execution states and the buffers of <see cref="Database.NewBuffer"/> keep their
    /// database's guard (<see cref="Database.GuardForNewState"/>) and consult it when a removal is staged, so a state or
    /// buffer created before a child exists still refuses afterwards. Rebuilds and refreshes stage through buffers from
    /// <see cref="Table.NewBuffer"/>, which carry none: they are not retractions. Each check is O(1): a gate read and a
    /// parent flag. The root's checks return after one volatile read of the root's gate while it has no live descendants
    /// (inlined), so a store's root pays that read per staged removal; a bare database has no guard.
    /// </para>
    /// </remarks>
    internal sealed class RetractionGuard
    {

        readonly LayeredSchema schema;
        readonly Context? owner;

        /// <summary>
        /// The root context's gate, for the root guard once <see cref="AttachRoot"/> ran; <c>null</c> for a child's guard
        /// (and for the root's before the root context exists).
        /// </summary>
        DescendantGate? rootGate;

        /// <summary>
        /// Initializes the guard of <paramref name="owner"/>, or of the store's root when it is <c>null</c> (the root
        /// guard is created with the schema, before the root context exists).
        /// </summary>
        /// <param name="schema">The store's schema, which says which tables are context-private.</param>
        /// <param name="owner">The guarded context, or <c>null</c> for the root.</param>
        internal RetractionGuard(LayeredSchema schema, Context? owner)
        {
            ArgumentNullException.ThrowIfNull(schema);
            this.schema = schema;
            this.owner = owner;
        }

        /// <summary>
        /// Gets the guarded context.
        /// </summary>
        Context Owner => owner ?? schema.Store.Root;

        /// <summary>
        /// Attaches the root guard to the root context, whose gate its checks read first. Called once, by the root
        /// context's constructor.
        /// </summary>
        /// <param name="root">The root context.</param>
        /// <exception cref="InvalidOperationException">This is a child's guard, or <paramref name="root"/> is not a
        /// root.</exception>
        internal void AttachRoot(Context root)
        {
            ArgumentNullException.ThrowIfNull(root);
            if (owner is not null || root.IsRoot == false)
                throw new InvalidOperationException("Only the root guard is attached, and only to the root context.");

            rootGate = root.Gate;
        }

        /// <summary>
        /// Throws unless the context may delete a row of <paramref name="table"/>: <c>stage_remove</c> from an execution
        /// state, a rule's action or a buffer of <see cref="Database.NewBuffer"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <exception cref="InvalidOperationException">The retraction is refused.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CheckRemove(TableId table)
        {
            if (rootGate is { HasLiveDescendants: false })
                return;

            Check(table, true, "A delete from");
        }

        /// <summary>
        /// Throws unless the context may clear <paramref name="table"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <exception cref="InvalidOperationException">The retraction is refused.</exception>
        public void CheckClear(TableId table)
        {
            if (rootGate is { HasLiveDescendants: false })
                return;

            Check(table, true, "Clearing");
        }

        /// <summary>
        /// Throws unless the context may subsume a row of <paramref name="table"/> (the bridge's subsume action).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <exception cref="InvalidOperationException">The retraction is refused.</exception>
        public void CheckSubsume(TableId table)
        {
            if (rootGate is { HasLiveDescendants: false })
                return;

            Check(table, false, "Subsuming a row of");
        }

        /// <summary>
        /// Applies the rules of the table above.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void Check(TableId table, bool needsFrozenParent, string operation)
        {
            if (schema[table] is { IsContextPrivate: true })
                return;

            var context = Owner;
            if (context.Gate.HasLiveDescendants)
                ThrowLiveDescendants(operation, table, context.Gate.LiveDescendants);

            if (needsFrozenParent && context.Parent is { IsFrozen: false })
                ThrowWritableParent(operation, table);

            // A child records that it retracts rows of the table: a row it hid may then have lost its carrier to the
            // retraction, which the invariant checker allows.
            if (context.Tables is { } tables && table != schema.UnionFind)
                tables.GetOrCreateLayer(table).SetRetracted();
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowLiveDescendants(string operation, TableId table, int live)
        {
            throw new InvalidOperationException($"{operation} table {table} is refused: the context has {live} live descendant(s) that read its rows. Dispose them first, or declare the table context-private.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowWritableParent(string operation, TableId table)
        {
            throw new InvalidOperationException($"{operation} table {table} is refused: a child context retracts a shared table's rows only under a frozen parent.");
        }

    }

}
