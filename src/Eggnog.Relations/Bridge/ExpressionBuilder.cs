using System;
using System.Collections.Generic;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Builds e-graph rules and terms from the small rule language of egglog-bridge's macros: the port of its
    /// <c>ExpressionBuilder</c> and of the <c>define_rule!</c> and <c>add_expressions!</c> macros
    /// (egglog-bridge/src/macros.rs), as builder helpers over <see cref="RuleTerm"/>, <see cref="RuleFact"/> and
    /// <see cref="RuleCommand"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DefineRule"/> lowers a semi-naive rule exactly as the macro does, so variables and atoms are created in
    /// the same order (which fixes the order of the semi-naive variants): for each fact <c>(-&gt; (f args...) out)</c>,
    /// the output first (a named variable of <c>f</c>'s output type, or a nested call), then the arguments left to right
    /// (a variable takes the type of its column the first time it is seen; a nested call becomes an atom of its own,
    /// added before the enclosing one, whose output is a fresh variable named after the call), then the atom itself, as
    /// a match of rows that are not subsumed. On the right-hand side a call is a <see cref="EGraphRuleBuilder.Lookup"/>
    /// (minting the term, for a constructor), arguments are evaluated left to right, and a variable must have been bound
    /// before.
    /// </para>
    /// <para>
    /// egglog marks this type as a helper for its macros and recommends <see cref="EGraphRuleBuilder"/> for building
    /// rules directly. An instance holds the name-to-variable map of one rule; not thread-safe. Cold path.
    /// </para>
    /// </remarks>
    public sealed class ExpressionBuilder
    {

        readonly Dictionary<string, RuleEntry> vars = new(StringComparer.Ordinal);

        /// <summary>
        /// Defines a semi-naive rule from left-hand-side facts and right-hand-side commands and registers it. The port of
        /// egglog's <c>define_rule!</c> (egglog-bridge/src/macros.rs:144-153); the description is the rule's text.
        /// </summary>
        /// <param name="egraph">The e-graph.</param>
        /// <param name="lhs">The facts the rule matches.</param>
        /// <param name="rhs">The commands it runs on each match.</param>
        /// <returns>The rule's id.</returns>
        /// <exception cref="ArgumentException">A term does not fit its function.</exception>
        /// <exception cref="InvalidOperationException">The right-hand side uses a variable the left-hand side does not
        /// bind.</exception>
        public static EGraphRuleId DefineRule(EGraph egraph, IReadOnlyList<RuleFact> lhs, IReadOnlyList<RuleCommand> rhs)
        {
            ArgumentNullException.ThrowIfNull(egraph);
            ArgumentNullException.ThrowIfNull(lhs);
            ArgumentNullException.ThrowIfNull(rhs);

            var expressions = new ExpressionBuilder();
            var builder = egraph.NewRule(Describe(egraph, lhs, rhs), true);
            expressions.ParseLhs(builder, lhs);
            expressions.ParseRhs(builder, rhs);
            return builder.Build();
        }

        /// <summary>
        /// Adds every ground term of <paramref name="expressions"/> to <paramref name="egraph"/>, innermost calls first.
        /// The port of egglog's <c>add_expressions!</c> (egglog-bridge/src/macros.rs:155-172).
        /// </summary>
        /// <param name="egraph">The e-graph.</param>
        /// <param name="expressions">The terms; their leaves are constants.</param>
        public static void AddExpressions(EGraph egraph, params RuleTerm[] expressions)
        {
            ArgumentNullException.ThrowIfNull(egraph);
            ArgumentNullException.ThrowIfNull(expressions);
            foreach (var expression in expressions)
                AddExpression(egraph, expression);
        }

        /// <summary>
        /// Adds the ground term <paramref name="expression"/>: its arguments left to right, then the call itself with
        /// <see cref="EGraph.AddTerm"/>. A constant leaf is its own value. egglog's <c>add_expression_impl!</c>.
        /// </summary>
        /// <param name="egraph">The e-graph.</param>
        /// <param name="expression">The term.</param>
        /// <returns>The term's canonical id, or the constant's value.</returns>
        /// <exception cref="ArgumentException">The term contains a variable.</exception>
        public static Value AddExpression(EGraph egraph, RuleTerm expression)
        {
            ArgumentNullException.ThrowIfNull(egraph);
            ArgumentNullException.ThrowIfNull(expression);
            switch (expression.Kind)
            {
                case RuleTermKind.Constant:
                    return expression.Constant.Constant;
                case RuleTermKind.Variable:
                    throw new ArgumentException($"add_expressions takes ground terms, not the variable {expression.Name}.", nameof(expression));
                default:
                {
                    var inner = new Value[expression.Args.Count];
                    for (var i = 0; i < inner.Length; i++)
                        inner[i] = AddExpression(egraph, expression.Args[i]);

                    return egraph.AddTerm(expression.Function, inner);
                }
            }
        }

        /// <summary>
        /// Binds <paramref name="name"/> to the variable <paramref name="variable"/>, unless the name is already bound.
        /// egglog's <c>bind_var</c>.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="variable">The variable.</param>
        public void BindVar(string name, VariableId variable)
        {
            ArgumentNullException.ThrowIfNull(name);
            vars.TryAdd(name, RuleEntry.Var(new RuleVariable(variable, name)));
        }

        /// <summary>
        /// Gets the variable bound to <paramref name="name"/>, creating a named variable of type <paramref name="type"/>
        /// the first time. egglog's <c>bind_or_lookup_var</c>.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="type">The type of a new variable.</param>
        /// <param name="builder">The rule's builder.</param>
        /// <returns>The variable's entry.</returns>
        public RuleEntry BindOrLookupVar(string name, ColumnTy type, EGraphRuleBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(builder);
            if (vars.TryGetValue(name, out var existing))
                return existing;

            var created = RuleEntry.Var(builder.NewVarNamed(type, name));
            vars.Add(name, created);
            return created;
        }

        /// <summary>
        /// Gets the variable bound to <paramref name="name"/>, if any. egglog's <c>lookup_var</c>.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="entry">The variable's entry.</param>
        /// <returns><c>true</c> if the name is bound.</returns>
        public bool TryLookupVar(string name, out RuleEntry entry)
        {
            ArgumentNullException.ThrowIfNull(name);
            return vars.TryGetValue(name, out entry);
        }

        /// <summary>
        /// Gets the variable bound to <paramref name="name"/>, creating one the first time with the type of column
        /// <paramref name="column"/> of <paramref name="function"/>. egglog's <c>get_var</c>.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="function">The function whose column the variable appears in.</param>
        /// <param name="column">The column.</param>
        /// <param name="builder">The rule's builder.</param>
        /// <returns>The variable's entry.</returns>
        public RuleEntry GetVar(string name, FunctionId function, int column, EGraphRuleBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(builder);
            if (vars.TryGetValue(name, out var existing))
                return existing;

            var created = RuleEntry.Var(builder.NewVarNamed(InferType(function, column, builder), name));
            vars.Add(name, created);
            return created;
        }

        /// <summary>
        /// Gets the output type of <paramref name="function"/>. egglog's <c>infer_return_type</c>.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="builder">The rule's builder.</param>
        /// <returns>The type.</returns>
        public ColumnTy InferReturnType(FunctionId function, EGraphRuleBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.EGraph.GetFunctionInfo(function).OutputType;
        }

        /// <summary>
        /// Gets the type of column <paramref name="column"/> of <paramref name="function"/>. egglog's <c>infer_type</c>.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="column">The column.</param>
        /// <param name="builder">The rule's builder.</param>
        /// <returns>The type.</returns>
        /// <exception cref="ArgumentException">The function has no such column.</exception>
        public ColumnTy InferType(FunctionId function, int column, EGraphRuleBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var info = builder.EGraph.GetFunctionInfo(function);
            if ((uint)column >= (uint)info.Schema.Length)
                throw new ArgumentException($"{info.Name} has {info.Schema.Length} columns; a term gives it more.", nameof(column));

            return info.Schema[column];
        }

        /// <summary>
        /// Records the left-hand side: egglog's <c>parse_lhs!</c> (egglog-bridge/src/macros.rs:131-142).
        /// </summary>
        /// <param name="builder">The rule's builder.</param>
        /// <param name="facts">The facts.</param>
        public void ParseLhs(EGraphRuleBuilder builder, IReadOnlyList<RuleFact> facts)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(facts);
            foreach (var fact in facts)
            {
                // First the output, then the call with it as its last column.
                var type = InferReturnType(fact.Call.Function, builder);
                var output = fact.Output.Kind == RuleTermKind.Call ? ParseLhsAtom(builder, fact.Output) : BindOrLookupVar(fact.Output.Name!, type, builder);
                var row = new List<RuleEntry>();
                ParseLhsAtomArgs(builder, fact.Call.Function, row, fact.Call.Args);
                row.Add(output);
                builder.QueryTable(fact.Call.Function, row.ToArray(), false);
            }
        }

        /// <summary>
        /// Records the right-hand side: egglog's <c>parse_rhs_command!</c> (egglog-bridge/src/macros.rs:50-72).
        /// </summary>
        /// <param name="builder">The rule's builder.</param>
        /// <param name="commands">The commands, in order.</param>
        /// <exception cref="InvalidOperationException">A command uses an unbound variable.</exception>
        public void ParseRhs(EGraphRuleBuilder builder, IReadOnlyList<RuleCommand> commands)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(commands);
            foreach (var command in commands)
            {
                switch (command.Kind)
                {
                    case RuleCommandKind.Let:
                    {
                        var result = ParseRhsAtom(builder, command.Left);
                        if (result.IsVariable)
                            BindVar(command.Name!, result.Variable.Id);

                        break;
                    }
                    case RuleCommandKind.Set:
                    {
                        var row = new List<RuleEntry>();
                        ParseRhsAtomArgs(builder, row, command.Left.Args);
                        row.Add(ParseRhsArg(builder, command.Right));
                        builder.Set(command.Left.Function, row.ToArray());
                        break;
                    }
                    default:
                    {
                        var left = ParseRhsAtom(builder, command.Left);
                        var right = ParseRhsAtom(builder, command.Right);
                        builder.Union(left, right);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Formats a rule as its description: the macros' <c>stringify!</c> of the rule, with function names.
        /// </summary>
        /// <param name="egraph">The e-graph, for function names.</param>
        /// <param name="lhs">The facts.</param>
        /// <param name="rhs">The commands.</param>
        /// <returns>The text.</returns>
        public static string Describe(EGraph egraph, IReadOnlyList<RuleFact> lhs, IReadOnlyList<RuleCommand> rhs)
        {
            ArgumentNullException.ThrowIfNull(egraph);
            ArgumentNullException.ThrowIfNull(lhs);
            ArgumentNullException.ThrowIfNull(rhs);

            var b = new StringBuilder("(");
            for (var i = 0; i < lhs.Count; i++)
            {
                if (i > 0)
                    b.Append(' ');

                b.Append("(-> ");
                Format(egraph, lhs[i].Call, b);
                b.Append(' ');
                Format(egraph, lhs[i].Output, b);
                b.Append(')');
            }

            b.Append(" => ");
            for (var i = 0; i < rhs.Count; i++)
            {
                if (i > 0)
                    b.Append(' ');

                var command = rhs[i];
                switch (command.Kind)
                {
                    case RuleCommandKind.Let:
                        b.Append("(let ").Append(command.Name).Append(' ');
                        Format(egraph, command.Left, b);
                        break;
                    case RuleCommandKind.Set:
                        b.Append("(set ");
                        Format(egraph, command.Left, b);
                        b.Append(' ');
                        Format(egraph, command.Right, b);
                        break;
                    default:
                        b.Append("(union ");
                        Format(egraph, command.Left, b);
                        b.Append(' ');
                        Format(egraph, command.Right, b);
                        break;
                }

                b.Append(')');
            }

            return b.Append(')').ToString();
        }

        /// <summary>
        /// Formats a term with function names.
        /// </summary>
        /// <param name="egraph">The e-graph, for function names.</param>
        /// <param name="term">The term.</param>
        /// <returns>The text.</returns>
        public static string Format(EGraph egraph, RuleTerm term)
        {
            ArgumentNullException.ThrowIfNull(egraph);
            ArgumentNullException.ThrowIfNull(term);
            var b = new StringBuilder();
            Format(egraph, term, b);
            return b.ToString();
        }

        /// <summary>
        /// Appends a term with function names.
        /// </summary>
        static void Format(EGraph egraph, RuleTerm term, StringBuilder b)
        {
            switch (term.Kind)
            {
                case RuleTermKind.Variable:
                    b.Append(term.Name);
                    break;
                case RuleTermKind.Constant:
                    b.Append('{').Append(term.Constant).Append('}');
                    break;
                default:
                    b.Append('(').Append(egraph.GetFunctionName(term.Function));
                    foreach (var arg in term.Args)
                    {
                        b.Append(' ');
                        Format(egraph, arg, b);
                    }

                    b.Append(')');
                    break;
            }
        }

        /// <summary>
        /// Records a left-hand-side call as an atom whose output is a fresh variable named after the call, and returns
        /// that variable: egglog's <c>parse_lhs_atom!</c> (egglog-bridge/src/macros.rs:95-115).
        /// </summary>
        RuleEntry ParseLhsAtom(EGraphRuleBuilder builder, RuleTerm call)
        {
            var row = new List<RuleEntry>();
            ParseLhsAtomArgs(builder, call.Function, row, call.Args);
            var type = InferType(call.Function, row.Count, builder);
            var output = RuleEntry.Var(builder.NewVarNamed(type, Format(builder.EGraph, call)));
            row.Add(output);
            builder.QueryTable(call.Function, row.ToArray(), false);
            return output;
        }

        /// <summary>
        /// Records the arguments of a left-hand-side call, left to right: egglog's <c>parse_lhs_atom_args!</c>
        /// (egglog-bridge/src/macros.rs:76-94).
        /// </summary>
        void ParseLhsAtomArgs(EGraphRuleBuilder builder, FunctionId function, List<RuleEntry> row, IReadOnlyList<RuleTerm> args)
        {
            foreach (var arg in args)
            {
                switch (arg.Kind)
                {
                    case RuleTermKind.Constant:
                        row.Add(arg.Constant);
                        break;
                    case RuleTermKind.Variable:
                        row.Add(GetVar(arg.Name!, function, row.Count, builder));
                        break;
                    default:
                        row.Add(ParseLhsAtom(builder, arg));
                        break;
                }
            }
        }

        /// <summary>
        /// Evaluates a right-hand-side term: a bound variable, or a call looked up (and minted, for a constructor).
        /// egglog's <c>parse_rhs_atom!</c> (egglog-bridge/src/macros.rs:32-48); a constant is also accepted.
        /// </summary>
        RuleEntry ParseRhsAtom(EGraphRuleBuilder builder, RuleTerm term)
        {
            switch (term.Kind)
            {
                case RuleTermKind.Constant:
                    return term.Constant;
                case RuleTermKind.Variable:
                    return LookupBound(term.Name!);
                default:
                {
                    var row = new List<RuleEntry>();
                    ParseRhsAtomArgs(builder, row, term.Args);
                    var text = Format(builder.EGraph, term);
                    return builder.Lookup(term.Function, row.ToArray(), () => text);
                }
            }
        }

        /// <summary>
        /// Evaluates the arguments of a right-hand-side call, left to right: egglog's <c>parse_rhs_atom_args!</c>
        /// (egglog-bridge/src/macros.rs:8-30).
        /// </summary>
        void ParseRhsAtomArgs(EGraphRuleBuilder builder, List<RuleEntry> row, IReadOnlyList<RuleTerm> args)
        {
            foreach (var arg in args)
                row.Add(ParseRhsArg(builder, arg));
        }

        /// <summary>
        /// Evaluates one right-hand-side argument.
        /// </summary>
        RuleEntry ParseRhsArg(EGraphRuleBuilder builder, RuleTerm arg) => arg.Kind switch
        {
            RuleTermKind.Constant => arg.Constant,
            RuleTermKind.Variable => LookupBound(arg.Name!),
            _ => ParseRhsAtom(builder, arg),
        };

        /// <summary>
        /// Gets the variable bound to <paramref name="name"/>, which the right-hand side requires.
        /// </summary>
        RuleEntry LookupBound(string name)
        {
            if (vars.TryGetValue(name, out var entry))
                return entry;

            throw new InvalidOperationException($"use of unbound variable {name} on the right-hand side of a rule");
        }

    }

}
