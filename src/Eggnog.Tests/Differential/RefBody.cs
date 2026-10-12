using System;
using System.Collections.Generic;
using System.Globalization;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A rule's or query's body as the reference interpreter evaluates it: atoms joined in order, then steps (lets,
    /// filters, equalities, disequalities) run once their inputs are bound. It flattens nested applications and
    /// computations itself, independently of the rule layer's compiler.
    /// </summary>
    internal sealed class RefBody
    {

        readonly Dictionary<string, int> variables = new(StringComparer.Ordinal);
        readonly List<string> names = new();
        readonly List<RefAtom> atoms = new();
        readonly List<RefStep> steps = new();
        int generated;

        /// <summary>
        /// Compiles a rule's or query's body; a rewrite head item adds its pattern to the body, bound to a generated class
        /// variable, which <see cref="RewriteVariables"/> gives by head item.
        /// </summary>
        /// <param name="rule">The rule or query.</param>
        public RefBody(DiffRule rule)
        {
            foreach (var (name, _) in rule.Variables)
                Declare(name);

            foreach (var fact in rule.Body)
                Add(fact);

            for (var i = 0; i < rule.Head.Count; i++)
            {
                var item = rule.Head[i];
                if (item.Kind != DiffHeadKind.Rewrite)
                    continue;

                var variable = Generated();
                RewriteVariables[i] = names[variable];
                Flatten(item.Left!, new RefSlot(variable, null));
            }
        }

        /// <summary>Gets the variables' names, by index: the declared ones first.</summary>
        public IReadOnlyList<string> Names => names;

        /// <summary>Gets the atoms, in join order.</summary>
        public IReadOnlyList<RefAtom> Atoms => atoms;

        /// <summary>Gets the steps.</summary>
        public IReadOnlyList<RefStep> Steps => steps;

        /// <summary>Gets the generated class variable of each rewrite, by head item index.</summary>
        public Dictionary<int, string> RewriteVariables { get; } = new();

        /// <summary>
        /// Gets a variable's index.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns>The index.</returns>
        public int IndexOf(string name) => variables[name];

        /// <summary>
        /// Adds a body fact.
        /// </summary>
        void Add(DiffFact fact)
        {
            switch (fact.Kind)
            {
                case DiffFactKind.Relation:
                {
                    var slots = new RefSlot[fact.Arguments.Length];
                    for (var i = 0; i < slots.Length; i++)
                        slots[i] = Flatten(fact.Arguments[i], null);

                    atoms.Add(new RefAtom(fact.Table!, slots));
                    break;
                }
                case DiffFactKind.Equal:
                {
                    var left = fact.Left!;
                    var right = fact.Right!;
                    if (left.Kind == DiffTermKind.App && right.Kind is DiffTermKind.Var or DiffTermKind.Const)
                        Flatten(left, Flatten(right, null));
                    else if (right.Kind == DiffTermKind.App && left.Kind is DiffTermKind.Var or DiffTermKind.Const)
                        Flatten(right, Flatten(left, null));
                    else if (left.Kind == DiffTermKind.Prim)
                        Flatten(left, Flatten(right, null));
                    else if (right.Kind == DiffTermKind.Prim)
                        Flatten(right, Flatten(left, null));
                    else
                        steps.Add(new RefStep(RefStepKind.Equal, null, new[] { Flatten(left, null), Flatten(right, null) }, default));

                    break;
                }
                case DiffFactKind.NotEqual:
                    steps.Add(new RefStep(RefStepKind.NotEqual, null, new[] { Flatten(fact.Left!, null), Flatten(fact.Right!, null) }, default));
                    break;
                case DiffFactKind.Filter:
                {
                    var condition = fact.Left!;
                    var arguments = new RefSlot[condition.Arguments.Length];
                    for (var i = 0; i < arguments.Length; i++)
                        arguments[i] = Flatten(condition.Arguments[i], null);

                    steps.Add(new RefStep(RefStepKind.Filter, condition.Primitive, arguments, default));
                    break;
                }
                default:
                    Flatten(fact.Left!, null);
                    break;
            }
        }

        /// <summary>
        /// Turns a term into a slot: a variable, a constant, or the output of an atom or let made for it. With
        /// <paramref name="output"/>, an application or computation produces into that slot (binding or checking it).
        /// </summary>
        RefSlot Flatten(DiffTerm term, RefSlot? output)
        {
            switch (term.Kind)
            {
                case DiffTermKind.Var:
                {
                    var slot = new RefSlot(variables[term.Name!], null);
                    if (output is { } target)
                        steps.Add(new RefStep(RefStepKind.Equal, null, new[] { slot, target }, default));

                    return slot;
                }
                case DiffTermKind.Const:
                {
                    var slot = new RefSlot(-1, term);
                    if (output is { } target)
                        steps.Add(new RefStep(RefStepKind.Equal, null, new[] { slot, target }, default));

                    return slot;
                }
                case DiffTermKind.App:
                {
                    var slots = new RefSlot[term.Arguments.Length + 1];
                    for (var i = 0; i < term.Arguments.Length; i++)
                        slots[i] = Flatten(term.Arguments[i], null);

                    slots[^1] = output ?? new RefSlot(Generated(), null);
                    atoms.Add(new RefAtom(term.Table!, slots));
                    return slots[^1];
                }
                default:
                {
                    var arguments = new RefSlot[term.Arguments.Length];
                    for (var i = 0; i < arguments.Length; i++)
                        arguments[i] = Flatten(term.Arguments[i], null);

                    var target = output ?? new RefSlot(Generated(), null);
                    steps.Add(new RefStep(RefStepKind.Let, term.Primitive, arguments, target));
                    return target;
                }
            }
        }

        /// <summary>
        /// Declares a variable.
        /// </summary>
        int Declare(string name)
        {
            var index = names.Count;
            names.Add(name);
            variables.Add(name, index);
            return index;
        }

        /// <summary>
        /// Declares a generated variable.
        /// </summary>
        int Generated() => Declare("$g" + (generated++).ToString(CultureInfo.InvariantCulture));

    }

}
