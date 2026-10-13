using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Eggnog
{

    /// <summary>
    /// A table of a graph: a relation, a function or a constructor. Made by the graph's <c>Define*</c> methods.
    /// </summary>
    public abstract class TableHandle
    {

        ColumnType[] keys = Array.Empty<ColumnType>();
        ReadOnlyCollection<ColumnType> keysView = Array.AsReadOnly(Array.Empty<ColumnType>());

        /// <summary>
        /// Initializes a new instance; the graph sets the definition through the internal initializers.
        /// </summary>
        private protected TableHandle()
        {

        }

        /// <summary>
        /// Gets the graph the table belongs to.
        /// </summary>
        public EGraph Graph { get; internal set; } = null!;

        /// <summary>
        /// Gets the table's name, unique in its graph.
        /// </summary>
        public string Name { get; internal set; } = null!;

        /// <summary>
        /// Gets the table's position in declaration order, from 0; extraction breaks ties by it.
        /// </summary>
        public int Index { get; internal set; }

        /// <summary>
        /// Gets whether the table is a relation, a function or a constructor.
        /// </summary>
        public TableKind Kind { get; internal set; }

        /// <summary>
        /// Gets what the rows mean.
        /// </summary>
        public TableRole Role { get; internal set; }

        /// <summary>
        /// Gets the key columns' types.
        /// </summary>
        public IReadOnlyList<ColumnType> Keys => keysView;

        /// <summary>
        /// Gets the output type: the value type of a function, the sort of a constructor; <c>null</c> for a relation.
        /// </summary>
        public ColumnType? Output { get; internal set; }

        /// <summary>
        /// Gets whether lifts copy the table into a case's parent (R3).
        /// </summary>
        public bool Lift { get; internal set; }

        /// <summary>
        /// Gets whether every key, and the output, is a value type: such a table may be read scoped (D1).
        /// </summary>
        public bool IsValueOnly
        {
            get
            {
                foreach (var key in keys)
                    if (key.Kind == ColumnKind.Sort)
                        return false;

                return Output is null || Output.Kind == ColumnKind.Value;
            }
        }

        /// <summary>
        /// Gets the tag of a read-only scoped view made by <c>In(tag)</c>: an atom over the view reads the nearest
        /// ancestor-or-self of the running scope tagged so (D1, §6.8). <c>null</c> for the table itself. A scoped view is
        /// rejected in any write: a <see cref="RuleDefinitionException"/> in a rule head, an
        /// <see cref="ArgumentException"/> in a host write.
        /// </summary>
        public ScopeTag? ReadScope { get; private set; }

        /// <summary>
        /// Gets the table's name, or <c>dom@schema</c> for a scoped view.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => ReadScope is { } tag ? Name + "@" + tag.Name : Name;

        /// <summary>
        /// Gets the unscoped table: for a scoped view, the table it views; for a mirror handle, the table it mirrors;
        /// <c>null</c> for a table itself.
        /// </summary>
        internal TableHandle? Source { get; set; }

        /// <summary>
        /// Gets the tag of a mirror handle (§6.8): the handle over the engine function <c>T@tag</c>, which only copy rules
        /// write. <c>null</c> for every other handle.
        /// </summary>
        internal ScopeTag? MirrorTag { get; set; }

        /// <summary>
        /// Gets whether this is a mirror handle, which is never listed in <see cref="EGraph.Tables"/> and never reaches a
        /// user.
        /// </summary>
        internal bool IsMirror => MirrorTag is not null;

        /// <summary>
        /// Gets the table this handle names: itself, or the table a scoped view views.
        /// </summary>
        internal TableHandle Table => ReadScope is null ? this : Source!;

        /// <summary>
        /// Makes <paramref name="view"/> a read-only scoped view of this table under <paramref name="tag"/> (§3.3
        /// <c>In</c>): it shares the table's definition and engine function, with <see cref="ReadScope"/> set.
        /// </summary>
        /// <typeparam name="T">The handle type, the table's own.</typeparam>
        /// <param name="view">A new, uninitialized handle of the table's type.</param>
        /// <param name="tag">The tag.</param>
        /// <returns>The view.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="tag"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The tag belongs to another graph, or the table has a sort column.</exception>
        internal T ViewIn<T>(T view, ScopeTag tag) where T : TableHandle
        {
            ArgumentNullException.ThrowIfNull(tag);
            var table = Table;
            table.Graph.CheckTag(tag, nameof(tag));
            if (table.IsValueOnly == false)
                throw new ArgumentException($"'{table.Name}' has a sort column; scoped reads are limited to tables whose columns are all values (D1).", nameof(tag));

            view.Graph = table.Graph;
            view.Name = table.Name;
            view.Index = table.Index;
            view.Kind = table.Kind;
            view.Role = table.Role;
            view.KeyArray = table.KeyArray;
            view.Output = table.Output;
            view.Lift = table.Lift;
            view.Function = table.Function;
            view.Columns = table.Columns;
            view.Source = table;
            view.ReadScope = tag;
            if (view is FunctionHandle function)
                function.Lattice = ((FunctionHandle)table).Lattice;

            return view;
        }

        /// <summary>
        /// Gets the key columns' types, as an array.
        /// </summary>
        internal ColumnType[] KeyArray
        {
            get => keys;
            set
            {
                keys = value;
                keysView = Array.AsReadOnly(value);
            }
        }

        /// <summary>
        /// Gets the engine function the table is.
        /// </summary>
        internal Relations.FunctionId Function { get; set; }

        /// <summary>
        /// Gets whether the table is a relation.
        /// </summary>
        internal bool IsRelation => Kind == TableKind.Relation;

        /// <summary>
        /// Gets the engine row's column types: the keys then the output; a relation's row ends with the unit type.
        /// </summary>
        internal ColumnType[] Columns { get; set; } = Array.Empty<ColumnType>();

        /// <summary>
        /// Checks that an untyped application passes one term per key, each of the key's CLR type.
        /// </summary>
        /// <param name="arguments">The arguments.</param>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The count or a type does not match.</exception>
        internal void CheckArguments(Term[] arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            if (arguments.Length != keys.Length)
                throw new ArgumentException($"'{Name}' takes {keys.Length} arguments, not {arguments.Length}.", nameof(arguments));

            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = arguments[i] ?? throw new ArgumentNullException(nameof(arguments), $"Argument {i} of '{Name}' is null.");
                if (argument.ClrType != keys[i].ClrType)
                    throw new ArgumentException($"Argument {i} of '{Name}' is a {ValueStore.DisplayName(argument.ClrType)}, not a {keys[i].Name}.", nameof(arguments));
            }
        }

    }

}
