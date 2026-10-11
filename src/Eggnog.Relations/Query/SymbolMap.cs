using System;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog.Relations
{

    /// <summary>
    /// The human-readable names of a rule's atoms (the names of their tables) and variables, for reports and plan
    /// dumps. egglog's <c>SymbolMap</c> (core-relations/src/query.rs:27-33).
    /// </summary>
    /// <remarks>
    /// Only named tables and named variables appear. Immutable once built, so one instance is shared by a rule, its
    /// cached plan and every rule set the cached plan is added to (egglog clones it).
    /// </remarks>
    internal sealed class SymbolMap
    {

        readonly DenseIdMap<AtomId, string> atoms;
        readonly DenseIdMap<Variable, string> variables;

        /// <summary>
        /// Initializes a new instance. The maps are owned by the new instance.
        /// </summary>
        /// <param name="atoms">The name of each atom whose table is named.</param>
        /// <param name="variables">The name of each named variable.</param>
        public SymbolMap(DenseIdMap<AtomId, string> atoms, DenseIdMap<Variable, string> variables)
        {
            ArgumentNullException.ThrowIfNull(atoms);
            ArgumentNullException.ThrowIfNull(variables);
            this.atoms = atoms;
            this.variables = variables;
        }

        /// <summary>
        /// Gets the number of named atoms.
        /// </summary>
        public int AtomCount => atoms.Count;

        /// <summary>
        /// Gets the number of named variables.
        /// </summary>
        public int VariableCount => variables.Count;

        /// <summary>
        /// Builds the map of <paramref name="query"/>: each atom named after its table, when the table has a name, and
        /// each variable that has a name. egglog's <c>build_symbol_map</c> (core-relations/src/query.rs:531-549).
        /// </summary>
        /// <param name="database">The database that declares the query's tables.</param>
        /// <param name="query">The query.</param>
        /// <returns>The map.</returns>
        public static SymbolMap Create(Database database, Query query)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(query);

            var atoms = new DenseIdMap<AtomId, string>(query.Atoms.IdCount);
            foreach (var (atom, info) in query.Atoms)
                if (database.GetTableInfo(info.Table).Name is { } name)
                    atoms.Insert(atom, name);

            var variables = new DenseIdMap<Variable, string>(query.Variables.IdCount);
            foreach (var (variable, info) in query.Variables)
                if (info.Name is { } name)
                    variables.Insert(variable, name);

            return new SymbolMap(atoms, variables);
        }

        /// <summary>
        /// Gets the name of <paramref name="atom"/>: the name of its table.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <param name="name">The name.</param>
        /// <returns><c>true</c> if the atom's table is named.</returns>
        public bool TryGetAtomName(AtomId atom, [MaybeNullWhen(false)] out string name) => atoms.TryGetValue(atom, out name);

        /// <summary>
        /// Gets the name of <paramref name="variable"/>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <param name="name">The name.</param>
        /// <returns><c>true</c> if the variable is named.</returns>
        public bool TryGetVariableName(Variable variable, [MaybeNullWhen(false)] out string name) => variables.TryGetValue(variable, out name);

    }

}
