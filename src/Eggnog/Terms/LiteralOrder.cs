using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Eggnog
{

    /// <summary>
    /// The order in which egglog interns the literals of a command or a rule. It lowers each to core actions and query
    /// atoms (src/core.rs, <c>to_core_actions</c> and <c>to_query</c>), each application or computation a <c>let</c> or
    /// an atom before what uses it, and interns the literals as it compiles them, before it evaluates any. So a call's
    /// literal arguments come after those of the calls among its arguments, a fact's own literals after every call in it,
    /// and a rule's query before its actions: <c>(F "a" (G "b") "c")</c> interns <c>"b"</c>, <c>"a"</c>, <c>"c"</c>,
    /// <c>(set (f "k") (g "v"))</c> interns <c>"v"</c> before <c>"k"</c>, and <c>(= "d" (f "e"))</c> <c>"e"</c> before
    /// <c>"d"</c>. A constant egglog has no literal for, a container or a <c>BigInt</c>, is the call it renders as,
    /// <c>(set-of "a" "b")</c> or <c>(from-string "123")</c>: the walks take its parts as that call's literal arguments,
    /// and leave the constant itself to the call's evaluation, as egglog makes the container or the number when it runs
    /// the call. Each walk calls <c>constant</c> on the plain constants in that order, a constant met again included.
    /// </summary>
    internal sealed class LiteralOrder
    {

        readonly Action<TermShape> constant;
        readonly Func<TermShape, TermShape[]?> parts;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="constant">Called on each plain constant, in order.</param>
        /// <param name="parts">Gets the parts of a constant that is a call (<see cref="Parts"/>), or <c>null</c> for a
        /// plain one.</param>
        public LiteralOrder(Action<TermShape> constant, Func<TermShape, TermShape[]?> parts)
        {
            this.constant = constant;
            this.parts = parts;
        }

        /// <summary>
        /// Gets the parts of a constant egglog writes as a call, the call's arguments in order: a set's, a vector's or a
        /// multiset's elements, a pair's two, a map's keys and values in key order, a <c>BigInt</c>'s decimal string;
        /// <c>null</c> for a plain constant. A map is the one call here, where its egglog form is a chain of
        /// <c>map-insert</c>s, which orders the parts that are calls themselves otherwise.
        /// </summary>
        /// <param name="type">The constant's column type.</param>
        /// <param name="value">The constant.</param>
        /// <param name="values">The graph's value store, for a <c>BigInt</c>'s string.</param>
        /// <returns>The parts, or <c>null</c>.</returns>
        public static TermShape[]? Parts(ColumnType type, object value, ValueStore values)
        {
            if (type is IContainerColumnType container)
            {
                var list = new List<(ColumnType Type, object Value)>();
                container.AppendLiteralParts(value, list);
                var shapes = new TermShape[list.Count];
                for (var i = 0; i < shapes.Length; i++)
                    shapes[i] = Constant(list[i].Type, list[i].Value);

                return shapes;
            }

            if (type.ClrType == typeof(BigInteger))
                return [Constant(values.TypeOf<string>(), ((BigInteger)value).ToString(CultureInfo.InvariantCulture))];

            return null;
        }

        /// <summary>
        /// Walks the literals of an application or a computation, as egglog's <c>let</c> or atom for it interns them:
        /// those of the calls among its arguments first, then its own literal arguments, in order. A constant that is a
        /// call walks as one; nothing for a plain constant or a variable.
        /// </summary>
        /// <param name="shape">The term.</param>
        public void Call(TermShape shape)
        {
            switch (shape.Kind)
            {
                case TermShapeKind.Variable:
                    return;
                case TermShapeKind.Constant:
                    if (parts(shape) is { } called)
                        Node(called);

                    return;
                default:
                    Node(Shapes(shape.Arguments));
                    return;
            }
        }

        /// <summary>
        /// Walks the literals of an action over a row: a relation's row, a function's write, a deletion or a subsumption.
        /// The calls among its keys and its value first, in order, then the keys' literals, then the value's.
        /// </summary>
        /// <param name="keys">The row's keys.</param>
        /// <param name="value">A function's written value; <c>null</c> for any other action.</param>
        public void Action(Term[] keys, TermShape? value)
        {
            foreach (var key in keys)
                Call(key.Shape);

            if (value is { } computed)
                Call(computed);

            foreach (var key in keys)
                Own(key.Shape);

            if (value is { } literal)
                Own(literal);
        }

        /// <summary>
        /// Walks the literals of a written fact, a command's or a rule head's: a relation's row; an equality with a
        /// function's application, the write of its value; or any other equality, a union.
        /// </summary>
        /// <param name="fact">The fact.</param>
        public void Write(Fact fact)
        {
            switch (fact)
            {
                case RelationFact relation:
                    Action(relation.Arguments, null);
                    break;
                case EqualityFact equality when IsFunctionApplication(equality.Left.Shape):
                    Action(equality.Left.Shape.Arguments, equality.Right.Shape);
                    break;
                case EqualityFact equality when IsFunctionApplication(equality.Right.Shape):
                    Action(equality.Right.Shape.Arguments, equality.Left.Shape);
                    break;
                case EqualityFact equality:
                    Call(equality.Left.Shape);
                    Call(equality.Right.Shape);
                    Own(equality.Left.Shape);
                    Own(equality.Right.Shape);
                    break;
            }
        }

        /// <summary>
        /// Walks the literals of a rule's body, or a query's, as egglog's lowering compiles them. The facts flatten to
        /// atoms, each call an atom after those of the calls among its arguments, its output a variable of its own; an
        /// equality is an atom of its two sides. Then egglog's <c>canonicalize</c> (src/core.rs:933-1004): while an
        /// equality's sides differ and one is a variable, the first such equality substitutes its other side for the
        /// variable in every atom, so a literal equated with a variable takes the variable's places; then an equality of
        /// two equal sides is dropped, and one of two different literals stays, as egglog's <c>value-eq</c>. The atoms'
        /// literals are walked last, atom by atom, so a literal equated with a variable that no atom uses is not.
        /// </summary>
        /// <param name="body">The facts.</param>
        /// <returns>The literals the substitutions bind at the start of the head, in the head's order: each substitution
        /// binds its variable there before the bindings of those made before it (<c>GenericCoreActions::subst</c>,
        /// core.rs:511-521), and the head compiles them first.</returns>
        public List<TermShape> Query(IEnumerable<Fact> body)
        {
            var atoms = new List<(bool IsEquality, (int Variable, TermShape Literal)[] Arguments)>();
            var variables = new Dictionary<(object? Owner, string? Name), int>();
            var next = 0;
            foreach (var fact in body)
                Flatten(fact, atoms, variables, ref next);

            var bindings = new List<(int Variable, TermShape Literal)>();
            while (true)
            {
                var substituted = -1;
                var by = default((int Variable, TermShape Literal));
                foreach (var (isEquality, arguments) in atoms)
                {
                    if (isEquality == false || Same(arguments[0], arguments[1]))
                        continue;

                    if (arguments[0].Variable >= 0)
                    {
                        (substituted, by) = (arguments[0].Variable, arguments[1]);
                        break;
                    }

                    if (arguments[1].Variable >= 0)
                    {
                        (substituted, by) = (arguments[1].Variable, arguments[0]);
                        break;
                    }
                }

                if (substituted < 0)
                    break;

                foreach (var (_, arguments) in atoms)
                    for (var i = 0; i < arguments.Length; i++)
                        if (arguments[i].Variable == substituted)
                            arguments[i] = by;

                bindings.Insert(0, by);
            }

            foreach (var (isEquality, arguments) in atoms)
            {
                if (isEquality && Same(arguments[0], arguments[1]))
                    continue;

                foreach (var argument in arguments)
                    if (argument.Variable < 0)
                        constant(argument.Literal);
            }

            var bound = new List<TermShape>();
            foreach (var binding in bindings)
                if (binding.Variable < 0)
                    bound.Add(binding.Literal);

            return bound;
        }

        /// <summary>
        /// Flattens a fact of a body into atoms.
        /// </summary>
        void Flatten(Fact fact, List<(bool IsEquality, (int Variable, TermShape Literal)[] Arguments)> atoms,
            Dictionary<(object? Owner, string? Name), int> variables, ref int next)
        {
            switch (fact)
            {
                case RelationFact relation:
                {
                    var arguments = new (int Variable, TermShape Literal)[relation.Arguments.Length];
                    for (var i = 0; i < arguments.Length; i++)
                        arguments[i] = Slot(relation.Arguments[i].Shape, atoms, variables, ref next);

                    atoms.Add((false, arguments));
                    break;
                }
                case EqualityFact equality:
                {
                    var left = Slot(equality.Left.Shape, atoms, variables, ref next);
                    var right = Slot(equality.Right.Shape, atoms, variables, ref next);
                    atoms.Add((true, [left, right]));
                    break;
                }
                case NotEqualFact inequality:
                {
                    var left = Slot(inequality.Left.Shape, atoms, variables, ref next);
                    var right = Slot(inequality.Right.Shape, atoms, variables, ref next);
                    atoms.Add((false, [left, right]));
                    break;
                }
                case FilterFact filter:
                    Slot(filter.Condition.Shape, atoms, variables, ref next);
                    break;
                case ExistsFact exists:
                    Slot(exists.Application.Shape, atoms, variables, ref next);
                    break;
                case ScopedFact scoped:
                    foreach (var inner in scoped.Facts)
                        Flatten(inner, atoms, variables, ref next);

                    break;
            }
        }

        /// <summary>
        /// Gets the argument a term is in an atom: a variable, a literal, or a call's output, a new variable, after the
        /// call's atom and those of the calls among its arguments.
        /// </summary>
        (int Variable, TermShape Literal) Slot(TermShape shape, List<(bool IsEquality, (int Variable, TermShape Literal)[] Arguments)> atoms,
            Dictionary<(object? Owner, string? Name), int> variables, ref int next)
        {
            TermShape[] arguments;
            switch (shape.Kind)
            {
                case TermShapeKind.Variable:
                    if (variables.TryGetValue((shape.Owner, shape.Name), out var known) == false)
                        variables.Add((shape.Owner, shape.Name), known = next++);

                    return (known, default);
                case TermShapeKind.Constant:
                    if (parts(shape) is not { } called)
                        return (-1, shape);

                    arguments = called;
                    break;
                default:
                    arguments = Shapes(shape.Arguments);
                    break;
            }

            var slots = new (int Variable, TermShape Literal)[arguments.Length + 1];
            for (var i = 0; i < arguments.Length; i++)
                slots[i] = Slot(arguments[i], atoms, variables, ref next);

            // A call's output is a variable no fact names.
            var output = next++;
            slots[^1] = (output, default);
            atoms.Add((false, slots));
            return (output, default);
        }

        /// <summary>
        /// Walks the literals of a call of <paramref name="arguments"/>: the calls among them first, then the plain
        /// constants.
        /// </summary>
        void Node(TermShape[] arguments)
        {
            foreach (var argument in arguments)
                Call(argument);

            foreach (var argument in arguments)
                Own(argument);
        }

        /// <summary>
        /// Calls <see cref="constant"/> on a plain constant; nothing for a constant that is a call, or any other shape.
        /// </summary>
        void Own(TermShape shape)
        {
            if (shape.Kind == TermShapeKind.Constant && parts(shape) is null)
                constant(shape);
        }

        /// <summary>
        /// Gets whether two atom arguments are one: the same variable, or equal literals.
        /// </summary>
        static bool Same((int Variable, TermShape Literal) a, (int Variable, TermShape Literal) b)
        {
            if (a.Variable >= 0 || b.Variable >= 0)
                return a.Variable == b.Variable;

            return a.Literal.ClrType == b.Literal.ClrType && Equals(a.Literal.Constant, b.Literal.Constant);
        }

        /// <summary>
        /// Gets the shapes of terms.
        /// </summary>
        static TermShape[] Shapes(Term[] terms)
        {
            var shapes = new TermShape[terms.Length];
            for (var i = 0; i < shapes.Length; i++)
                shapes[i] = terms[i].Shape;

            return shapes;
        }

        /// <summary>
        /// Makes the shape of a constant of a column type.
        /// </summary>
        static TermShape Constant(ColumnType type, object value) =>
            new(TermShapeKind.Constant, type.ClrType, null, null, type, value, null, null, Array.Empty<Term>());

        /// <summary>
        /// Gets whether a shape applies a function, whose application an equality writes.
        /// </summary>
        static bool IsFunctionApplication(TermShape shape) =>
            shape.Kind == TermShapeKind.Application && shape.Table!.Kind == TableKind.Function;

    }

}
