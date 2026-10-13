using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Checks and normalizes a draft into a program and a compiled rule, then lowers the rule's default variant.
    /// Every error is a <see cref="RuleDefinitionException"/> naming the rule. Called under the graph's definition lock.
    /// </summary>
    internal static class RuleCompiler
    {

        /// <summary>
        /// Compiles a rule.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="draft">What the builder collected.</param>
        /// <param name="ruleset">The ruleset that schedules the rule.</param>
        /// <param name="owner">The scope the rule is local to; <c>null</c> for a global rule.</param>
        /// <returns>The compiled rule, its default variant lowered with the written class constants.</returns>
        /// <exception cref="RuleDefinitionException">The draft is not a valid rule, or the engine rejected it; nothing is
        /// registered then (a mirror its first scoped read created stays, harmlessly).</exception>
        public static CompiledRule Compile(EGraph graph, RuleDraft draft, Ruleset ruleset, Scope? owner)
        {
            var licence = draft.Erasing ?? ruleset.Licence;
            var program = new ProgramBuilder(graph, draft, owner, licence: licence).Build();
            var serial = graph.NextSerial();
            var effective = draft.OwnFactsReason is null ? licence : null;
            var rule = new Rule(draft.Name, ruleset, owner, draft.Naive, draft, effective, Array.AsReadOnly(program.ReadScopes));
            var description = (owner is null ? "" : owner.Path + ":") + ruleset.Name + "/" + draft.Name + "#" + serial;
            var variants = new RuleVariants(program, rule, description, draft.Naive);
            var compiled = new CompiledRule(rule, program, variants, serial);
            rule.Compiled = compiled;
            try
            {
                variants.Get(graph, draft.Naive == false);
            }
            catch (Exception exception) when (exception is not RuleDefinitionException)
            {
                variants.Free(graph);
                throw new RuleDefinitionException(draft.Name, "the engine rejected the rule: " + exception.Message, exception);
            }

            return compiled;
        }

        /// <summary>
        /// Compiles a query: the body checked and normalized as a rule's, and a head of one collector of the declared
        /// variables, then lowers its one variant, naive, with the written class constants. Called under the graph's
        /// definition lock.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="draft">What the query's builder collected.</param>
        /// <returns>The compiled query, with its public <see cref="Query"/>.</returns>
        /// <exception cref="RuleDefinitionException">The draft is not a valid query, or the engine rejected it; nothing is
        /// registered then.</exception>
        public static CompiledQuery CompileQuery(EGraph graph, RuleDraft draft)
        {
            var program = new ProgramBuilder(graph, draft, null, query: true).BuildQuery();
            var serial = graph.NextSerial();
            var description = "$query/" + draft.Name + "#" + serial;
            var variants = new RuleVariants(program, null, description, true);
            var columns = program.Head[0].Inputs;
            var types = new ColumnType[columns.Length];
            var variables = new Term[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                types[i] = columns[i].Type;
                variables[i] = draft.Variables[i].Variable;
            }

            var query = new Query(graph, draft.Name, variables) { Draft = draft };
            var compiled = new CompiledQuery(graph, query, program, variants, serial, types);
            query.Compiled = compiled;
            try
            {
                variants.Get(graph, false);
            }
            catch (Exception exception) when (exception is not RuleDefinitionException)
            {
                variants.Free(graph);
                throw new RuleDefinitionException(draft.Name, "the engine rejected the query: " + exception.Message, exception);
            }

            return compiled;
        }

        /// <summary>
        /// Compiles a split rule: checks its shape (one <see cref="SplitBuilder.Cases{T}"/> over a declared set variable; a
        /// key of declared variables, by default every one but the set) and its assumptions, then compiles the body as a
        /// rule's into a naive matcher whose head collects the key, the set and every other variable an assumption uses, and
        /// lowers its one variant. Class constants follow the global-rule rule: only classes from the root. Called under the
        /// graph's definition lock.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="builder">What the split rule's builder collected.</param>
        /// <param name="caseTag">The tag of its cases; <c>null</c> for none.</param>
        /// <returns>The split rule, with its program.</returns>
        /// <exception cref="RuleDefinitionException">The draft is not a valid split rule, or the engine rejected its matcher;
        /// nothing is registered then.</exception>
        public static SplitRule CompileSplit(EGraph graph, SplitBuilder builder, ScopeTag? caseTag)
        {
            var draft = builder.Draft;
            RuleDefinitionException Error(string message, Exception? inner = null) => new(draft.Name, message, inner);

            if (builder.CasesCalls == 0)
                throw Error("a split rule needs Cases: the set whose elements make the cases");

            if (builder.CasesCalls > 1)
                throw Error("Cases is called more than once; a split rule splits over one set");

            var set = builder.Set!;
            var element = builder.Element!;
            var declared = new HashSet<Term>(ReferenceEqualityComparer.Instance);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, _, variable) in draft.Variables)
            {
                declared.Add(variable);
                names.Add(name);
            }

            var elementName = element.Shape.Name!;
            if (names.Contains(elementName))
                throw Error($"variable '{elementName}' is declared twice");

            if (ReferenceEquals(set.Shape.Owner, builder) == false || declared.Contains(set) == false)
                throw Error($"the set of Cases, '{set.Shape.Name}', is not a variable declared in this split rule");

            ColumnType elementType;
            try
            {
                elementType = graph.Values.TypeOf(builder.ElementType!);
            }
            catch (ArgumentException exception)
            {
                throw Error($"type {ValueStore.DisplayName(builder.ElementType!)} is not registered; call {ValueStore.RegisterMethod(builder.ElementType!)}", exception);
            }

            // The key: what a split step compares to skip a request that a live split from the same source already covers
            // in the scope or an ancestor. Declared variables of this split rule, never the case variable.
            var key = new List<Term>();
            if (builder.KeyTerms is { } explicitKey)
            {
                foreach (var term in explicitKey)
                {
                    if (ReferenceEquals(term, element))
                        throw Error($"case variable '{elementName}' cannot be part of the key; the key is what all of a split's cases share");

                    if (term.Shape.Kind != TermShapeKind.Variable || ReferenceEquals(term.Shape.Owner, builder) == false || declared.Contains(term) == false)
                        throw Error($"the key holds {term}, which is not a variable declared in this split rule");

                    if (key.Contains(term) == false)
                        key.Add(term);
                }
            }
            else
            {
                foreach (var (_, _, variable) in draft.Variables)
                    if (ReferenceEquals(variable, set) == false)
                        key.Add(variable);
            }

            var parameters = new List<Term>();
            var assumptions = SplitAssumptions.Compile(graph, draft.Name, builder, builder.Assumptions, declared, element, elementType, parameters);

            // The matcher's columns: the key, the set, then the other variables the assumptions use.
            var columns = new List<Term>(key);
            var keyColumns = new int[key.Count];
            for (var i = 0; i < keyColumns.Length; i++)
                keyColumns[i] = i;

            var setColumn = columns.IndexOf(set);
            if (setColumn < 0)
            {
                setColumn = columns.Count;
                columns.Add(set);
            }

            foreach (var parameter in parameters)
                if (columns.Contains(parameter) == false)
                    columns.Add(parameter);

            var program = new ProgramBuilder(graph, draft, null, caseVariable: element).BuildSplit(columns);
            var types = new ColumnType[columns.Count];
            for (var i = 0; i < types.Length; i++)
                types[i] = program.Head[0].Inputs[i].Type;

            assumptions.Bind(columns, types);
            var serial = graph.NextSerial();
            var variants = new RuleVariants(program, null, "$split/" + draft.Name + "#" + serial, true);
            var rule = new SplitRule(graph, builder, caseTag)
            {
                ReadScopes = Array.AsReadOnly(program.ReadScopes),
                Program = new SplitProgram(program, variants, types, keyColumns, setColumn, elementType, builder.CountOf!, builder.ElementAt!, assumptions),
            };

            try
            {
                variants.Get(graph, false);
            }
            catch (Exception exception) when (exception is not RuleDefinitionException)
            {
                variants.Free(graph);
                throw new RuleDefinitionException(draft.Name, "the engine rejected the split rule: " + exception.Message, exception);
            }

            return rule;
        }

    }

}
