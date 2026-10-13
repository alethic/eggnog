using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The assumptions of a split rule as written (§6.9.2), checked at definition like host-write facts (§6.6.2) with the
    /// declared variables and the case variable as parameters, and grounded per case: each variable becomes the constant a
    /// match or the case's element binds, and each computation is evaluated on its ground arguments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The checks at definition (<see cref="Compile"/>): every fact is a write (a relation row, a function value, a
    /// constructor output, or a union of classes); no table is read in a term (a function application is refused with
    /// <i>"assumptions do not read; match 'dom' in When"</i>), none is scoped or of another graph; a primitive takes and
    /// returns values, with arguments of its parameters' types; a class constant is from the root, as for a global rule
    /// (§6.3 step 3); a variable is declared in the split rule or is its case variable. Grounding then never fails for
    /// want of a check, only for a primitive that throws or a conflict the host batch finds (D32).
    /// </para>
    /// <para>
    /// Cost: grounding allocates the ground facts of one case, a few objects per term; it runs once per case opened, not
    /// per row.
    /// </para>
    /// </remarks>
    internal sealed class SplitAssumptions
    {

        readonly EGraph graph;
        readonly string name;
        readonly Fact[] facts;
        readonly Term element;
        readonly ColumnType elementType;
        readonly Dictionary<Term, (ColumnType[] Parameters, ColumnType Result)> computations;
        Dictionary<Term, (int Column, ColumnType Type)> columns = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Initializes the template of checked facts.
        /// </summary>
        SplitAssumptions(EGraph graph, string name, Fact[] facts, Term element, ColumnType elementType,
            Dictionary<Term, (ColumnType[] Parameters, ColumnType Result)> computations)
        {
            this.graph = graph;
            this.name = name;
            this.facts = facts;
            this.element = element;
            this.elementType = elementType;
            this.computations = computations;
        }

        /// <summary>
        /// Gets the facts as written.
        /// </summary>
        public Fact[] Facts => facts;

        /// <summary>
        /// Checks the assumptions of a split rule (§6.9.2) and lists the declared variables they use, in first-use order,
        /// which the matcher must collect.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="name">The split rule's name, for messages.</param>
        /// <param name="owner">The builder whose variables the facts may use.</param>
        /// <param name="facts">The assumptions.</param>
        /// <param name="declared">The declared body variables.</param>
        /// <param name="element">The case variable.</param>
        /// <param name="elementType">The case variable's column type.</param>
        /// <param name="parameters">Receives the declared variables the facts use, in first-use order.</param>
        /// <returns>The template.</returns>
        /// <exception cref="RuleDefinitionException">An assumption is not a ground host write once its variables are
        /// bound.</exception>
        public static SplitAssumptions Compile(EGraph graph, string name, object owner, IReadOnlyList<Fact> facts,
            ISet<Term> declared, Term element, ColumnType elementType, List<Term> parameters)
        {
            var computations = new Dictionary<Term, (ColumnType[] Parameters, ColumnType Result)>(ReferenceEqualityComparer.Instance);
            var checker = new SplitAssumptionChecker(graph, name, owner, declared, element, parameters, computations);
            foreach (var fact in facts)
                checker.CheckFact(fact);

            var copy = new Fact[facts.Count];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = facts[i];

            return new SplitAssumptions(graph, name, copy, element, elementType, computations);
        }

        /// <summary>
        /// Binds each declared variable the facts use to its column of the matcher's tuples.
        /// </summary>
        /// <param name="tupleColumns">The matcher's collected variables, in column order.</param>
        /// <param name="types">Their column types.</param>
        public void Bind(IReadOnlyList<Term> tupleColumns, ColumnType[] types)
        {
            var bound = new Dictionary<Term, (int Column, ColumnType Type)>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < tupleColumns.Count; i++)
                bound.TryAdd(tupleColumns[i], (i, types[i]));

            columns = bound;
        }

        /// <summary>
        /// Grounds the facts for one case: each declared variable becomes the value of its column in
        /// <paramref name="tuple"/> (a class with <paramref name="parent"/>'s origin, usable in the case, D39), the case
        /// variable becomes <paramref name="value"/>, and each computation is evaluated.
        /// </summary>
        /// <param name="tuple">The match, as the matcher collected it.</param>
        /// <param name="value">The case's element.</param>
        /// <param name="parent">The scope the split was made in.</param>
        /// <returns>The ground facts.</returns>
        /// <exception cref="RuleEvaluationException">A primitive threw.</exception>
        public Fact[] Instantiate(ReadOnlySpan<uint> tuple, object value, Scope parent)
        {
            var ground = new Fact[facts.Length];
            for (var i = 0; i < ground.Length; i++)
                ground[i] = Ground(facts[i], tuple, value, parent);

            return ground;
        }

        /// <summary>
        /// Grounds one fact.
        /// </summary>
        Fact Ground(Fact fact, ReadOnlySpan<uint> tuple, object value, Scope parent)
        {
            switch (fact)
            {
                case RelationFact relation:
                {
                    var arguments = new Term[relation.Arguments.Length];
                    for (var i = 0; i < arguments.Length; i++)
                        arguments[i] = Ground(relation.Arguments[i], tuple, value, parent);

                    return new RelationFact(relation.Table, arguments);
                }
                default:
                {
                    var equality = (EqualityFact)fact;
                    return new EqualityFact(Ground(equality.Left, tuple, value, parent), Ground(equality.Right, tuple, value, parent));
                }
            }
        }

        /// <summary>
        /// Grounds one term: a variable becomes a constant, an application keeps its table over ground arguments, and a
        /// computation becomes the constant it evaluates to.
        /// </summary>
        Term Ground(Term term, ReadOnlySpan<uint> tuple, object value, Scope parent)
        {
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Constant:
                    return term;
                case TermShapeKind.Variable:
                {
                    if (ReferenceEquals(term, element))
                        return new UntypedConstantTerm(elementType, value);

                    var (column, type) = columns[term];
                    return new UntypedConstantTerm(type, type.DecodeObject(tuple[column], parent));
                }
                case TermShapeKind.Application:
                {
                    var arguments = new Term[shape.Arguments.Length];
                    for (var i = 0; i < arguments.Length; i++)
                        arguments[i] = Ground(shape.Arguments[i], tuple, value, parent);

                    return new UntypedApplicationTerm(shape.Table!, arguments);
                }
                default:
                {
                    var (parameters, result) = computations[term];
                    var cells = new uint[shape.Arguments.Length];
                    for (var i = 0; i < cells.Length; i++)
                    {
                        var argument = Ground(shape.Arguments[i], tuple, value, parent).Shape;
                        cells[i] = parameters[i].EncodeObject(argument.Constant!);
                    }

                    var primitive = shape.Primitive!;
                    uint cell;
                    EngineCallMarker.Enter();
                    try
                    {
                        cell = primitive.InvokeCore(cells, parameters, result);
                    }
                    catch (Exception exception)
                    {
                        throw new RuleEvaluationException(name, null,
                            $"split '{name}': primitive '{primitive.Name}' threw {exception.GetType().Name}: {exception.Message}", exception);
                    }
                    finally
                    {
                        EngineCallMarker.Exit();
                    }

                    return new UntypedConstantTerm(result, result.DecodeObject(cell, parent));
                }
            }
        }

    }

}
