using System;

namespace Eggnog
{

    /// <summary>
    /// The base of the typed functions: maps from keys to a value, merged by a lattice or, without one, required to agree.
    /// </summary>
    public abstract class FunctionHandle : TableHandle
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        private protected FunctionHandle()
        {

        }

        /// <summary>
        /// Gets the lattice that merges the function's values; <c>null</c> when two different values are an error.
        /// </summary>
        public ValueLattice? Lattice { get; internal set; }

        /// <summary>
        /// Gets the case join a lift uses for the function's values (§6.9.1, R3), over cells: its lattice's
        /// (<see cref="ValueLattice{T}.CaseJoin"/>), made once when the function is defined, so every lift spec built later
        /// holds the same delegate and the engine's lift memo, which compares specs by content, still hits; <c>null</c>
        /// when the function has no lattice or its lattice no case join, and a lift then requires every live case to agree.
        /// </summary>
        internal Func<uint, uint, uint>? CaseJoin { get; set; }

        /// <summary>
        /// The untyped application: the function's value at <paramref name="arguments"/> (D13).
        /// </summary>
        /// <param name="arguments">One term per key, of the key's CLR type.</param>
        /// <returns>The term.</returns>
        internal Term Apply(params Term[] arguments)
        {
            CheckArguments(arguments);
            return new UntypedApplicationTerm(this, (Term[])arguments.Clone());
        }

        /// <summary>
        /// The untyped scoped view (D13): what the typed <c>In(tag)</c> returns, as an untyped function.
        /// </summary>
        /// <param name="tag">The tag of the scope to read.</param>
        /// <returns>The view.</returns>
        /// <exception cref="System.ArgumentException">The tag belongs to another graph, or the table has a sort
        /// column.</exception>
        internal FunctionHandle InScope(ScopeTag tag) => ViewIn<FunctionHandle>(new UntypedFunction(), tag);

    }

}
