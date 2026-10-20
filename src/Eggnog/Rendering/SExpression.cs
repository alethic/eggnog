using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Eggnog
{

    /// <summary>
    /// Renders terms, facts, rules, split rules and schedules as egglog s-expressions, for <c>ToString</c> and messages.
    /// Constants are formatted by their column type where the context gives one (a table's argument), and with the
    /// built-in literal syntax otherwise; named primitives render by name, lambdas as <c>&lt;lambda&gt;</c>.
    /// </summary>
    internal static class SExpression
    {

        /// <summary>
        /// Renders a term: <c>(StrEq "kind" p)</c>.
        /// </summary>
        /// <param name="term">The term.</param>
        /// <returns>The rendering.</returns>
        public static string Render(Term term)
        {
            var builder = new StringBuilder();
            Append(builder, term, null);
            return builder.ToString();
        }

        /// <summary>
        /// Renders a fact as a body fact: <c>(present p)</c>, <c>(= d (dom p))</c>, <c>(!= a b)</c>.
        /// </summary>
        /// <param name="fact">The fact.</param>
        /// <returns>The rendering.</returns>
        public static string Render(Fact fact)
        {
            var builder = new StringBuilder();
            Append(builder, fact);
            return builder.ToString();
        }

        /// <summary>
        /// Renders a rule as written: <c>(rule (body…) (head…) :ruleset name)</c>, with <c>:naive</c> for a naive rule,
        /// <c>:erasing tag</c> for an erasing rule's licence, or <c>:reads-own-facts "reason"</c> for an exempt rule; a
        /// scoped application renders as <c>(present@schema p)</c>.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <returns>The rendering.</returns>
        public static string Render(Rule rule)
        {
            var draft = rule.Draft;
            var builder = new StringBuilder("(rule (");
            for (var i = 0; i < draft.Body.Count; i++)
            {
                if (i > 0)
                    builder.Append(' ');

                Append(builder, draft.Body[i]);
            }

            builder.Append(") (");
            for (var i = 0; i < draft.Head.Count; i++)
            {
                if (i > 0)
                    builder.Append(' ');

                var item = draft.Head[i];
                switch (item.Kind)
                {
                    case HeadItemKind.Then:
                        AppendAction(builder, item.Fact!);
                        break;
                    case HeadItemKind.Make:
                        builder.Append("(let ").Append(item.Variable!.Shape.Name).Append(' ');
                        Append(builder, item.Left!, null);
                        builder.Append(')');
                        break;
                    case HeadItemKind.Subsume:
                        builder.Append("(subsume ");
                        if (item.Fact is { } row)
                            Append(builder, row);
                        else
                            Append(builder, item.Left!, null);

                        builder.Append(')');
                        break;
                    default:
                        builder.Append("(union ");
                        Append(builder, item.Variable!, null);
                        builder.Append(' ');
                        Append(builder, item.Right!, null);
                        builder.Append(')');
                        break;
                }
            }

            builder.Append(") :ruleset ").Append(rule.Ruleset.Name);
            if (rule.IsNaive)
                builder.Append(" :naive");

            if (rule.Licence is { } licence)
                builder.Append(" :erasing ").Append(licence.Name);
            else if (rule.OwnFactsReason is { } reason)
                builder.Append(" :reads-own-facts ").Append(Literal(reason, null));

            return builder.Append(')').ToString();
        }

        /// <summary>
        /// Renders a query as written: <c>(query ((present p) (= d (dom p))) (p d))</c>, its body then its variables.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>The rendering.</returns>
        public static string Render(Query query)
        {
            var draft = query.Draft;
            var builder = new StringBuilder("(query (");
            for (var i = 0; i < draft.Body.Count; i++)
            {
                if (i > 0)
                    builder.Append(' ');

                Append(builder, draft.Body[i]);
            }

            builder.Append(") (");
            for (var i = 0; i < draft.Variables.Count; i++)
            {
                if (i > 0)
                    builder.Append(' ');

                builder.Append(draft.Variables[i].Name);
            }

            return builder.Append("))").ToString();
        }

        /// <summary>
        /// Renders a split rule as written: <c>(split (body…) (cases v d) (key k) (assume…))</c>, then
        /// <c>:case-tag tag</c> when its cases are tagged; the assumptions render as actions.
        /// </summary>
        /// <param name="rule">The split rule.</param>
        /// <returns>The rendering.</returns>
        public static string Render(SplitRule rule)
        {
            var builder = rule.Builder;
            var draft = builder.Draft;
            var text = new StringBuilder("(split (");
            for (var i = 0; i < draft.Body.Count; i++)
            {
                if (i > 0)
                    text.Append(' ');

                Append(text, draft.Body[i]);
            }

            text.Append(") (cases ");
            Append(text, builder.Element!, null);
            text.Append(' ');
            Append(text, builder.Set!, null);
            text.Append(") (key");
            if (builder.KeyTerms is { } key)
            {
                foreach (var term in key)
                {
                    text.Append(' ');
                    Append(text, term, null);
                }
            }
            else
            {
                foreach (var (name, _, variable) in draft.Variables)
                    if (ReferenceEquals(variable, builder.Set) == false)
                        text.Append(' ').Append(name);
            }

            text.Append(") (");
            for (var i = 0; i < builder.Assumptions.Count; i++)
            {
                if (i > 0)
                    text.Append(' ');

                AppendAction(text, builder.Assumptions[i]);
            }

            text.Append(')');
            if (rule.CaseTag is { } tag)
                text.Append(" :case-tag ").Append(tag.Name);

            return text.Append(')').ToString();
        }

        /// <summary>
        /// Renders a table row: <c>(present "kind")</c>, <c>(dom "kind") = (set-of "a" "b")</c>,
        /// <c>(StrEq "kind" "a") = #17</c>.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <returns>The rendering.</returns>
        public static string Render(TableRow row)
        {
            var table = row.Table;
            var builder = new StringBuilder("(").Append(table.Name);
            for (var i = 0; i < row.Keys.Count; i++)
                builder.Append(' ').Append(Cell(row.Keys[i], table.KeyArray[i]));

            builder.Append(')');
            if (row.Output is { } output)
                builder.Append(" = ").Append(Cell(output, table.Output!));

            return builder.ToString();
        }

        /// <summary>
        /// Renders a schedule: <c>(saturate (run horn) (run rewrite))</c>.
        /// </summary>
        /// <param name="schedule">The schedule.</param>
        /// <returns>The rendering.</returns>
        public static string Render(Schedule schedule)
        {
            var builder = new StringBuilder();
            Append(builder, schedule);
            return builder.ToString();
        }

        /// <summary>
        /// Appends a term; <paramref name="type"/> formats a constant in its context.
        /// </summary>
        static void Append(StringBuilder builder, Term term, ColumnType? type, ScopeTag? enclosing = null)
        {
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Variable:
                    builder.Append(shape.Name);
                    break;
                case TermShapeKind.Constant:
                    builder.Append(Literal(shape.Constant!, shape.ConstantType ?? type));
                    break;
                case TermShapeKind.Application:
                {
                    var table = shape.Table!;
                    builder.Append('(');
                    AppendTable(builder, table, enclosing);
                    for (var i = 0; i < shape.Arguments.Length; i++)
                    {
                        builder.Append(' ');
                        Append(builder, shape.Arguments[i], i < table.KeyArray.Length ? table.KeyArray[i] : null, enclosing);
                    }

                    builder.Append(')');
                    break;
                }
                default:
                {
                    builder.Append('(').Append(shape.Primitive!.Name);
                    foreach (var argument in shape.Arguments)
                    {
                        builder.Append(' ');
                        Append(builder, argument, null, enclosing);
                    }

                    builder.Append(')');
                    break;
                }
            }
        }

        /// <summary>
        /// Appends a body fact.
        /// </summary>
        static void Append(StringBuilder builder, Fact fact, ScopeTag? enclosing = null)
        {
            switch (fact)
            {
                case RelationFact relation:
                    builder.Append('(');
                    AppendTable(builder, relation.Table, enclosing);
                    for (var i = 0; i < relation.Arguments.Length; i++)
                    {
                        builder.Append(' ');
                        Append(builder, relation.Arguments[i], i < relation.Table.KeyArray.Length ? relation.Table.KeyArray[i] : null, enclosing);
                    }

                    builder.Append(')');
                    break;
                case EqualityFact equality:
                    AppendPair(builder, "=", equality.Left, equality.Right, enclosing);
                    break;
                case NotEqualFact notEqual:
                    AppendPair(builder, "!=", notEqual.Left, notEqual.Right, enclosing);
                    break;
                case ExistsFact exists:
                    Append(builder, exists.Application, null, enclosing);
                    break;
                case FilterFact filter:
                    Append(builder, filter.Condition, null, enclosing);
                    break;
                case ScopedFact scoped:
                    // Fact.In renders as its facts, each application read in the tagged scope: (present@schema p).
                    for (var i = 0; i < scoped.Facts.Length; i++)
                    {
                        if (i > 0)
                            builder.Append(' ');

                        Append(builder, scoped.Facts[i], scoped.Scope);
                    }

                    break;
                default:
                    builder.Append(fact.GetType().Name);
                    break;
            }
        }

        /// <summary>
        /// Appends a head fact as an egglog action: a row, <c>(set (f k) v)</c>, or <c>(union a b)</c>.
        /// </summary>
        static void AppendAction(StringBuilder builder, Fact fact)
        {
            if (fact is EqualityFact equality)
            {
                var left = equality.Left.Shape;
                var right = equality.Right.Shape;
                if (left.Kind == TermShapeKind.Application && left.Table!.Kind == TableKind.Function)
                    AppendPair(builder, "set", equality.Left, equality.Right);
                else if (right.Kind == TermShapeKind.Application && right.Table!.Kind == TableKind.Function)
                    AppendPair(builder, "set", equality.Right, equality.Left);
                else
                    AppendPair(builder, "union", equality.Left, equality.Right);

                return;
            }

            Append(builder, fact);
        }

        /// <summary>
        /// Appends <c>(op a b)</c>, formatting a constant on one side by the other side's table output.
        /// </summary>
        static void AppendPair(StringBuilder builder, string op, Term left, Term right, ScopeTag? enclosing = null)
        {
            builder.Append('(').Append(op).Append(' ');
            Append(builder, left, OutputOf(right), enclosing);
            builder.Append(' ');
            Append(builder, right, OutputOf(left), enclosing);
            builder.Append(')');
        }

        /// <summary>
        /// Appends a table's name, with <c>@tag</c> for a scoped read: its handle's tag, or an enclosing
        /// <see cref="Fact.In"/>'s.
        /// </summary>
        static void AppendTable(StringBuilder builder, TableHandle table, ScopeTag? enclosing)
        {
            builder.Append(table.Name);
            if ((table.ReadScope ?? enclosing) is { } tag)
                builder.Append('@').Append(tag.Name);
        }

        /// <summary>
        /// Gets the output type of an application, which formats the constant it is equated with.
        /// </summary>
        static ColumnType? OutputOf(Term term)
        {
            var shape = term.Shape;
            return shape.Kind == TermShapeKind.Application ? shape.Table!.Output : null;
        }

        /// <summary>
        /// Appends a schedule.
        /// </summary>
        static void Append(StringBuilder builder, Schedule schedule)
        {
            switch (schedule)
            {
                case RunSchedule run:
                    builder.Append("(run");
                    foreach (var ruleset in run.Rulesets)
                        builder.Append(' ').Append(ruleset.Name);

                    builder.Append(')');
                    break;
                case RepeatSchedule repeat:
                    builder.Append("(repeat ").Append(repeat.Times.ToString(CultureInfo.InvariantCulture));
                    AppendSteps(builder, repeat.Steps);
                    break;
                case SaturateSchedule saturate:
                    builder.Append("(saturate");
                    AppendSteps(builder, saturate.Steps);
                    break;
                case SequenceSchedule sequence:
                    builder.Append("(seq");
                    AppendSteps(builder, sequence.Steps);
                    break;
                case UntilSchedule until:
                    builder.Append("(until ");
                    Append(builder, until.Body);
                    foreach (var fact in until.Condition)
                    {
                        builder.Append(' ');
                        Append(builder, fact);
                    }

                    builder.Append(')');
                    break;
                case SplitSchedule split:
                    builder.Append("(split (");
                    for (var i = 0; i < split.Rules.Length; i++)
                    {
                        if (i > 0)
                            builder.Append(' ');

                        builder.Append(split.Rules[i].Name);
                    }

                    builder.Append(") ");
                    Append(builder, split.InCase);
                    builder.Append(')');
                    break;
                default:
                    builder.Append(schedule.GetType().Name);
                    break;
            }
        }

        /// <summary>
        /// Appends a composite schedule's steps and its closing parenthesis.
        /// </summary>
        static void AppendSteps(StringBuilder builder, Schedule[] steps)
        {
            foreach (var step in steps)
            {
                builder.Append(' ');
                Append(builder, step);
            }

            builder.Append(')');
        }

        /// <summary>
        /// Formats a decoded cell of a table row: a class as <c>#17</c>, a value by its column type.
        /// </summary>
        static string Cell(object value, ColumnType type) => value is ClassId id ? id.ToString() : Literal(value, type);

        /// <summary>
        /// Formats a constant: by its column type when known, else with the built-in literal syntax.
        /// </summary>
        static string Literal(object value, ColumnType? type)
        {
            if (type is not null && type.ClrType.IsInstanceOfType(value))
            {
                try
                {
                    return type.FormatObject(value);
                }
                catch (Exception)
                {
                    // Fall back to the built-in syntax.
                }
            }

            return DefaultLiteral(value);
        }

        /// <summary>
        /// Formats a value with the built-in literal syntax, culture-invariantly. A sort value renders as its class,
        /// found by reflection over its <see cref="ISort{TSort}"/> implementation.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2075",
            Justification = "Renders a sort value whose column type is unknown. When the trimmer has removed the value's ISort<> "
                + "implementation or its Id property, the value renders by its ToString().")]
        static string DefaultLiteral(object value)
        {
            switch (value)
            {
                case string text:
                    return Relations.LiteralText.String(text);
                case bool flag:
                    return flag ? "true" : "false";
                case double number:
                    return Relations.LiteralText.Float(number);
                case ClassId id:
                    return id.ToString();
                case Enum member:
                    return member.ToString();
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
            }

            var type = value.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueSet<>))
            {
                var builder = new StringBuilder("(set-of");
                foreach (var element in (IEnumerable)value)
                    builder.Append(' ').Append(DefaultLiteral(element!));

                return builder.Append(')').ToString();
            }

            foreach (var face in type.GetInterfaces())
            {
                if (face.IsGenericType && face.GetGenericTypeDefinition() == typeof(ISort<>) && face.GetProperty("Id")?.GetValue(value) is ClassId sortId)
                    return sortId.ToString();
            }

            return value.ToString() ?? string.Empty;
        }

    }

}
