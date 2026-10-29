using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A function's merge that is not a lattice (<see cref="FunctionMerge{T}"/>), as its table and the host batch use it.
    /// </summary>
    internal abstract class FunctionMergeDefinition
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The merge's name.</param>
        private protected FunctionMergeDefinition(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Gets the merge's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets whether the merge decodes the old and the new value (<see cref="FunctionMerge.Of{T}"/>), which a class
        /// cannot be; <c>false</c> for <c>new</c>, <c>old</c> and an expression, which merge classes too.
        /// </summary>
        public abstract bool DecodesValues { get; }

        /// <summary>
        /// Gets whether the merge looks at the old and the new value (<see cref="FunctionMerge.Of{T}"/> and an expression),
        /// so that classes it is given before a union are not those egglog's next command would give it; <c>false</c>
        /// for <c>new</c> and <c>old</c>, which only pick one.
        /// </summary>
        public virtual bool ComparesValues => DecodesValues;

        /// <summary>
        /// Gets whether the merge reads classes it holds, egglog's globals, whatever its output: an expression with a class
        /// constant, once its function is defined, which reads the class as the last rebuild before it leaves it.
        /// </summary>
        public virtual bool ReadsClasses => false;

        /// <summary>
        /// Gets whether a host batch can work out the merged value itself (<see cref="MergeCells"/>); <c>false</c> for an
        /// expression that calls tables, so that only the engine evaluates it.
        /// </summary>
        public virtual bool CanSimulate => true;

        /// <summary>
        /// Gets whether the merge calls a primitive over the tables, which writes what it chooses when the engine merges.
        /// </summary>
        public virtual bool WritesTables => false;

        /// <summary>
        /// Gets the tables the merge calls: those of an expression's applications; none for the others.
        /// </summary>
        public virtual IReadOnlyCollection<TableHandle> Tables => Array.Empty<TableHandle>();

        /// <summary>
        /// Creates the engine merge of a function with this merge, registering its external function if it has one: not a
        /// lattice merge, so that only the root writes the function.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="functionName">The function's name, for messages.</param>
        /// <param name="type">The function's output type.</param>
        /// <param name="registered">Gets the external functions registered for the merge, which the caller frees when
        /// the definition fails.</param>
        /// <returns>The engine merge.</returns>
        public abstract Relations.MergeFn CreateMergeFn(EGraph graph, string functionName, ColumnType type, List<Relations.ExternalFunctionId> registered);

        /// <summary>
        /// Merges two cells of the function's output, as the engine's merge will, interning a new value.
        /// </summary>
        /// <param name="scope">The scope the batch writes.</param>
        /// <param name="type">The function's output type.</param>
        /// <param name="stored">The old cell.</param>
        /// <param name="incoming">The new cell.</param>
        /// <param name="union">Unions two class cells as the batch does, for a write primitive the merge calls, such as
        /// <c>(vec-union old new)</c>.</param>
        /// <returns>The merged cell.</returns>
        public abstract uint MergeCells(Scope scope, ColumnType type, uint stored, uint incoming, Action<uint, uint> union);

    }

}
