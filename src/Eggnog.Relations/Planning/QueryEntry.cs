namespace Eggnog.Relations
{

    /// <summary>
    /// One entry of an atom or an action: either a query <see cref="Variable"/> or a constant <see cref="Value"/>.
    /// egglog's <c>QueryEntry</c> (core-relations/src/action/mod.rs).
    /// </summary>
    /// <remarks>
    /// A variable entry holding <see cref="Variable.Placeholder"/> is a wildcard: the column is matched by anything and
    /// binds nothing (core-relations/src/query.rs:422).
    /// </remarks>
    internal readonly record struct QueryEntry
    {

        QueryEntry(bool isVariable, Variable variable, Value constant)
        {
            IsVariable = isVariable;
            Variable = variable;
            Constant = constant;
        }

        /// <summary>
        /// Gets whether the entry is a variable (or a wildcard); otherwise it is a constant.
        /// </summary>
        public bool IsVariable { get; }

        /// <summary>
        /// Gets the variable of a variable entry; meaningless for a constant.
        /// </summary>
        public Variable Variable { get; }

        /// <summary>
        /// Gets the constant of a constant entry; meaningless for a variable.
        /// </summary>
        public Value Constant { get; }

        /// <summary>
        /// Gets whether the entry is the wildcard <see cref="Variable.Placeholder"/>.
        /// </summary>
        public bool IsWildcard => IsVariable && Variable.IsPlaceholder;

        /// <summary>
        /// Creates a variable entry.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns>The entry.</returns>
        public static QueryEntry Var(Variable variable) => new(true, variable, default);

        /// <summary>
        /// Creates a constant entry.
        /// </summary>
        /// <param name="constant">The constant.</param>
        /// <returns>The entry.</returns>
        public static QueryEntry Const(Value constant) => new(false, default, constant);

        /// <summary>
        /// Creates a wildcard entry.
        /// </summary>
        /// <returns>The entry.</returns>
        public static QueryEntry Wildcard() => new(true, Variable.Placeholder, default);

        /// <summary>
        /// Converts a variable to a variable entry.
        /// </summary>
        /// <param name="variable">The variable.</param>
        public static implicit operator QueryEntry(Variable variable) => Var(variable);

        /// <summary>
        /// Converts a value to a constant entry.
        /// </summary>
        /// <param name="constant">The constant.</param>
        public static implicit operator QueryEntry(Value constant) => Const(constant);

        /// <inheritdoc />
        public override string ToString() => IsVariable ? (Variable.IsPlaceholder ? "_" : Variable.ToString()) : Constant.Rep.ToString();

    }

}
