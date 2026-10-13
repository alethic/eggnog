using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Runs a <see cref="ColorProgram"/> on the engine (<see cref="EGraphContext"/>s of a store-backed
    /// <see cref="EGraph"/>) and, in lockstep, on its oracles, checking at every checkpoint: the operational
    /// <see cref="ReferenceRuleContext"/>, a second engine whose children never inherit watermarks, and after the
    /// program a fresh root per live context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every write is flushed as it is made (so the reference's ticks follow the engine's epochs), then the context's
    /// invariants are checked (<see cref="EGraphContext.CheckInvariants"/>), its lookup paths compared
    /// (<see cref="LookupPaths"/>), and its view compared with the oracles; so after every run and rebuild. A view
    /// comparison also checks that no catch-up copy was re-stamped (<c>RestampedCopies == 0</c>) and, in family A, that
    /// every id the context may use has the same canonical id on every side.
    /// </para>
    /// <para>
    /// Deletes and clears are attempted wherever the program asks: where the engine's retraction rule allows them (no
    /// live descendants, and the root or a child of a frozen parent) they apply on every side; elsewhere the engine
    /// must refuse with <see cref="InvalidOperationException"/>, leave the context unfaulted and change nothing.
    /// </para>
    /// <para>
    /// A command whose context or ids are not usable (a shrunk program dropped their creator, or the context is frozen
    /// or disposed) is skipped.
    /// </para>
    /// </remarks>
    sealed class ColorRunner
    {

        /// <summary>
        /// The most iterations a saturation runs.
        /// </summary>
        const int MaxIterations = 60;

        /// <summary>
        /// An id no side uses, for the key of a delete in a context that may use no id.
        /// </summary>
        const uint MissingId = 0x7FFF_FFF0;

        readonly ColorProgram program;
        readonly ColorRunOptions options;
        readonly ColorEngineSide main;
        readonly ColorEngineSide? unseeded;
        readonly ReferenceRuleClock clock = new();
        readonly List<ReferenceRuleContext?> references = new();
        readonly List<ulong> referenceIds = new();
        readonly List<int> parents = new();
        readonly List<int> owners = new();
        readonly List<List<ColorCommand>> hostLogs = new();
        readonly ColorRule[] rules;
        readonly ColorRule[] queries;
        readonly List<uint> pool = new();
        readonly bool byNames;

        /// <summary>
        /// Initializes a runner: builds the sides, mints family A's pool and defines the rules.
        /// </summary>
        public ColorRunner(ColorProgram program, ColorRunOptions options)
        {
            this.program = program;
            this.options = options;
            byNames = program.Family.Minting;
            main = new ColorEngineSide(true);
            unseeded = options.Unseeded ? new ColorEngineSide(false) : null;
            parents.Add(-1);
            hostLogs.Add(new List<ColorCommand>());
            references.Add(options.Reference ? new ReferenceRuleContext(main.Schema, clock) : null);
            if (program.Family.Minting == false)
            {
                for (var i = 0; i < ColorProgram.PoolSize; i++)
                {
                    var id = main.Schema.Root.FreshId().Rep;
                    if (unseeded is not null)
                        Assert.Equal(id, unseeded.Schema.Root.FreshId().Rep);

                    pool.Add(id);
                    SetId(i, 0, id, id, id);
                }
            }

            rules = program.Rules.Select(r => r.Resolve(pool)).ToArray();
            queries = program.Queries.Select(q => q.Resolve(pool)).ToArray();
            foreach (var side in Sides())
            {
                side.Rules = rules.Select(r => r.Build(side.Schema, countFullBindings: options.Unseeded)).ToArray();
                side.Queries = queries.Select(q => q.Build(side.Schema)).ToArray();
            }
        }

        /// <summary>
        /// Gets what the program exercised.
        /// </summary>
        public ColorRunStats Stats { get; } = new();

        /// <summary>
        /// Gets the index of the command running, or that failed.
        /// </summary>
        public int Current { get; private set; } = -1;

        /// <summary>
        /// Runs every command, checks every live context, and runs the copy checks if asked; throws at the first
        /// disagreement (<see cref="Current"/> names the command).
        /// </summary>
        public ColorRunStats Run()
        {
            for (var i = 0; i < program.Commands.Count; i++)
            {
                Current = i;
                Execute(program.Commands[i]);
            }

            Current = program.Commands.Count;
            CheckAll();
            if (options.Copy)
                CopyAll();

            return Stats;
        }

        /// <summary>
        /// Runs one command, or skips it when it is not usable.
        /// </summary>
        void Execute(ColorCommand command)
        {
            var x = command.Context;
            switch (command.Kind)
            {
                case ColorCommandKind.NewId:
                {
                    if (Writable(x) == false)
                        return;

                    var id = main.Contexts[x]!.FreshId().Rep;
                    var other = unseeded?.Contexts[x]!.FreshId().Rep ?? id;
                    Assert.Equal(id, other);
                    SetId(command.Result, x, id, other, id);
                    break;
                }
                case ColorCommandKind.Term:
                {
                    if (Writable(x) == false || IdsUsable(x, command) == false)
                        return;

                    long Mint(ColorEngineSide side)
                    {
                        var key = command.Table == ColorSchema.Num
                            ? [side.Schema.EGraph.BaseValues.Get<Int64Codec, long>(command.Cells[0])]
                            : command.Cells.Select(slot => new Value((uint)side.Ids[slot])).ToArray();
                        return side.Contexts[x]!.AddTerm(side.Schema.Functions[command.Table], key).Rep;
                    }

                    var id = Mint(main);
                    var other = unseeded is null ? id : Mint(unseeded);
                    var reference = references[x] is { } r
                        ? r.AddTerm(command.Table, command.Table == ColorSchema.Num ? [(ulong)command.Cells[0]] : command.Cells.Select(slot => referenceIds[slot]).ToArray())
                        : 0;
                    SetId(command.Result, x, id, other, reference);
                    AfterWrite(x);
                    break;
                }
                case ColorCommandKind.Row:
                {
                    if (Writable(x) == false || IdsUsable(x, command) == false)
                        return;

                    foreach (var side in Sides())
                    {
                        var context = side.Contexts[x]!;
                        context.StageRow(side.Schema.Functions[command.Table], side.Schema.EngineRow(command.Table, Cells(command, slot => (ulong)side.Ids[slot])));
                        context.FlushUpdates();
                    }

                    references[x]?.Insert(command.Table, Cells(command, slot => referenceIds[slot]));
                    AfterWrite(x);
                    break;
                }
                case ColorCommandKind.Union:
                {
                    if (Writable(x) == false || IdsUsable(x, command) == false)
                        return;

                    foreach (var side in Sides())
                    {
                        var context = side.Contexts[x]!;
                        context.StageUnion(new Value((uint)side.Ids[command.Cells[0]]), new Value((uint)side.Ids[command.Cells[1]]));
                        context.FlushUpdates();
                    }

                    references[x]?.Union(referenceIds[command.Cells[0]], referenceIds[command.Cells[1]]);
                    AfterWrite(x);
                    break;
                }
                case ColorCommandKind.Delete:
                case ColorCommandKind.Clear:
                    if (Writable(x) == false)
                        return;

                    Retract(command);
                    break;
                case ColorCommandKind.Run:
                    if (Writable(x) == false)
                        return;

                    Iterate(x, command.Arg);
                    AfterRun(x);
                    break;
                case ColorCommandKind.Saturate:
                    if (Writable(x) == false)
                        return;

                    if (command.Result == 0)
                    {
                        SaturateThroughTheApi(x, command.Arg);
                    }
                    else
                    {
                        for (var i = 0; i < MaxIterations; i++)
                        {
                            var changed = Iterate(x, command.Arg);
                            AfterRun(x);
                            if (changed == false)
                                break;
                        }
                    }

                    break;
                case ColorCommandKind.Child:
                {
                    if (Live(x) == false)
                        return;

                    var slot = command.Result;
                    foreach (var side in Sides())
                    {
                        Grow(side.Contexts, slot + 1, null);
                        side.Contexts[slot] = side.Contexts[x]!.CreateChild(side.ChildOptions);
                    }

                    Grow(references, slot + 1, null);
                    references[slot] = references[x]?.CreateChild();
                    Grow(parents, slot + 1, -1);
                    parents[slot] = x;
                    Grow(hostLogs, slot + 1, null!);
                    hostLogs[slot] = new List<ColorCommand>();
                    Compare(slot);
                    break;
                }
                case ColorCommandKind.Freeze:
                    if (Writable(x) == false || (x != 0 && main.Contexts[parents[x]]!.IsFrozen == false))
                        return;

                    foreach (var side in Sides())
                    {
                        side.Contexts[x]!.Freeze();
                        Assert.True(side.Contexts[x]!.IsFrozen);
                    }

                    Stats.Freezes++;
                    Compare(x);
                    break;
                case ColorCommandKind.Dispose:
                    if (x == 0 || Live(x) == false || LiveChildren(x) > 0)
                        return;

                    foreach (var side in Sides())
                    {
                        side.Contexts[x]!.Dispose();
                        side.Contexts[x] = null;
                    }

                    references[x] = null;
                    Stats.Disposals++;
                    break;
                case ColorCommandKind.Rebuild:
                    if (Writable(x) == false)
                        return;

                    foreach (var side in Sides())
                    {
                        side.Contexts[x]!.Rebuild();
                        side.Contexts[x]!.CheckInvariants();
                    }

                    Compare(x);
                    break;
                default:
                    CheckAll();
                    break;
            }

            Stats.Commands++;
            if (command.Kind is ColorCommandKind.NewId or ColorCommandKind.Term or ColorCommandKind.Row or ColorCommandKind.Union or ColorCommandKind.Delete or ColorCommandKind.Clear)
                hostLogs[x].Add(command);
        }

        /// <summary>
        /// Runs the rules of <paramref name="mask"/> once in context <paramref name="x"/> on every side.
        /// </summary>
        /// <returns>Whether the engine's iteration changed anything (its rules or its rebuild).</returns>
        bool Iterate(int x, int mask)
        {
            var selected = Enumerable.Range(0, rules.Length).Where(i => (mask & (1 << i)) != 0).ToArray();
            var report = main.Contexts[x]!.RunRules(main.Select(mask));
            var changed = report.Changed || report.RebuildChanged;
            Stats.SeededMatches += selected.Sum(i => report.RuleSetReport.NumMatches(rules[i].Name));
            if (unseeded is not null)
            {
                var other = unseeded.Contexts[x]!.RunRules(unseeded.Select(mask));
                Stats.UnseededMatches += selected.Sum(i => other.RuleSetReport.NumMatches(rules[i].Name));
            }

            if (references[x] is { } reference)
            {
                var fired = new int[selected.Length];
                var inheritedBefore = clock.InheritedRuns;
                var derived = reference.RunIteration(selected.Select(i => rules[i]).ToArray(), fired);
                Stats.ReferenceMatches += fired.Sum();
                Stats.InheritedRuns += clock.InheritedRuns - inheritedBefore;
                if (derived && changed == false)
                    throw new Xunit.Sdk.XunitException($"c{x}: the reference derived new facts in an iteration the engine reports unchanged.");
            }

            if (x == 0)
                Stats.RootIterations++;
            else
                Stats.ChildIterations++;

            if (changed)
                Stats.ChangingIterations++;

            return changed;
        }

        /// <summary>
        /// Saturates through <see cref="EGraphContext.Saturate"/>; the reference runs as many iterations as the engine
        /// reports, and must have stopped changing when the engine did.
        /// </summary>
        void SaturateThroughTheApi(int x, int mask)
        {
            var report = main.Contexts[x]!.Saturate(main.Select(mask), MaxIterations);
            unseeded?.Contexts[x]!.Saturate(unseeded.Select(mask), MaxIterations);
            if (references[x] is { } reference)
            {
                var selected = Enumerable.Range(0, rules.Length).Where(i => (mask & (1 << i)) != 0).Select(i => rules[i]).ToArray();
                var last = false;
                for (var i = 0; i < report.Iterations; i++)
                    last = reference.RunIteration(selected);

                if (report.HitLimit == false && last)
                    throw new Xunit.Sdk.XunitException($"c{x}: the engine saturated after {report.Iterations} iterations; the reference still derived new facts in the last.");
            }

            if (x == 0)
                Stats.RootIterations += report.Iterations;
            else
                Stats.ChildIterations += report.Iterations;

            AfterRun(x);
        }

        /// <summary>
        /// Deletes a row or clears a table where the engine's retraction rule allows it (no live descendants, and the
        /// root or a child of a frozen parent), and checks the refusal elsewhere.
        /// </summary>
        void Retract(ColorCommand command)
        {
            var x = command.Context;
            var allowed = LiveChildren(x) == 0 && (x == 0 || main.Contexts[parents[x]]!.IsFrozen);
            var table = command.Table;
            Action<EGraphContext, ColorEngineSide> perform;
            Action<ReferenceRuleContext> mirror;
            if (command.Kind == ColorCommandKind.Clear)
            {
                perform = (context, side) => context.ClearTable(side.Schema.Functions[table]);
                mirror = reference => reference.Clear(table);
            }
            else
            {
                var keys = SelectKey(x, table, command.Arg);
                perform = (context, side) =>
                {
                    var key = side == main ? keys.Main : keys.Unseeded;
                    var id = side.Schema.Tables[table].Id;
                    context.WithExecutionState(state =>
                    {
                        state.StageRemove(id, key);
                        return 0;
                    });
                };
                mirror = reference => reference.Delete(table, keys.Reference);
            }

            if (allowed == false)
            {
                foreach (var side in Sides())
                {
                    var context = side.Contexts[x]!;
                    Assert.Throws<InvalidOperationException>(() => perform(context, side));
                    Assert.False(context.IsFaulted);
                    context.FlushUpdates();
                }

                Stats.Refusals++;
                AfterWrite(x);
                return;
            }

            foreach (var side in Sides())
            {
                var context = side.Contexts[x]!;
                perform(context, side);
                context.FlushUpdates();
            }

            if (references[x] is { } r)
                mirror(r);

            if (x == 0)
                Stats.RootRetractions++;
            else
                Stats.ChildRetractions++;

            AfterWrite(x);
        }

        /// <summary>
        /// Chooses the key a delete removes on each side: the row at <paramref name="selector"/> (modulo the count)
        /// among the context's rows of the table in rendered order, found on the other sides by its rendering; or, for
        /// an empty table, a key of ids the context may use, which no row holds.
        /// </summary>
        ColorKeys SelectKey(int x, int table, int selector)
        {
            var shape = main.Schema.Tables[table];
            var keyCount = shape.KeyCount;
            var lines = Lines(main, x, table);
            if (lines.Count > 0)
            {
                var (line, row) = lines[selector % lines.Count];
                var mainKey = row.Take(keyCount).Select(c => (uint)c).ToArray();
                var otherKey = mainKey;
                if (unseeded is not null)
                    otherKey = Find(Lines(unseeded, x, table), line, "the unseeded engine").Take(keyCount).Select(c => (uint)c).ToArray();

                var found = Array.Empty<ulong>();
                if (references[x] is { } r)
                    found = Find(ReferenceLines(r, table), line, "the reference")[..keyCount];

                return new ColorKeys(mainKey, otherKey, found);
            }

            main.Contexts[x]!.EnsureClean();
            unseeded?.Contexts[x]!.EnsureClean();
            var visible = Enumerable.Range(0, owners.Count).Where(i => main.Ids[i] >= 0 && Usable(x, i)).ToList();
            if (visible.Count == 0 && shape.IsId.Take(keyCount).Any(isId => isId))
            {
                // No id to name: a key no side holds.
                var none = Enumerable.Repeat(MissingId, keyCount).ToArray();
                return new ColorKeys(none, none, none.Select(c => (ulong)c).ToArray());
            }

            var slots = new int[keyCount];
            var values = new ulong[keyCount];
            for (var c = 0; c < keyCount; c++)
            {
                if (shape.IsId[c])
                    slots[c] = visible[(selector + c) % visible.Count];
                else
                    values[c] = (ulong)((selector + c) % 10);
            }

            uint[] EngineKey(ColorEngineSide side) => Enumerable.Range(0, keyCount).Select(c => shape.IsId[c] ? side.Contexts[x]!.Find(new Value((uint)side.Ids[slots[c]])).Rep : (uint)values[c]).ToArray();
            var referenceView = references[x]?.View();
            ulong[] referenceKey = referenceView is null ? [] : Enumerable.Range(0, keyCount).Select(c => shape.IsId[c] ? referenceView.Find(referenceIds[slots[c]]) : values[c]).ToArray();
            return new ColorKeys(EngineKey(main), unseeded is null ? [] : EngineKey(unseeded), referenceKey);
        }

        /// <summary>
        /// Checks a context after a write: invariants and lookup paths on every engine, then the views.
        /// </summary>
        void AfterWrite(int x)
        {
            foreach (var side in Sides())
            {
                var context = side.Contexts[x]!;
                context.CheckInvariants();
                LookupPaths.Check(context.Context);
            }

            Compare(x);
        }

        /// <summary>
        /// Checks a context after a run, as after a write.
        /// </summary>
        void AfterRun(int x) => AfterWrite(x);

        /// <summary>
        /// Compares every live context, and checks its invariants.
        /// </summary>
        void CheckAll()
        {
            for (var x = 0; x < main.Contexts.Count; x++)
            {
                if (Live(x) == false)
                    continue;

                Compare(x);
                foreach (var side in Sides())
                    side.Contexts[x]!.CheckInvariants();
            }
        }

        /// <summary>
        /// Compares context <paramref name="x"/>'s view on every side, its canonical ids in family A, and checks that
        /// no copy was re-stamped.
        /// </summary>
        void Compare(int x)
        {
            var context = main.Contexts[x]!;
            context.EnsureClean();
            var view = ColorViews.Engine(main.Schema, context, byNames);
            Stats.RowsCompared += view.Count;
            Assert.Equal(0, context.Context.Tables?.Statistics.RestampedCopies ?? 0);
            if (references[x] is { } reference)
            {
                var referenceView = reference.View();
                ColorViews.AssertSame(ColorViews.Reference(main.Schema, referenceView, byNames), view, $"c{x} against the reference");
                if (byNames == false)
                {
                    for (var slot = 0; slot < owners.Count; slot++)
                    {
                        if (main.Ids[slot] < 0 || Usable(x, slot) == false)
                            continue;

                        var expected = referenceView.Find(referenceIds[slot]);
                        var actual = context.Find(new Value((uint)main.Ids[slot])).Rep;
                        if (expected != actual)
                            throw new Xunit.Sdk.XunitException($"c{x}: id #{slot} ({main.Ids[slot]}) is canonically {actual} in the engine and {expected} in the reference.");
                    }
                }
            }

            if (unseeded is not null)
            {
                var other = unseeded.Contexts[x]!;
                other.EnsureClean();
                ColorViews.AssertSame(view, ColorViews.Engine(unseeded.Schema, other, byNames), $"c{x}, unseeded against seeded");
                Assert.Equal(0, other.Context.Tables?.Statistics.RestampedCopies ?? 0);
            }
        }

        /// <summary>
        /// After the program: saturates each live context (a frozen one through a fresh child) and compares it with a
        /// fresh root that replays the chain's host writes and saturates the same rules; then runs each conjunctive
        /// query in a child of both and compares their views. Monotone family only: a fresh root is no oracle for a
        /// program with deletes or non-monotone bodies, where facts derived in an ancestor persist.
        /// </summary>
        void CopyAll()
        {
            for (var x = 0; x < main.Contexts.Count; x++)
            {
                if (Live(x) == false)
                    continue;

                var target = main.Contexts[x]!;
                EGraphContext? temporary = null;
                if (target.IsFrozen)
                {
                    temporary = target.CreateChild();
                    target = temporary;
                }

                var saturated = target.Saturate(main.Rules, MaxIterations * 2);
                Assert.False(saturated.HitLimit, $"c{x} did not saturate");
                target.CheckInvariants();

                var copy = Replay(x);
                var copySaturated = copy.Root.Saturate(copy.Rules, MaxIterations * 2);
                Assert.False(copySaturated.HitLimit, $"the copy of c{x} did not saturate");
                ColorViews.AssertSame(ColorViews.Engine(copy.Schema, copy.Root, byNames), ColorViews.Engine(main.Schema, target, byNames), $"c{x} against a fresh root replaying its chain");

                for (var q = 0; q < queries.Length; q++)
                {
                    using var asked = target.CreateChild();
                    asked.RunRules(main.Queries[q]);
                    using var expected = copy.Root.CreateChild();
                    expected.RunRules(copy.Queries[q]);
                    ColorViews.AssertSame(ColorViews.Engine(copy.Schema, expected, byNames), ColorViews.Engine(main.Schema, asked, byNames), $"query {queries[q].Describe()} in c{x} against the copy");
                    Stats.Queries++;
                }

                temporary?.Dispose();
                Stats.Copies++;
            }
        }

        /// <summary>
        /// Builds a fresh root that replays the host writes of context <paramref name="x"/>'s chain, root first: family
        /// A with the same explicit ids, family B with terms re-added and ids mapped.
        /// </summary>
        (ColorSchema Schema, EGraphContext Root, EGraphRuleId[] Rules, EGraphRuleId[] Queries) Replay(int x)
        {
            var schema = new ColorSchema();
            var root = schema.Root;
            var ids = new Dictionary<int, uint>();
            if (byNames == false)
            {
                for (var i = 0; i < pool.Count; i++)
                    Assert.Equal(pool[i], root.FreshId().Rep);

                // Explicit ids: make the counter pass every id the program used.
                var max = main.Ids.Where(id => id >= 0).DefaultIfEmpty(0).Max();
                while (root.FreshId().Rep < max)
                {
                }

                for (var slot = 0; slot < main.Ids.Count; slot++)
                    if (main.Ids[slot] >= 0)
                        ids[slot] = (uint)main.Ids[slot];
            }

            var chain = new List<int>();
            for (var c = x; c >= 0; c = parents[c])
                chain.Insert(0, c);

            foreach (var c in chain)
            {
                foreach (var command in hostLogs[c])
                {
                    switch (command.Kind)
                    {
                        case ColorCommandKind.NewId:
                            break;
                        case ColorCommandKind.Term:
                        {
                            var key = command.Table == ColorSchema.Num
                                ? [schema.EGraph.BaseValues.Get<Int64Codec, long>(command.Cells[0])]
                                : command.Cells.Select(slot => new Value(ids[slot])).ToArray();
                            ids[command.Result] = root.AddTerm(schema.Functions[command.Table], key).Rep;
                            break;
                        }
                        case ColorCommandKind.Row:
                            root.StageRow(schema.Functions[command.Table], schema.EngineRow(command.Table, Cells(command, slot => ids[slot])));
                            root.FlushUpdates();
                            break;
                        case ColorCommandKind.Union:
                            root.StageUnion(new Value(ids[command.Cells[0]]), new Value(ids[command.Cells[1]]));
                            root.FlushUpdates();
                            break;
                        default:
                            throw new InvalidOperationException("A copy replays monotone programs only.");
                    }
                }
            }

            var built = rules.Select(r => r.Build(schema)).ToArray();
            var asked = queries.Select(q => q.Build(schema)).ToArray();
            return (schema, root, built, asked);
        }

        /// <summary>
        /// Gets the logical cells of a row command, an id column's slot mapped by <paramref name="id"/>.
        /// </summary>
        static ulong[] Cells(ColorCommand command, Func<int, ulong> id)
        {
            var isId = ColorSchema.IdColumns[command.Table];
            var cells = new ulong[command.Cells.Length];
            for (var c = 0; c < cells.Length; c++)
                cells[c] = isId[c] ? id(command.Cells[c]) : (ulong)command.Cells[c];

            return cells;
        }

        /// <summary>
        /// Gets the rendered rows of <paramref name="table"/> that context <paramref name="x"/> sees on an engine side,
        /// sorted by rendering.
        /// </summary>
        List<(string Line, ulong[] Row)> Lines(ColorEngineSide side, int x, int table)
        {
            var context = side.Contexts[x]!;
            context.EnsureClean();
            var rows = new List<ulong[]>[side.Schema.Tables.Count];
            for (var t = 0; t < rows.Length; t++)
                rows[t] = side.Schema.Rows(context, t);

            var names = byNames ? TermNamer.Name(side.Schema, t => rows[t]) : null;
            return rows[table].Select(row => (ColorViews.Line(side.Schema.Tables[table], row, names), row)).OrderBy(p => p.Item1, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Gets the rendered rows of <paramref name="table"/> in a reference context.
        /// </summary>
        List<(string Line, ulong[] Row)> ReferenceLines(ReferenceRuleContext reference, int table)
        {
            var view = reference.View();
            var names = byNames ? TermNamer.Name(main.Schema, t => view.Rows(main.Schema.Tables[t])) : null;
            return view.Rows(main.Schema.Tables[table]).Select(row => (ColorViews.Line(main.Schema.Tables[table], row, names), row)).ToList();
        }

        /// <summary>
        /// Finds the row rendered <paramref name="line"/>.
        /// </summary>
        static ulong[] Find(List<(string Line, ulong[] Row)> lines, string line, string where)
        {
            foreach (var (text, row) in lines)
                if (text == line)
                    return row;

            throw new Xunit.Sdk.XunitException($"{where} has no row {line} to delete.");
        }

        /// <summary>
        /// Records id slot <paramref name="slot"/>, owned by context <paramref name="owner"/>.
        /// </summary>
        void SetId(int slot, int owner, long id, long other, ulong reference)
        {
            Grow(owners, slot + 1, -1);
            Grow(main.Ids, slot + 1, -1);
            Grow(referenceIds, slot + 1, 0UL);
            owners[slot] = owner;
            main.Ids[slot] = id;
            referenceIds[slot] = reference;
            if (unseeded is not null)
            {
                Grow(unseeded.Ids, slot + 1, -1);
                unseeded.Ids[slot] = other;
            }
        }

        /// <summary>
        /// Gets whether every id slot of a term, row or union command may be used in context <paramref name="x"/>.
        /// </summary>
        bool IdsUsable(int x, ColorCommand command)
        {
            for (var c = 0; c < command.Cells.Length; c++)
            {
                var isId = command.Kind == ColorCommandKind.Union || (command.Kind == ColorCommandKind.Term ? command.Table != ColorSchema.Num : ColorSchema.IdColumns[command.Table][c]);
                if (isId && (command.Cells[c] >= main.Ids.Count || main.Ids[command.Cells[c]] < 0 || Usable(x, command.Cells[c]) == false))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Gets whether id slot <paramref name="slot"/> may be used in context <paramref name="x"/>: its owner lives
        /// and is <paramref name="x"/> or an ancestor.
        /// </summary>
        bool Usable(int x, int slot)
        {
            var owner = owners[slot];
            if (Live(owner) == false)
                return false;

            for (var c = x; c >= 0; c = parents[c])
                if (c == owner)
                    return true;

            return false;
        }

        /// <summary>
        /// Gets whether context slot <paramref name="x"/> holds a live context.
        /// </summary>
        bool Live(int x) => x >= 0 && x < main.Contexts.Count && main.Contexts[x] is { IsDisposed: false };

        /// <summary>
        /// Gets whether context slot <paramref name="x"/> holds a live context that may be written.
        /// </summary>
        bool Writable(int x) => Live(x) && main.Contexts[x]!.IsFrozen == false;

        /// <summary>
        /// Counts the live children of context <paramref name="x"/>.
        /// </summary>
        int LiveChildren(int x) => Enumerable.Range(0, parents.Count).Count(c => parents[c] == x && Live(c));

        /// <summary>
        /// Enumerates the engine sides.
        /// </summary>
        IEnumerable<ColorEngineSide> Sides()
        {
            yield return main;
            if (unseeded is not null)
                yield return unseeded;
        }

        /// <summary>
        /// Grows <paramref name="list"/> to <paramref name="count"/> items with <paramref name="fill"/>.
        /// </summary>
        static void Grow<T>(List<T> list, int count, T fill)
        {
            while (list.Count < count)
                list.Add(fill);
        }

    }

}
