using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The operational oracle of the rule differentials (colored-contexts spec §17 M10.3, X41, R1-5): a
    /// <see cref="SpikeReferenceContext"/> (its ordered assertions, deletes and clears included) with rule checkpoints.
    /// At each rule iteration in a context X it computes X's view from the ordered ops of X's chain, evaluates the rules
    /// naively over it (<see cref="ReferenceRuleEvaluator"/>) with the engine's iteration schedule, and appends what they
    /// derive to X's ops as <see cref="SpikeReferenceOpKind.Derived"/> assertions, which descendants inherit like any
    /// assertion. So facts derived in an ancestor under fewer equalities, or before a child's delete, persist as they do in
    /// the engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schedule.</b> One iteration is one Jacobi step: every listed rule reads the same view, and their derivations
    /// are asserted together; the next view's closure (<see cref="SpikeReferenceView.Close"/>) is the rebuild. A saturation
    /// repeats iterations as <see cref="EGraphContext.Saturate"/> does; the harness runs as many as the engine reports.
    /// </para>
    /// <para>
    /// <b>Semi-naive selection.</b> A naive re-evaluation is not the engine's semantics once a body or an action is not
    /// monotone: a derived fact a child deletes stays deleted until one of its premises changes, and an action's read sees
    /// the row of the iteration that fired it. So each rule fires only the matches that use a row it has not seen: one
    /// whose canonical content was not present at the rule's watermark, or was deleted since (§10.2 on content, not
    /// timestamps). The watermark is the tick of the rule's last run in X when its id constants' canonical forms (K) are
    /// the same; else, for a rule that reads no table in an action, in a context that inherits, the nearest ancestor's,
    /// bounded by the creation tick of the ancestor's child on the path, under the same K test; else 0, a full run.
    /// The reference decides which rules may inherit from the rule itself (<see cref="ColorRule.ReadsTables"/>), not from
    /// the engine's analysis. Ticks follow the engine's epoch order because the harness flushes every write as it makes it.
    /// </para>
    /// <para>
    /// Views are recomputed from the ops, so nothing here is incremental beyond a small cache of views at past ticks,
    /// which cannot change: every later op has a later tick.
    /// </para>
    /// </remarks>
    sealed class ReferenceRuleContext
    {

        /// <summary>
        /// The most views cached at past ticks before the cache is cleared.
        /// </summary>
        const int MaxCachedViews = 32;

        readonly ColorSchema schema;
        readonly ReferenceRuleClock clock;
        readonly SpikeReferenceContext log;
        readonly List<long> times = new();
        readonly Dictionary<string, ReferenceRuleProgress> progress = new();
        readonly Dictionary<long, SpikeReferenceView> pastViews = new();
        SpikeReferenceView? current;
        long currentOps = -1;

        /// <summary>
        /// Initializes a root over <paramref name="schema"/>'s tables, on <paramref name="clock"/>.
        /// </summary>
        public ReferenceRuleContext(ColorSchema schema, ReferenceRuleClock clock)
            : this(schema, clock, null, true)
        {

        }

        /// <summary>
        /// Initializes a context.
        /// </summary>
        ReferenceRuleContext(ColorSchema schema, ReferenceRuleClock clock, ReferenceRuleContext? parent, bool inheritWatermarks)
        {
            this.schema = schema;
            this.clock = clock;
            Parent = parent;
            InheritWatermarks = inheritWatermarks;
            log = new SpikeReferenceContext(schema.Tables, parent?.log, new HashSet<ulong>());
            CreatedAt = parent is null ? 0 : clock.Tick();
        }

        /// <summary>
        /// Gets the parent, or <c>null</c>.
        /// </summary>
        public ReferenceRuleContext? Parent { get; }

        /// <summary>
        /// Gets the tick of the context's creation; 0 for the root.
        /// </summary>
        public long CreatedAt { get; }

        /// <summary>
        /// Gets whether rules may seed their watermarks from an ancestor here (<see cref="ContextOptions.InheritWatermarks"/>).
        /// </summary>
        public bool InheritWatermarks { get; }

        /// <summary>
        /// Gets the context's ordered ops.
        /// </summary>
        public SpikeReferenceContext Log => log;

        /// <summary>
        /// Creates a child, at the next tick.
        /// </summary>
        public ReferenceRuleContext CreateChild(bool inheritWatermarks = true) => new(schema, clock, this, inheritWatermarks);

        /// <summary>
        /// Asserts a logical row of table <paramref name="table"/>.
        /// </summary>
        public void Insert(int table, ulong[] row)
        {
            log.Insert(schema.Tables[table], row);
            times.Add(clock.Tick());
        }

        /// <summary>
        /// Asserts a union.
        /// </summary>
        public void Union(ulong a, ulong b)
        {
            log.Union(a, b);
            times.Add(clock.Tick());
        }

        /// <summary>
        /// Retracts the row of table <paramref name="table"/> with key <paramref name="key"/>.
        /// </summary>
        public void Delete(int table, ulong[] key)
        {
            log.Delete(schema.Tables[table], key);
            times.Add(clock.Tick());
        }

        /// <summary>
        /// Retracts every row of table <paramref name="table"/>.
        /// </summary>
        public void Clear(int table)
        {
            log.Clear(schema.Tables[table]);
            times.Add(clock.Tick());
        }

        /// <summary>
        /// Adds the term <paramref name="table"/>(<paramref name="key"/>) and returns its canonical id: the view's, or a
        /// minted one whose row is asserted (family B's host terms).
        /// </summary>
        public ulong AddTerm(int table, ulong[] key)
        {
            var view = View();
            if (view.TryLookup(schema.Tables[table], key, out var id))
                return id;

            id = clock.MintId();
            var row = new ulong[key.Length + 1];
            key.CopyTo(row, 0);
            row[^1] = id;
            Insert(table, row);
            return id;
        }

        /// <summary>
        /// Gets the context's current closed view. Read it only; it is cached.
        /// </summary>
        public SpikeReferenceView View()
        {
            var ops = ChainOpCount();
            if (current is null || currentOps != ops)
            {
                current = Replay(long.MaxValue);
                currentOps = ops;
            }

            return current;
        }

        /// <summary>
        /// Gets the context's closed view just before tick <paramref name="time"/>: the chain's ops with earlier ticks.
        /// Read it only; it is cached.
        /// </summary>
        public SpikeReferenceView ViewAt(long time)
        {
            if (time > clock.Now)
                return View();

            if (pastViews.TryGetValue(time, out var view))
                return view;

            if (pastViews.Count >= MaxCachedViews)
                pastViews.Clear();

            view = Replay(time);
            pastViews[time] = view;
            return view;
        }

        /// <summary>
        /// Runs one iteration of <paramref name="rules"/> (§10.3): at a new tick T, every rule's matches over the view before
        /// T that use a row it has not seen fire, its progress becomes (T, K), and what they derive is asserted at T.
        /// </summary>
        /// <param name="rules">The rules, their id constants resolved.</param>
        /// <param name="fired">Receives the number of matches each rule fired, or <c>null</c>.</param>
        /// <returns>Whether the iteration derived anything the view did not hold.</returns>
        public bool RunIteration(IReadOnlyList<ColorRule> rules, int[]? fired = null)
        {
            ArgumentNullException.ThrowIfNull(rules);
            var time = clock.Tick();
            var view = ViewAt(time);
            var output = new ReferenceRuleDerivations(clock);
            var staged = new ReferenceRuleProgress[rules.Count];
            var seenAt = new Dictionary<long, HashSet<string>>();
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                var constants = rule.IdConstants().Select(view.Find).ToArray();
                var watermark = Watermark(rule, constants);
                HashSet<string>? seen = null;
                if (watermark > 0 && seenAt.TryGetValue(watermark, out seen) == false)
                {
                    seen = Seen(watermark, time);
                    seenAt[watermark] = seen;
                }

                var count = ReferenceRuleEvaluator.Evaluate(schema, rule, view, seen, output);
                if (fired is not null)
                    fired[i] = count;

                staged[i] = new ReferenceRuleProgress(time, constants);
            }

            for (var i = 0; i < rules.Count; i++)
                progress[rules[i].Name] = staged[i];

            // Assert what changes the view, at T.
            var changed = false;
            foreach (var (table, row) in output.Rows)
            {
                if (Holds(view, table, row))
                    continue;

                log.Derive(table, row);
                times.Add(time);
                changed = true;
            }

            foreach (var (a, b) in output.Unions)
            {
                if (view.Find(a) == view.Find(b))
                    continue;

                log.DeriveUnion(a, b);
                times.Add(time);
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Gets rule <paramref name="rule"/>'s watermark here for a run whose id constants are canonically
        /// <paramref name="constants"/> (§10.2): its own progress, or one seeded from the nearest ancestor with progress.
        /// </summary>
        public long Watermark(ColorRule rule, IReadOnlyList<ulong> constants)
        {
            if (progress.TryGetValue(rule.Name, out var own))
                return own.SameConstants(constants) ? own.Time : 0;

            if (Parent is null || rule.ReadsTables || InheritWatermarks == false)
                return 0;

            var bound = long.MaxValue;
            for (var c = this; c.Parent is { } up; c = up)
            {
                bound = Math.Min(bound, c.CreatedAt);
                if (up.progress.TryGetValue(rule.Name, out var inherited))
                {
                    if (inherited.SameConstants(constants) == false)
                        return 0;

                    clock.InheritedRuns++;
                    return Math.Min(inherited.Time, bound);
                }
            }

            return 0;
        }

        /// <summary>
        /// Gets the chain's ops with their ticks, root first.
        /// </summary>
        public IEnumerable<(SpikeReferenceOp Op, long Time)> ChainOps()
        {
            var chain = new List<ReferenceRuleContext>();
            for (var c = this; c is not null; c = c.Parent)
                chain.Insert(0, c);

            foreach (var c in chain)
                for (var i = 0; i < c.times.Count; i++)
                    yield return (c.log.Ops[i], c.times[i]);
        }

        /// <summary>
        /// Gets the rows a rule with watermark <paramref name="watermark"/> has seen at tick <paramref name="time"/>: those
        /// present with the same canonical content just before the watermark and after every delete or clear of the
        /// chain since. A content that disappears otherwise never returns (unions and lattice merges are monotone), so
        /// these are exactly the rows present ever since.
        /// </summary>
        HashSet<string> Seen(long watermark, long time)
        {
            var seen = Keys(ViewAt(watermark));
            foreach (var (op, at) in ChainOps())
                if (op.Kind is SpikeReferenceOpKind.Delete or SpikeReferenceOpKind.Clear && at > watermark && at < time)
                    seen.IntersectWith(Keys(ViewAt(at + 1)));

            return seen;
        }

        /// <summary>
        /// Gets the content keys of every row of <paramref name="view"/>.
        /// </summary>
        HashSet<string> Keys(SpikeReferenceView view)
        {
            var keys = new HashSet<string>();
            foreach (var table in schema.Tables)
                foreach (var row in view.Rows(table))
                    keys.Add(ReferenceRuleEvaluator.RowKey(table, row));

            return keys;
        }

        /// <summary>
        /// Gets whether <paramref name="view"/> already holds what asserting <paramref name="row"/> would say: the key's row
        /// for a relation, a value its merge with <paramref name="row"/>'s keeps, or an output in the same class.
        /// </summary>
        static bool Holds(SpikeReferenceView view, SpikeTable table, ulong[] row)
        {
            if (view.TryLookup(table, row.AsSpan(0, table.KeyCount), out var output) == false)
                return false;

            if (table.HasOutput == false)
                return true;

            var incoming = row[table.KeyCount];
            return table.OutputIsId ? view.Find(output) == view.Find(incoming) : table.MergeValues(output, incoming) == output;
        }

        /// <summary>
        /// Replays the chain's ops with ticks before <paramref name="time"/> into a closed view.
        /// </summary>
        SpikeReferenceView Replay(long time)
        {
            var view = new SpikeReferenceView(schema.Tables);
            foreach (var (op, at) in ChainOps())
            {
                if (at >= time)
                    continue;

                switch (op.Kind)
                {
                    case SpikeReferenceOpKind.Union:
                    case SpikeReferenceOpKind.Derived when op.Table is null:
                        view.Union(op.A, op.B);
                        break;
                    case SpikeReferenceOpKind.Insert:
                    case SpikeReferenceOpKind.Derived:
                        view.Add(op.Table!, op.Row!);
                        break;
                    case SpikeReferenceOpKind.Delete:
                        view.Delete(op.Table!, op.Row!);
                        break;
                    default:
                        view.Clear(op.Table!);
                        break;
                }
            }

            view.Close();
            return view;
        }

        /// <summary>
        /// Counts the chain's ops: a change in any of them changes the count.
        /// </summary>
        long ChainOpCount()
        {
            long count = 0;
            for (var c = this; c is not null; c = c.Parent)
                count += c.times.Count;

            return count;
        }

    }

}
