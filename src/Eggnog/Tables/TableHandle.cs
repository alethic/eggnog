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
        /// Gets the table's position in declaration order, from 0; egglog's extractor lists a sort's constructors in it.
        /// </summary>
        public int Index { get; internal set; }

        /// <summary>
        /// Gets whether the table is a relation, a function or a constructor.
        /// </summary>
        public TableKind Kind { get; internal set; }

        /// <summary>
        /// Gets the key columns' types.
        /// </summary>
        public IReadOnlyList<ColumnType> Keys => keysView;

        /// <summary>
        /// Gets the output type: the value type of a function, the sort of a constructor; <c>null</c> for a relation.
        /// </summary>
        public ColumnType? Output { get; internal set; }

        /// <summary>
        /// Gets whether rows of the table may be subsumed (<see cref="RelationOptions.Subsumable"/>,
        /// <see cref="ConstructorOptions.Subsumable"/>).
        /// </summary>
        public bool Subsumable { get; internal set; }

        /// <summary>
        /// Gets whether every key, and the output, is a value type that holds no classes.
        /// </summary>
        public bool IsValueOnly
        {
            get
            {
                foreach (var key in keys)
                    if (key.InvolvesClasses)
                        return false;

                return Output is null || Output.InvolvesClasses == false;
            }
        }

        /// <summary>
        /// Gets the table's name.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;

        /// <summary>
        /// Gets whether this is a scheduler's worklist (<see cref="ScheduledRule"/>): a context-private relation of the
        /// matches chosen for one step, which its action rule deletes as it fires them. Never listed in
        /// <see cref="EGraph.Tables"/>; its deletions are bookkeeping, not a rule's deletion.
        /// </summary>
        internal bool IsWorklist { get; set; }

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
        /// Converts the arguments of an any-arity application (<see cref="RelationN.Of"/> and its kin): a term as it is,
        /// any other value a constant of its key column's type.
        /// </summary>
        /// <param name="keys">The arguments, one per key column.</param>
        /// <returns>The terms, a new array.</returns>
        /// <exception cref="ArgumentNullException">A key is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The number or a type does not match.</exception>
        internal Term[] ToArguments(ReadOnlySpan<object> keys)
        {
            var types = new Type[this.keys.Length];
            for (var i = 0; i < types.Length; i++)
                types[i] = this.keys[i].ClrType;

            return BoxedConstantTerm.Convert(keys, types, Name);
        }

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
