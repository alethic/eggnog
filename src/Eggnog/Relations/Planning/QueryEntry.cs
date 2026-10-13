using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One entry of an atom or an action: either a query <see cref="Variable"/> or a constant <see cref="Value"/>.
    /// egglog's <c>QueryEntry</c> (core-relations/src/action/mod.rs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A variable entry holding <see cref="Variable.Placeholder"/> is a wildcard: the column is matched by anything and
    /// binds nothing (core-relations/src/query.rs:422).
    /// </para>
    /// <para>
    /// A constant may be flagged as an e-class id (<see cref="IdConst"/>, <see cref="IsIdConstant"/>): a store-backed
    /// context then runs it in its canonical form, so an id that has lost a union still matches its class, even where no
    /// table column says it is an id (comparisons, external-function arguments). The flag does not take part in
    /// equality: a flagged and an unflagged entry of one value are the same operand.
    /// </para>
    /// </remarks>
    internal readonly record struct QueryEntry
    {

        QueryEntry(bool isVariable, Variable variable, Value constant, bool isIdConstant = false)
        {
            IsVariable = isVariable;
            Variable = variable;
            Constant = constant;
            IsIdConstant = isIdConstant;
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
        /// Gets whether the entry is a constant flagged as an e-class id (<see cref="IdConst"/>): always <c>false</c> for
        /// a variable.
        /// </summary>
        public bool IsIdConstant { get; }

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
        /// Creates a constant entry flagged as an e-class id: a store-backed context runs it in its canonical form,
        /// wherever it appears in a rule's actions (comparisons, external-function arguments, and the columns of a table
        /// that are not id columns). The bridge lowers every id-typed constant through it.
        /// </summary>
        /// <param name="constant">The id.</param>
        /// <returns>The entry.</returns>
        public static QueryEntry IdConst(Value constant) => new(false, default, constant, true);

        /// <summary>
        /// Gets a copy of a constant entry holding <paramref name="constant"/> instead, keeping
        /// <see cref="IsIdConstant"/>: how an action constant is replaced by its canonical form
        /// (<see cref="ActionSubstituter"/>).
        /// </summary>
        /// <param name="constant">The new constant.</param>
        /// <returns>The entry.</returns>
        /// <exception cref="InvalidOperationException">The entry is a variable.</exception>
        public QueryEntry WithConstant(Value constant) => IsVariable ? throw new InvalidOperationException("Only a constant entry holds a constant.") : new(false, default, constant, IsIdConstant);

        /// <summary>
        /// Gets whether two entries are the same operand: both variables of one index, or both constants of one value;
        /// <see cref="IsIdConstant"/> is ignored.
        /// </summary>
        /// <param name="other">The other entry.</param>
        /// <returns><c>true</c> if equal.</returns>
        public bool Equals(QueryEntry other) => IsVariable == other.IsVariable && (IsVariable ? Variable == other.Variable : Constant == other.Constant);

        /// <inheritdoc />
        public override int GetHashCode() => IsVariable ? Variable.GetHashCode() : ~Constant.GetHashCode();

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
