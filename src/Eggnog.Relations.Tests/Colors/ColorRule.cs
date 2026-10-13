using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A rule of the rule differentials (colored-contexts spec §17 M10.3) over <see cref="ColorSchema"/>, in a form that is
    /// both built into the engine (<see cref="Build"/>, through <see cref="EGraphRuleBuilder"/>) and evaluated naively by
    /// <see cref="ReferenceRuleEvaluator"/>, so the engine is checked against a definition with no plans, timestamps or
    /// layers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A body of <see cref="Atoms"/> and <see cref="Filters"/>, and a head of <see cref="Actions"/>, run in order per match.
    /// Entries are variable indexes (non-negative) or constants encoded as <c>-1 - c</c>. A generated rule's id constants
    /// are indexes into the program's pool of root ids until <see cref="Resolve"/> replaces them with the ids.
    /// </para>
    /// <para>
    /// Every rule is semi-naive. Its watermark may be inherited (§10.2, X41) unless it reads a table in an action
    /// (<see cref="ReadsTables"/>), which the reference decides for itself from the rule rather than from the engine's
    /// <see cref="ActionReadAnalysis"/>.
    /// </para>
    /// </remarks>
    sealed class ColorRule
    {

        /// <summary>
        /// Initializes an empty rule named <paramref name="name"/>.
        /// </summary>
        public ColorRule(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Gets the rule's name, its description in the engine's reports.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets whether each variable holds an id (otherwise an i64); body variables first, then those actions define.
        /// </summary>
        public List<bool> VarIsId { get; } = new();

        /// <summary>
        /// Gets the atoms.
        /// </summary>
        public List<ColorAtom> Atoms { get; } = new();

        /// <summary>
        /// Gets the filters.
        /// </summary>
        public List<ColorFilter> Filters { get; } = new();

        /// <summary>
        /// Gets the actions.
        /// </summary>
        public List<ColorAction> Actions { get; } = new();

        /// <summary>
        /// Gets whether an action reads a table (<see cref="ColorActionKind.Read"/>): the rule's actions are not
        /// query-determined, so its watermark is never inherited (X41).
        /// </summary>
        public bool ReadsTables => Actions.Any(a => a.Kind == ColorActionKind.Read);

        /// <summary>
        /// Gets whether the rule mints ids.
        /// </summary>
        public bool Mints => Actions.Any(a => a.Kind == ColorActionKind.Mint);

        /// <summary>
        /// Encodes the constant <paramref name="value"/> as an entry.
        /// </summary>
        public static long Constant(ulong value) => -1L - (long)value;

        /// <summary>
        /// Decodes a constant entry.
        /// </summary>
        public static ulong Decode(long entry) => (ulong)(-1L - entry);

        /// <summary>
        /// Gets the tables the rule writes: those of its sets and mints.
        /// </summary>
        public IEnumerable<int> WrittenTables() => Actions.Where(a => a.Kind is ColorActionKind.Set or ColorActionKind.Mint).Select(a => a.Table);

        /// <summary>
        /// Gets the rule's id constants in a fixed order (atoms, then actions), as the engine's K lists them in its own
        /// order (X19, §9.8): the reference compares a run's canonical forms of them with those of the run that set a
        /// watermark.
        /// </summary>
        public List<ulong> IdConstants()
        {
            var constants = new List<ulong>();
            foreach (var (_, entry, isId) in Entries())
                if (isId && entry < 0)
                    constants.Add(Decode(entry));

            return constants;
        }

        /// <summary>
        /// Returns a copy whose id constants, indexes into <paramref name="pool"/>, are replaced by the pool's ids.
        /// </summary>
        public ColorRule Resolve(IReadOnlyList<uint> pool)
        {
            ArgumentNullException.ThrowIfNull(pool);
            long Map(long entry, bool isId) => entry < 0 && isId ? Constant(pool[checked((int)Decode(entry))]) : entry;

            var copy = new ColorRule(Name);
            copy.VarIsId.AddRange(VarIsId);
            foreach (var atom in Atoms)
                copy.Atoms.Add(atom with { Entries = atom.Entries.Select((e, c) => Map(e, ColorSchema.IdColumns[atom.Table][c])).ToArray() });

            copy.Filters.AddRange(Filters);
            foreach (var action in Actions)
                copy.Actions.Add(action with { Args = action.Args.Select((e, c) => Map(e, IsIdArgument(action, c))).ToArray() });

            return copy;
        }

        /// <summary>
        /// Builds the rule into <paramref name="schema"/>'s e-graph, semi-naively.
        /// </summary>
        /// <remarks>
        /// With <paramref name="countFullBindings"/>, the body also calls <see cref="ColorSchema.Keep"/> on every body
        /// variable. That changes nothing the rule derives, and it does not read a table, but every variable is then used,
        /// so the executor enumerates distinct full bindings and the reported match counts are those: otherwise a full run
        /// counts the distinct bindings of the variables the actions use, while a semi-naive run sums that over its
        /// variants, which can count one projected binding more than once.
        /// </remarks>
        /// <param name="schema">The schema.</param>
        /// <param name="countFullBindings">Whether match counts are to be distinct full bindings.</param>
        /// <returns>The rule's id.</returns>
        public EGraphRuleId Build(ColorSchema schema, bool countFullBindings = false)
        {
            ArgumentNullException.ThrowIfNull(schema);
            var rb = schema.EGraph.NewRule(Name, true);
            var vars = new RuleVariable?[VarIsId.Count];
            var defined = Actions.Where(a => a.Result >= 0).Select(a => a.Result).ToHashSet();
            for (var v = 0; v < vars.Length; v++)
                if (defined.Contains(v) == false)
                    vars[v] = rb.NewVarNamed(VarIsId[v] ? ColumnTy.Id : schema.I64, "v" + v);

            RuleEntry Entry(long e, bool isId)
            {
                if (e >= 0)
                    return vars[e] ?? throw new InvalidOperationException($"Variable v{e} of {Name} is used before an action defines it.");

                var value = Decode(e);
                return isId ? RuleEntry.Const(new Value(checked((uint)value)), ColumnTy.Id) : RuleEntry.Const(schema.EGraph.BaseValues.Get<Int64Codec, long>((long)value), schema.I64);
            }

            RuleEntry[] Row(int table, long[] entries)
            {
                var spike = schema.Tables[table];
                var row = new RuleEntry[entries.Length + (spike.HasOutput ? 0 : 1)];
                for (var c = 0; c < entries.Length; c++)
                    row[c] = Entry(entries[c], spike.IsId[c]);

                return row;
            }

            foreach (var atom in Atoms)
            {
                var row = Row(atom.Table, atom.Entries);
                if (schema.IsRelation(atom.Table))
                    row[^1] = rb.NewVar(schema.UnitType);

                rb.QueryTable(schema.Functions[atom.Table], row, false);
            }

            if (countFullBindings)
            {
                var body = new List<RuleEntry>();
                for (var v = 0; v < vars.Length; v++)
                    if (vars[v] is { } variable)
                        body.Add(variable);

                body.Add(rb.NewVar(ColumnTy.Id));
                rb.QueryPrim(schema.Keep, body.ToArray(), ColumnTy.Id);
            }

            foreach (var filter in Filters)
                rb.QueryPrim(filter.Kind == ColorFilterKind.Ne ? schema.Ne : schema.Less, [vars[filter.A]!.Value, vars[filter.B]!.Value, rb.NewVar(ColumnTy.Id)], ColumnTy.Id);

            foreach (var action in Actions)
            {
                switch (action.Kind)
                {
                    case ColorActionKind.Set:
                    {
                        var row = Row(action.Table, action.Args);
                        if (schema.IsRelation(action.Table))
                            row[^1] = RuleEntry.Const(schema.Unit, schema.UnitType);

                        rb.Set(schema.Functions[action.Table], row);
                        break;
                    }
                    case ColorActionKind.Union:
                        rb.Union(Entry(action.Args[0], true), Entry(action.Args[1], true));
                        break;
                    case ColorActionKind.Mint:
                        vars[action.Result] = rb.Lookup(schema.Functions[action.Table], Row(action.Table, action.Args), () => "mint");
                        break;
                    default:
                        vars[action.Result] = rb.CallExternalFunc(schema.LowOr, [Entry(action.Args[0], true)], schema.I64, () => "low-or");
                        break;
                }
            }

            return rb.Build();
        }

        /// <summary>
        /// Gets whether argument <paramref name="column"/> of <paramref name="action"/> holds an id.
        /// </summary>
        public static bool IsIdArgument(ColorAction action, int column) => action.Kind switch
        {
            ColorActionKind.Union or ColorActionKind.Read => true,
            _ => ColorSchema.IdColumns[action.Table][column],
        };

        /// <summary>
        /// Prints the rule.
        /// </summary>
        public string Describe()
        {
            static string E(long e) => e >= 0 ? "v" + e : "#" + Decode(e);
            static string T(int table) => ColorSchema.Names[table];
            var b = new StringBuilder(Name).Append(": ");
            b.AppendJoin(", ", Atoms.Select(a => $"{T(a.Table)}({string.Join(", ", a.Entries.Select(E))})"));
            foreach (var f in Filters)
                b.Append(f.Kind == ColorFilterKind.Ne ? $", v{f.A} != v{f.B}" : $", v{f.A} < v{f.B}");

            b.Append(" => ");
            b.AppendJoin("; ", Actions.Select(a => a.Kind switch
            {
                ColorActionKind.Set => $"set {T(a.Table)}({string.Join(", ", a.Args.Select(E))})",
                ColorActionKind.Union => $"union({E(a.Args[0])}, {E(a.Args[1])})",
                ColorActionKind.Mint => $"v{a.Result} = {T(a.Table)}({string.Join(", ", a.Args.Select(E))})",
                _ => $"v{a.Result} = low-or({E(a.Args[0])})",
            }));
            return b.ToString();
        }

        /// <summary>
        /// Enumerates every entry with its table (or -1) and whether its column holds an id: atoms, then actions.
        /// </summary>
        IEnumerable<(int Table, long Entry, bool IsId)> Entries()
        {
            foreach (var atom in Atoms)
                for (var c = 0; c < atom.Entries.Length; c++)
                    yield return (atom.Table, atom.Entries[c], ColorSchema.IdColumns[atom.Table][c]);

            foreach (var action in Actions)
                for (var c = 0; c < action.Args.Length; c++)
                    yield return (action.Table, action.Args[c], IsIdArgument(action, c));
        }

    }

}
