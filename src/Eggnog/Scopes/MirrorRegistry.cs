using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// The mirror tables and copy rules of a graph's scoped reads, and the count of live frozen scopes per tag (§6.8,
    /// D38). Mutated under the graph's definition lock; the per-tag copy-rule lists are read lock-free.
    /// </summary>
    /// <remarks>
    /// A mirror is created by the first scoped read of its <c>(table, tag)</c>, which is a table definition (D6), and is
    /// refused while a live frozen scope of this graph carries the tag: a frozen scope's mirrors were synced at its
    /// freeze and are never synced again, so a mirror created later would stay empty there and break invariant M. The
    /// counts belong to the graph, so one graph's frozen schemas never affect another's theory (D38).
    /// </remarks>
    internal sealed class MirrorRegistry
    {

        readonly EGraph graph;
        readonly object sync = new();
        readonly Dictionary<(TableHandle Table, ScopeTag Tag), MirrorTable> mirrors = new();
        readonly Ruleset copyRuleset;
        CompiledRule[][] copyRules = Array.Empty<CompiledRule[]>();
        CompiledRule[] allCopyRules = Array.Empty<CompiledRule>();
        int[] frozenCounts = Array.Empty<int>();

        /// <summary>
        /// Initializes an empty registry.
        /// </summary>
        /// <param name="graph">The graph.</param>
        public MirrorRegistry(EGraph graph)
        {
            this.graph = graph;
            copyRuleset = new Ruleset(graph, "$mirror");
        }

        /// <summary>
        /// Gets the mirror of <paramref name="table"/> under <paramref name="tag"/>, creating it, with its copy rule, on the
        /// first read (§6.3 step 2). Called under the graph's definition lock.
        /// </summary>
        /// <param name="table">The value-only table read scoped.</param>
        /// <param name="tag">The tag.</param>
        /// <param name="reader">The name of the rule or query that reads it, for errors.</param>
        /// <returns>The mirror.</returns>
        /// <exception cref="RuleDefinitionException">A live frozen scope of this graph carries the tag and the mirror is
        /// new.</exception>
        /// <exception cref="InvalidOperationException">The mirror is new and another thread is using the writable root
        /// (a table definition, D6).</exception>
        public MirrorTable GetOrCreate(TableHandle table, ScopeTag tag, string reader)
        {
            lock (sync)
            {
                if (mirrors.TryGetValue((table, tag), out var existing))
                    return existing;

                if (FrozenCountCore(tag) > 0)
                    throw new RuleDefinitionException(reader,
                        $"'{table.Name}' is first read in scope '{tag.Name}' after a scope tagged '{tag.Name}' was frozen; define rules that read it before freezing such scopes");
            }

            var handle = graph.DefineMirrorTable(table, tag, -1 - mirrors.Count);
            var copyRule = CompileCopyRule(table, handle);
            var mirror = new MirrorTable(table, tag, handle, copyRule);
            lock (sync)
            {
                mirrors.Add((table, tag), mirror);
                var rules = copyRules;
                if (tag.Serial >= rules.Length)
                    Array.Resize(ref rules, Math.Max(tag.Serial + 1, rules.Length * 2));
                else
                    rules = (CompiledRule[][])rules.Clone();

                var forTag = rules[tag.Serial] ?? Array.Empty<CompiledRule>();
                var nextForTag = new CompiledRule[forTag.Length + 1];
                forTag.CopyTo(nextForTag, 0);
                nextForTag[^1] = copyRule;
                rules[tag.Serial] = nextForTag;
                var all = new CompiledRule[allCopyRules.Length + 1];
                allCopyRules.CopyTo(all, 0);
                all[^1] = copyRule;
                Volatile.Write(ref copyRules, rules);
                Volatile.Write(ref allCopyRules, all);
            }

            return mirror;
        }

        /// <summary>
        /// Gets the mirrors of one definition's scoped reads, all or nothing (§6.3 step 2): every new mirror's tag is checked
        /// against the frozen counts first, and the new mirrors are created while this thread holds the writable root's
        /// guard, so that a refusal leaves no mirror behind. Called under the graph's definition lock, once every other check
        /// of the definition has passed.
        /// </summary>
        /// <param name="reads">The distinct <c>(table, tag)</c> pairs the definition reads.</param>
        /// <param name="reader">The name of the rule or query that reads them, for errors.</param>
        /// <returns>The mirrors, in the order of <paramref name="reads"/>.</returns>
        /// <exception cref="RuleDefinitionException">A mirror would be new and a live frozen scope of this graph carries its
        /// tag; nothing was created.</exception>
        /// <exception cref="InvalidOperationException">A mirror would be new and another thread is using the writable root
        /// (a table definition, D6); nothing was created.</exception>
        public MirrorTable[] GetOrCreateAll(IReadOnlyList<(TableHandle Table, ScopeTag Tag)> reads, string reader)
        {
            var found = new MirrorTable[reads.Count];
            var missing = false;
            lock (sync)
            {
                for (var i = 0; i < reads.Count; i++)
                {
                    var (table, tag) = reads[i];
                    if (mirrors.TryGetValue((table, tag), out var existing))
                    {
                        found[i] = existing;
                        continue;
                    }

                    if (FrozenCountCore(tag) > 0)
                        throw new RuleDefinitionException(reader,
                            $"'{table.Name}' is first read in scope '{tag.Name}' after a scope tagged '{tag.Name}' was frozen; define rules that read it before freezing such scopes");

                    missing = true;
                }
            }

            if (missing == false)
                return found;

            // One entry into the writable root's domain covers every table definition below, so a busy root refuses them all
            // before the first.
            var root = graph.Root;
            var guarded = root.IsFrozen == false;
            if (guarded)
                root.EnterGuard();

            try
            {
                for (var i = 0; i < reads.Count; i++)
                    found[i] ??= GetOrCreate(reads[i].Table, reads[i].Tag, reader);
            }
            finally
            {
                if (guarded)
                    root.Guard.Exit();
            }

            return found;
        }

        /// <summary>
        /// Gets an existing mirror.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="tag">The tag.</param>
        /// <returns>The mirror.</returns>
        /// <exception cref="KeyNotFoundException">No such mirror.</exception>
        public MirrorTable Get(TableHandle table, ScopeTag tag)
        {
            lock (sync)
                return mirrors[(table, tag)];
        }

        /// <summary>
        /// Gets the copy rules of <paramref name="tag"/>'s mirrors, in creation order (a copy-on-write snapshot, read
        /// lock-free).
        /// </summary>
        /// <param name="tag">The tag.</param>
        /// <returns>The copy rules.</returns>
        public CompiledRule[] CopyRules(ScopeTag tag)
        {
            var rules = Volatile.Read(ref copyRules);
            return tag.Serial < rules.Length && rules[tag.Serial] is { } forTag ? forTag : Array.Empty<CompiledRule>();
        }

        /// <summary>
        /// Gets every copy rule of every tag (a snapshot): the plans of a freeze (E10).
        /// </summary>
        /// <returns>The copy rules.</returns>
        public CompiledRule[] AllCopyRules() => Volatile.Read(ref allCopyRules);

        /// <summary>
        /// Gets the number of live frozen scopes of this graph that carry <paramref name="tag"/>.
        /// </summary>
        /// <param name="tag">The tag.</param>
        /// <returns>The count.</returns>
        public int FrozenCount(ScopeTag tag)
        {
            lock (sync)
                return FrozenCountCore(tag);
        }

        /// <summary>
        /// Counts a scope tagged <paramref name="tag"/> as frozen: from now on a first scoped read of a table under the tag
        /// is refused.
        /// </summary>
        /// <param name="tag">The tag.</param>
        public void AddFrozen(ScopeTag tag)
        {
            lock (sync)
            {
                if (tag.Serial >= frozenCounts.Length)
                    Array.Resize(ref frozenCounts, Math.Max(tag.Serial + 1, frozenCounts.Length * 2));

                frozenCounts[tag.Serial]++;
            }
        }

        /// <summary>
        /// Uncounts a released frozen scope tagged <paramref name="tag"/>.
        /// </summary>
        /// <param name="tag">The tag.</param>
        public void RemoveFrozen(ScopeTag tag)
        {
            lock (sync)
                frozenCounts[tag.Serial]--;
        }

        /// <summary>
        /// Gets the frozen count under the lock.
        /// </summary>
        int FrozenCountCore(ScopeTag tag) => tag.Serial < frozenCounts.Length ? frozenCounts[tag.Serial] : 0;

        /// <summary>
        /// Compiles the copy rule of a mirror (§6.8): one register per column, one atom over the source, one write into
        /// the mirror; semi-naive, with its default variant lowered now, under the description
        /// <c>$mirror/{mirror name}#{serial}</c> (§6.4.3).
        /// </summary>
        CompiledRule CompileCopyRule(TableHandle source, TableHandle mirror)
        {
            var columns = source.IsRelation ? source.KeyArray : source.Columns;
            var registers = new ProgramRegister[columns.Length];
            var operands = new Operand[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                registers[i] = new ProgramRegister(columns[i], "x" + i.ToString(CultureInfo.InvariantCulture), RegisterKind.Declared);
                operands[i] = Operand.Register(i, columns[i]);
            }

            var head = new HeadOp(source.IsRelation ? HeadOpKind.Insert : HeadOpKind.Write, mirror, operands, -1, null);
            var name = "copy " + mirror.Name;
            var program = new RuleProgram(name, registers, [new ProgramAtom(source, null, operands)], Array.Empty<ProgramStep>(), [head],
                Array.Empty<uint>(), Array.Empty<ScopeOrigin>(), Array.Empty<int>(), false, Array.Empty<ScopeTag>());
            var serial = graph.NextSerial();
            var rule = new Rule(name, copyRuleset, null, false, new RuleDraft(name, this), null, Array.Empty<ScopeTag>());
            var variants = new RuleVariants(program, null, "$mirror/" + mirror.Name + "#" + serial.ToString(CultureInfo.InvariantCulture), false);
            var compiled = new CompiledRule(rule, program, variants, serial);
            rule.Compiled = compiled;
            variants.Get(graph, true);
            return compiled;
        }

    }

}
