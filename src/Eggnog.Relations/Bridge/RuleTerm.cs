using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A term of the small rule language of egglog-bridge's macros (egglog-bridge/src/macros.rs): a variable by name, a
    /// constant entry, or a function applied to terms. <see cref="ExpressionBuilder.DefineRule"/> turns facts and commands
    /// over such terms into an e-graph rule, and <see cref="ExpressionBuilder.AddExpressions"/> adds ground terms.
    /// </summary>
    /// <remarks>
    /// The C# form of the macros' token trees: <c>Call(add, "x", Call(add, "y", "z")).Is("id")</c> is
    /// <c>(-&gt; (add x (add y z)) id)</c>. Strings convert to variables, <see cref="RuleEntry"/> and
    /// <see cref="Value"/> to constants. Immutable.
    /// </remarks>
    internal sealed class RuleTerm
    {

        static readonly RuleTerm[] NoArgs = Array.Empty<RuleTerm>();

        readonly RuleTerm[] args;

        RuleTerm(RuleTermKind kind, string? name, RuleEntry constant, FunctionId function, RuleTerm[] args)
        {
            Kind = kind;
            Name = name;
            Constant = constant;
            Function = function;
            this.args = args;
        }

        /// <summary>
        /// Gets which case this is.
        /// </summary>
        public RuleTermKind Kind { get; }

        /// <summary>
        /// Gets the name of a <see cref="RuleTermKind.Variable"/>.
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// Gets the entry of a <see cref="RuleTermKind.Constant"/>.
        /// </summary>
        public RuleEntry Constant { get; }

        /// <summary>
        /// Gets the function of a <see cref="RuleTermKind.Call"/>.
        /// </summary>
        public FunctionId Function { get; }

        /// <summary>
        /// Gets the arguments of a <see cref="RuleTermKind.Call"/>, its keys in order.
        /// </summary>
        public IReadOnlyList<RuleTerm> Args => args;

        /// <summary>
        /// Creates the variable named <paramref name="name"/>.
        /// </summary>
        /// <param name="name">The name; occurrences of one name in a rule are one variable.</param>
        /// <returns>The term.</returns>
        public static RuleTerm Var(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            return new(RuleTermKind.Variable, name, default, default, NoArgs);
        }

        /// <summary>
        /// Creates the constant <paramref name="entry"/>: the macros' <c>{ expr }</c>, which is used as given.
        /// </summary>
        /// <param name="entry">The entry.</param>
        /// <returns>The term.</returns>
        public static RuleTerm Const(RuleEntry entry) => new(RuleTermKind.Constant, null, entry, default, NoArgs);

        /// <summary>
        /// Creates the application of <paramref name="function"/> to <paramref name="args"/>, its keys.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="args">The key terms.</param>
        /// <returns>The term.</returns>
        public static RuleTerm Call(FunctionId function, params RuleTerm[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            foreach (var arg in args)
                ArgumentNullException.ThrowIfNull(arg, nameof(args));

            return new(RuleTermKind.Call, null, default, function, (RuleTerm[])args.Clone());
        }

        /// <summary>
        /// Converts a name to a variable term.
        /// </summary>
        /// <param name="name">The name.</param>
        public static implicit operator RuleTerm(string name) => Var(name);

        /// <summary>
        /// Converts an entry to a constant term.
        /// </summary>
        /// <param name="entry">The entry.</param>
        public static implicit operator RuleTerm(RuleEntry entry) => Const(entry);

        /// <summary>
        /// Converts a value to a constant term of the id type: the leaves of <see cref="ExpressionBuilder.AddExpressions"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        public static implicit operator RuleTerm(Value value) => Const(RuleEntry.Const(value, ColumnTy.Id));

        /// <summary>
        /// States that this call's output is <paramref name="output"/>: the macros' left-hand-side fact
        /// <c>(-&gt; (f args...) output)</c>.
        /// </summary>
        /// <param name="output">The output term: a variable, or another call.</param>
        /// <returns>The fact.</returns>
        /// <exception cref="ArgumentException">This term is not a call, or <paramref name="output"/> is a
        /// constant.</exception>
        public RuleFact Is(RuleTerm output) => new(this, output);

        /// <inheritdoc />
        public override string ToString() => Kind switch
        {
            RuleTermKind.Variable => Name!,
            RuleTermKind.Constant => "{" + Constant + "}",
            _ => args.Length == 0 ? "(" + Function + ")" : "(" + Function + " " + string.Join(" ", (IEnumerable<RuleTerm>)args) + ")",
        };

    }

}
