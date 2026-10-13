using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// The differential's oracle: evaluates a <see cref="RandomProgram"/> naively over decoded values, with its own
    /// union-find and congruence closure, independently of the rule layer and the engine. Each iteration is
    /// Jacobi-style: every rule matches the database as it was when the iteration began, their writes merge at its end,
    /// and a rebuild restores congruence. Schedules, limits and run reports follow the rule layer's semantics.
    /// </summary>
    /// <remarks>
    /// Values are <c>long</c>, <c>string</c> and <c>ValueSet&lt;int&gt;</c>; a class is a <c>long</c> id. Rows of a
    /// function or constructor map a key to an output; a relation is a set of keys. Every cell is canonical between
    /// commands. Observations are rendered with <see cref="TermNamer"/> names, as the Eggnog side renders its own.
    /// </remarks>
    internal sealed class ReferenceInterpreter
    {

        readonly RandomProgram program;
        readonly List<long> parent = new();
        readonly Dictionary<DiffTable, HashSet<RowKey>> relations = new();
        readonly Dictionary<DiffTable, Dictionary<RowKey, object>> functions = new();
        readonly List<long> handles = new();
        readonly List<(DiffRule Rule, RefBody Body)> rules = new();
        readonly List<(DiffRule Query, RefBody Body)> queries = new();

        // The state of one iteration: the indexes of the database as it began, and its writes.
        readonly Dictionary<(DiffTable, int), Dictionary<RowKey, List<object[]>>> indexes = new();
        readonly Dictionary<(DiffTable, RowKey), long> made = new();
        readonly List<(DiffTable Table, object[] Keys)> inserts = new();
        readonly List<(DiffTable Table, object[] Keys, object Value)> writes = new();
        readonly List<(DiffTable Table, object[] Keys, long Output)> terms = new();
        readonly List<(long A, long B)> unions = new();

        // The state of one run.
        int iterations;
        int iterationLimit;
        bool topChanged;
        bool lastChanged;

        /// <summary>
        /// Initializes an empty database for a program.
        /// </summary>
        /// <param name="program">The program.</param>
        public ReferenceInterpreter(RandomProgram program)
        {
            this.program = program;
            foreach (var table in program.Tables)
            {
                if (table.Kind == TableKind.Relation)
                    relations.Add(table, new HashSet<RowKey>());
                else
                    functions.Add(table, new Dictionary<RowKey, object>());
            }

            foreach (var query in program.Queries)
                queries.Add((query, new RefBody(query)));
        }

        /// <summary>
        /// Runs a command and renders what it observed, as <see cref="EggnogDriver.Execute(DiffCommand)"/> does.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <returns>The observation.</returns>
        public string Execute(DiffCommand command)
        {
            switch (command.Kind)
            {
                case DiffCommandKind.Assume:
                    foreach (var fact in command.Facts)
                        Assume(fact);

                    Rebuild();
                    return "assume\n" + Snapshot();
                case DiffCommandKind.Make:
                    handles.Add((long)Ground(command.Term!));
                    Rebuild();
                    return "make\n" + Snapshot();
                case DiffCommandKind.Union:
                {
                    var distinct = Union(handles[command.First], handles[command.Second]);
                    Rebuild();
                    return "union " + (distinct ? "distinct" : "equal") + "\n" + Snapshot();
                }
                case DiffCommandKind.DefineRule:
                    rules.Add((command.Rule!, new RefBody(command.Rule!)));
                    return "define " + command.Rule!.Name;
                case DiffCommandKind.DefineQuery:
                    queries.Add((command.Rule!, new RefBody(command.Rule!)));
                    return "define query " + command.Rule!.Name;
                case DiffCommandKind.Run:
                    return Run(command.Schedule!, command.First);
                default:
                    return Query(command.First, command.Mode);
            }
        }

        /// <summary>
        /// Gets the number of rule matches so far, over every iteration.
        /// </summary>
        public long Matches { get; private set; }

        /// <summary>
        /// Gets the number of rows of every table: the differential stops a program that grows too large.
        /// </summary>
        public int TotalRows => relations.Values.Sum(r => r.Count) + functions.Values.Sum(f => f.Count);

        /// <summary>
        /// Renders every table's rows, named, sorted, in declaration order.
        /// </summary>
        /// <returns>The rendering.</returns>
        public string Snapshot()
        {
            var names = Names();
            var builder = new StringBuilder();
            foreach (var table in program.Tables)
            {
                var rows = table.Kind == TableKind.Relation
                    ? relations[table].Select(k => DiffValues.Row(table, k.Cells, null, Lookup(names)))
                    : functions[table].Select(r => DiffValues.Row(table, r.Key.Cells, r.Value, Lookup(names)));
                builder.Append(table.Name).Append(": ").AppendJoin(" | ", rows.OrderBy(r => r, StringComparer.Ordinal)).Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Names every class by its least ground term.
        /// </summary>
        Dictionary<long, string> Names()
        {
            var rows = new List<(DiffTable, object[], long)>();
            foreach (var (table, rowsOf) in functions)
                if (table.Kind == TableKind.Constructor)
                    foreach (var (key, output) in rowsOf)
                        rows.Add((table, key.Cells, (long)output));

            return TermNamer.Name(rows);
        }

        /// <summary>
        /// A name lookup over a naming.
        /// </summary>
        static Func<long, string> Lookup(Dictionary<long, string> names) => id => names.TryGetValue(id, out var name) ? name : "#" + id.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Runs a schedule with an iteration limit and renders its report and the database.
        /// </summary>
        string Run(DiffSchedule schedule, int limit)
        {
            iterations = 0;
            iterationLimit = limit;
            topChanged = false;
            lastChanged = false;
            var reason = "Completed";
            try
            {
                Execute(schedule);
            }
            catch (ReferenceStopException)
            {
                reason = "IterationLimit";
            }

            var saturated = reason == "Completed" && lastChanged == false;
            return $"run iterations={iterations} stop={reason} saturated={saturated} changed={topChanged}\n" + Snapshot();
        }

        /// <summary>
        /// Executes a schedule node; returns whether it changed the database.
        /// </summary>
        bool Execute(DiffSchedule schedule)
        {
            switch (schedule.Kind)
            {
                case DiffScheduleKind.Run:
                    return Iterate(schedule.Rulesets);
                case DiffScheduleKind.Saturate:
                {
                    var changed = false;
                    while (Sequence(schedule.Steps))
                        changed = true;

                    return changed;
                }
                case DiffScheduleKind.Repeat:
                {
                    var changed = false;
                    for (var pass = 0; pass < schedule.Times; pass++)
                    {
                        if (Sequence(schedule.Steps) == false)
                            break;

                        changed = true;
                    }

                    return changed;
                }
                case DiffScheduleKind.Sequence:
                    return Sequence(schedule.Steps);
                default:
                    foreach (var fact in schedule.Condition)
                        if (Check(fact) == false)
                            return Execute(schedule.Steps[0]);

                    return false;
            }
        }

        /// <summary>
        /// Executes steps in order; whether any changed the database.
        /// </summary>
        bool Sequence(DiffSchedule[] steps)
        {
            var changed = false;
            foreach (var step in steps)
                changed |= Execute(step);

            return changed;
        }

        /// <summary>
        /// One iteration of the rulesets' rules: the limit is checked first, even with no rules.
        /// </summary>
        bool Iterate(int[] rulesets)
        {
            if (iterations >= iterationLimit)
                throw new ReferenceStopException();

            var active = rules.Where(r => rulesets.Contains(r.Rule.Ruleset)).ToList();
            if (active.Count == 0)
                return false;

            indexes.Clear();
            made.Clear();
            inserts.Clear();
            writes.Clear();
            terms.Clear();
            unions.Clear();

            // Every rule matches the database as the iteration began; then every head runs.
            var matches = active.Select(r => (r.Rule, r.Body, Matches: Match(r.Body))).ToList();
            foreach (var (rule, body, found) in matches)
            {
                Matches += found.Count;
                foreach (var binding in found)
                    Head(rule, body, binding);
            }

            var changed = Merge();
            iterations++;
            lastChanged = changed;
            topChanged |= changed;
            return changed;
        }

        /// <summary>
        /// Runs a rule's head for one match, into the iteration's writes.
        /// </summary>
        void Head(DiffRule rule, RefBody body, object?[] binding)
        {
            var environment = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i < body.Names.Count; i++)
                if (binding[i] is { } value)
                    environment[body.Names[i]] = value;

            for (var i = 0; i < rule.Head.Count; i++)
            {
                var item = rule.Head[i];
                switch (item.Kind)
                {
                    case DiffHeadKind.Make:
                        environment[item.Name!] = Evaluate(item.Left!, environment);
                        break;
                    case DiffHeadKind.Rewrite:
                        Write(DiffFact.Equal(DiffTerm.Var(body.RewriteVariables[i], DiffType.Node), item.Right!), environment);
                        break;
                    default:
                        Write(item.Fact!, environment);
                        break;
                }
            }
        }

        /// <summary>
        /// Runs a head fact: a relation row, a function write, a constructor write, or a union.
        /// </summary>
        void Write(DiffFact fact, Dictionary<string, object> environment)
        {
            if (fact.Kind == DiffFactKind.Relation)
            {
                inserts.Add((fact.Table!, fact.Arguments.Select(a => Evaluate(a, environment)).ToArray()));
                return;
            }

            var (left, right) = (fact.Left!, fact.Right!);
            if (right.IsFunction)
                (left, right) = (right, left);

            if (left.IsFunction)
            {
                writes.Add((left.Table!, left.Arguments.Select(a => Evaluate(a, environment)).ToArray(), Evaluate(right, environment)));
                return;
            }

            // A constructor write C(k) = x is a get-or-add of C(k) unioned with x: one class either way.
            unions.Add(((long)Evaluate(left, environment), (long)Evaluate(right, environment)));
        }

        /// <summary>
        /// Evaluates a head term: a variable, a constant, a get-or-add, or a computation.
        /// </summary>
        object Evaluate(DiffTerm term, Dictionary<string, object> environment)
        {
            switch (term.Kind)
            {
                case DiffTermKind.Var:
                    return environment[term.Name!];
                case DiffTermKind.Const:
                    return Constant(term);
                case DiffTermKind.App:
                {
                    var keys = term.Arguments.Select(a => Evaluate(a, environment)).ToArray();
                    var key = new RowKey(Canonical(term.Table!, keys));
                    if (functions[term.Table!].TryGetValue(key, out var existing))
                        return Find((long)existing);

                    if (made.TryGetValue((term.Table!, key), out var id) == false)
                    {
                        id = NewClass();
                        made.Add((term.Table!, key), id);
                        terms.Add((term.Table!, key.Cells, id));
                    }

                    return id;
                }
                default:
                    return term.Primitive!.Evaluate(term.Arguments.Select(a => Evaluate(a, environment)).ToArray());
            }
        }

        /// <summary>
        /// Merges an iteration's writes, then rebuilds; returns whether the database changed.
        /// </summary>
        bool Merge()
        {
            var changed = false;
            foreach (var (table, keys) in inserts)
                changed |= relations[table].Add(new RowKey(keys));

            foreach (var (table, keys, output) in terms)
                changed |= AddTerm(table, keys, output);

            foreach (var (table, keys, value) in writes)
                changed |= WriteValue(table, keys, value);

            foreach (var (a, b) in unions)
                changed |= Union(a, b);

            changed |= Rebuild();
            return changed;
        }

        /// <summary>
        /// Adds a constructor row, or unions its output with the existing row's.
        /// </summary>
        bool AddTerm(DiffTable table, object[] keys, long output)
        {
            var key = new RowKey(Canonical(table, keys));
            if (functions[table].TryGetValue(key, out var existing))
                return Union((long)existing, output);

            functions[table].Add(key, Find(output));
            return true;
        }

        /// <summary>
        /// Writes a function value, merged by the function's lattice.
        /// </summary>
        bool WriteValue(DiffTable table, object[] keys, object value)
        {
            var key = new RowKey(Canonical(table, keys));
            var rows = functions[table];
            if (rows.TryGetValue(key, out var existing) == false)
            {
                rows.Add(key, value);
                return true;
            }

            var merged = Merge(table, existing, value);
            if (Equals(merged, existing))
                return false;

            rows[key] = merged;
            return true;
        }

        /// <summary>
        /// A function's lattice merge; a function without one keeps the old value, which only differs on a conflict the
        /// program never makes.
        /// </summary>
        static object Merge(DiffTable table, object old, object value) => table.Lattice switch
        {
            DiffLattice.Max => Math.Max((long)old, (long)value),
            DiffLattice.Intersect => ((ValueSet<int>)old).Intersect((ValueSet<int>)value),
            _ => Equals(old, value) ? old : throw new InvalidOperationException($"Reference: a conflicting write to '{table.Name}'."),
        };

        /// <summary>
        /// Restores congruence: canonical cells everywhere, constructor rows with one key unioned, function rows with
        /// one key merged; repeated until no union happens. Returns whether a union happened.
        /// </summary>
        bool Rebuild()
        {
            var any = false;
            while (true)
            {
                var unioned = false;
                foreach (var table in program.Tables)
                {
                    if (table.Kind == TableKind.Relation)
                        continue;

                    var rows = functions[table];
                    var rebuilt = new Dictionary<RowKey, object>(rows.Count);
                    foreach (var (key, value) in rows)
                    {
                        var canonical = new RowKey(Canonical(table, key.Cells));
                        var output = table.Kind == TableKind.Constructor ? Find((long)value) : value;
                        if (rebuilt.TryGetValue(canonical, out var existing))
                        {
                            if (table.Kind == TableKind.Constructor)
                                unioned |= Union((long)existing, (long)output);
                            else
                                rebuilt[canonical] = Merge(table, existing, output);
                        }
                        else
                        {
                            rebuilt.Add(canonical, output);
                        }
                    }

                    functions[table] = rebuilt;
                }

                if (unioned == false)
                    return any;

                any = true;
            }
        }

        /// <summary>
        /// A copy of keys with every class canonical.
        /// </summary>
        object[] Canonical(DiffTable table, object[] keys)
        {
            var canonical = new object[keys.Length];
            for (var i = 0; i < keys.Length; i++)
                canonical[i] = table.Keys[i] == DiffType.Node ? Find((long)keys[i]) : keys[i];

            return canonical;
        }

        /// <summary>
        /// Finds all matches of a body: the join of its atoms in order, then its steps; each match binds every
        /// variable.
        /// </summary>
        List<object?[]> Match(RefBody body)
        {
            var matches = new List<object?[]>();
            var distinct = new HashSet<RowKey>();
            var binding = new object?[body.Names.Count];
            Join(body, 0, binding, matches, distinct);
            return matches;
        }

        /// <summary>
        /// Joins atom <paramref name="index"/> and the rest, then runs the steps.
        /// </summary>
        void Join(RefBody body, int index, object?[] binding, List<object?[]> matches, HashSet<RowKey> distinct)
        {
            if (index == body.Atoms.Count)
            {
                var copy = (object?[])binding.Clone();
                if (Steps(body, copy) && distinct.Add(new RowKey(copy.Select(c => c!).ToArray())))
                    matches.Add(copy);

                return;
            }

            var atom = body.Atoms[index];
            var mask = 0;
            var probe = new List<object>();
            for (var i = 0; i < atom.Slots.Length; i++)
            {
                if (Value(atom.Slots[i], binding) is { } value)
                {
                    mask |= 1 << i;
                    probe.Add(value);
                }
            }

            if (IndexOf(atom.Table, mask).TryGetValue(new RowKey(probe.ToArray()), out var rows) == false)
                return;

            foreach (var row in rows)
            {
                var bound = new List<int>();
                var fits = true;
                for (var i = 0; i < atom.Slots.Length && fits; i++)
                {
                    var slot = atom.Slots[i];
                    if (slot.IsVariable && binding[slot.Variable] is null)
                    {
                        binding[slot.Variable] = row[i];
                        bound.Add(slot.Variable);
                    }
                    else if (Equals(Value(slot, binding), row[i]) == false)
                    {
                        fits = false;
                    }
                }

                if (fits)
                    Join(body, index + 1, binding, matches, distinct);

                foreach (var variable in bound)
                    binding[variable] = null;
            }
        }

        /// <summary>
        /// Runs a body's steps over a joined binding, each once its inputs are bound; whether they all hold.
        /// </summary>
        bool Steps(RefBody body, object?[] binding)
        {
            var pending = new List<RefStep>(body.Steps);
            while (pending.Count > 0)
            {
                var progressed = false;
                for (var i = 0; i < pending.Count; i++)
                {
                    var step = pending[i];
                    bool? holds = step.Kind switch
                    {
                        RefStepKind.Let => Let(step, binding),
                        RefStepKind.Filter => Filter(step, binding),
                        RefStepKind.Equal => Equal(step.Arguments[0], step.Arguments[1], binding),
                        _ => Value(step.Arguments[0], binding) is { } x && Value(step.Arguments[1], binding) is { } y ? Equals(x, y) == false : null,
                    };

                    if (holds is null)
                        continue;

                    if (holds == false)
                        return false;

                    pending.RemoveAt(i--);
                    progressed = true;
                }

                if (progressed == false)
                    throw new InvalidOperationException("Reference: a step's inputs are never bound; the program is not range-restricted.");
            }

            return true;
        }

        /// <summary>
        /// A let: <c>null</c> while an argument is unbound; binds an unbound target, checks a bound one.
        /// </summary>
        bool? Let(RefStep step, object?[] binding)
        {
            var arguments = new object[step.Arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                if (Value(step.Arguments[i], binding) is not { } value)
                    return null;

                arguments[i] = value;
            }

            var result = step.Primitive!.Evaluate(arguments);
            if (Value(step.Target, binding) is { } existing)
                return Equals(existing, result);

            binding[step.Target.Variable] = result;
            return true;
        }

        /// <summary>
        /// A filter: <c>null</c> while an argument is unbound.
        /// </summary>
        bool? Filter(RefStep step, object?[] binding)
        {
            var arguments = new object[step.Arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                if (Value(step.Arguments[i], binding) is not { } value)
                    return null;

                arguments[i] = value;
            }

            return (bool)step.Primitive!.Evaluate(arguments);
        }

        /// <summary>
        /// An equality: binds one side from the other, or checks both; <c>null</c> while neither is bound.
        /// </summary>
        bool? Equal(RefSlot left, RefSlot right, object?[] binding)
        {
            var x = Value(left, binding);
            var y = Value(right, binding);
            if (x is not null && y is not null)
                return Equals(x, y);

            if (x is not null)
            {
                binding[right.Variable] = x;
                return true;
            }

            if (y is not null)
            {
                binding[left.Variable] = y;
                return true;
            }

            return null;
        }

        /// <summary>
        /// A slot's value: a bound variable's, or a constant's (a class constant's class, canonical now); <c>null</c>
        /// for an unbound variable.
        /// </summary>
        object? Value(RefSlot slot, object?[] binding) => slot.IsVariable ? binding[slot.Variable] : Constant(slot.Constant!);

        /// <summary>
        /// A constant's value; a class constant's class, canonical now.
        /// </summary>
        object Constant(DiffTerm constant) => constant.Type == DiffType.Node ? Find(handles[(int)constant.Value!]) : constant.Value!;

        /// <summary>
        /// The rows of a table by the values of the columns in <paramref name="mask"/>, indexed once per iteration.
        /// </summary>
        Dictionary<RowKey, List<object[]>> IndexOf(DiffTable table, int mask)
        {
            if (indexes.TryGetValue((table, mask), out var index))
                return index;

            index = new Dictionary<RowKey, List<object[]>>();
            IEnumerable<object[]> rows = table.Kind == TableKind.Relation
                ? relations[table].Select(k => k.Cells)
                : functions[table].Select(r => r.Key.Cells.Append(r.Value).ToArray());
            foreach (var row in rows)
            {
                var key = new List<object>();
                for (var i = 0; i < row.Length; i++)
                    if ((mask & (1 << i)) != 0)
                        key.Add(row[i]);

                var probe = new RowKey(key.ToArray());
                if (index.TryGetValue(probe, out var list) == false)
                    index.Add(probe, list = new List<object[]>());

                list.Add(row);
            }

            indexes.Add((table, mask), index);
            return index;
        }

        /// <summary>
        /// Writes a ground host fact: a relation row, a function value, or an equality of classes (a union).
        /// </summary>
        void Assume(DiffFact fact)
        {
            if (fact.Kind == DiffFactKind.Relation)
            {
                relations[fact.Table!].Add(new RowKey(fact.Arguments.Select(Ground).ToArray()));
                return;
            }

            var (left, right) = (fact.Left!, fact.Right!);
            if (right.IsFunction)
                (left, right) = (right, left);

            if (left.IsFunction)
            {
                WriteValue(left.Table!, left.Arguments.Select(Ground).ToArray(), Ground(right));
                return;
            }

            Union((long)Ground(left), (long)Ground(right));
        }

        /// <summary>
        /// The value of a ground term, creating constructor terms that do not exist (host writes).
        /// </summary>
        object Ground(DiffTerm term)
        {
            if (term.Kind == DiffTermKind.Const)
                return Constant(term);

            var key = new RowKey(Canonical(term.Table!, term.Arguments.Select(Ground).ToArray()));
            var rows = functions[term.Table!];
            if (rows.TryGetValue(key, out var existing))
                return Find((long)existing);

            var id = NewClass();
            rows.Add(key, id);
            return id;
        }

        /// <summary>
        /// The value of a ground term without creating anything (host checks); <c>null</c> when a term does not exist.
        /// </summary>
        object? Read(DiffTerm term)
        {
            if (term.Kind == DiffTermKind.Const)
                return Constant(term);

            var keys = new object[term.Arguments.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                if (Read(term.Arguments[i]) is not { } value)
                    return null;

                keys[i] = value;
            }

            if (functions[term.Table!].TryGetValue(new RowKey(Canonical(term.Table!, keys)), out var output) == false)
                return null;

            return term.Table!.Kind == TableKind.Constructor ? Find((long)output) : output;
        }

        /// <summary>
        /// Checks a ground fact, as a host check does.
        /// </summary>
        bool Check(DiffFact fact)
        {
            switch (fact.Kind)
            {
                case DiffFactKind.Relation:
                {
                    var keys = new object[fact.Arguments.Length];
                    for (var i = 0; i < keys.Length; i++)
                    {
                        if (Read(fact.Arguments[i]) is not { } value)
                            return false;

                        keys[i] = value;
                    }

                    return relations[fact.Table!].Contains(new RowKey(keys));
                }
                case DiffFactKind.Equal:
                    return Read(fact.Left!) is { } x && Read(fact.Right!) is { } y && Equals(x, y);
                default:
                    return Read(fact.Left!) is not null;
            }
        }

        /// <summary>
        /// Asks a query and renders its answer.
        /// </summary>
        string Query(int index, DiffQueryMode mode)
        {
            var (query, body) = queries[index];
            indexes.Clear();
            var columns = query.Variables.Select(v => body.IndexOf(v.Name)).ToArray();
            var tuples = new HashSet<RowKey>();
            foreach (var match in Match(body))
                tuples.Add(new RowKey(columns.Select(c => match[c]!).ToArray()));

            indexes.Clear();
            switch (mode)
            {
                case DiffQueryMode.Any:
                    return $"query {query.Name} any={tuples.Count > 0}";
                case DiffQueryMode.Count:
                    return $"query {query.Name} count={tuples.Count}";
                default:
                {
                    var names = Lookup(Names());
                    var rendered = tuples.Select(t => string.Join(" ", t.Cells.Select((c, i) => DiffValues.Format(c, query.Variables[i].Type, names))))
                        .OrderBy(t => t, StringComparer.Ordinal);
                    return $"query {query.Name} count={tuples.Count}: " + string.Join(" | ", rendered);
                }
            }
        }

        /// <summary>
        /// Makes a class.
        /// </summary>
        long NewClass()
        {
            var id = (long)parent.Count;
            parent.Add(id);
            return id;
        }

        /// <summary>
        /// Finds a class's representative, compressing the path.
        /// </summary>
        long Find(long id)
        {
            var root = id;
            while (parent[(int)root] != root)
                root = parent[(int)root];

            while (parent[(int)id] != root)
            {
                var next = parent[(int)id];
                parent[(int)id] = root;
                id = next;
            }

            return root;
        }

        /// <summary>
        /// Unions two classes; whether they were distinct.
        /// </summary>
        bool Union(long a, long b)
        {
            var x = Find(a);
            var y = Find(b);
            if (x == y)
                return false;

            if (x < y)
                parent[(int)y] = x;
            else
                parent[(int)x] = y;

            return true;
        }

    }

}
