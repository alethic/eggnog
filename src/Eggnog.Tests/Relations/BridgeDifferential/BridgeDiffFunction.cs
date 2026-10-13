using System;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A function of a bridge differential program: its name, kind, key sorts and output sort, and whether its rows can be
    /// subsumed. Immutable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any number of keys, none included (an egglog global): the oracle and the comparison key rows by
    /// <see cref="BridgeDiffKey"/>. The output sort follows from the kind: an eq sort for constructors and id functions,
    /// integers for the lattices, and <see cref="BridgeDiffSort.Unit"/> for relations.
    /// </para>
    /// <para>
    /// A relation's output is written as the unit sort because egglog's syntax gives a relation none, but its cells are
    /// ids: the frontend desugars <c>(relation R (A B))</c> into a constructor into a fresh sort of its own, which nothing
    /// unions (egglog/src/ast/desugar.rs:396-427), so each row has an id of its own (<see cref="IsIdColumn"/>). Where a
    /// program writes the unit constant as a relation's output it means egglog's <c>(R a b)</c>: in an atom, any output
    /// (a fresh variable); in a set or a fact, an insertion of the key (<see cref="IsNoOutput"/>).
    /// </para>
    /// </remarks>
    sealed class BridgeDiffFunction
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="kind">The kind.</param>
        /// <param name="keys">The key sorts.</param>
        /// <param name="output">The output sort; <see cref="BridgeDiffSort.Unit"/> for a relation.</param>
        /// <param name="canSubsume">Whether rows can be subsumed.</param>
        public BridgeDiffFunction(string name, BridgeDiffFunctionKind kind, int[] keys, int output, bool canSubsume)
        {
            ArgumentNullException.ThrowIfNull(keys);
            if (kind == BridgeDiffFunctionKind.Relation && output != BridgeDiffSort.Unit)
                throw new ArgumentException($"The relation {name} must have the unit sort as its output.", nameof(output));

            Name = name;
            Kind = kind;
            Keys = keys;
            Output = output;
            CanSubsume = canSubsume;
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the kind.
        /// </summary>
        public BridgeDiffFunctionKind Kind { get; }

        /// <summary>
        /// Gets the key sorts.
        /// </summary>
        public int[] Keys { get; }

        /// <summary>
        /// Gets the output sort.
        /// </summary>
        public int Output { get; }

        /// <summary>
        /// Gets whether rows can be subsumed: the function's table has a subsumption column.
        /// </summary>
        public bool CanSubsume { get; }

        /// <summary>
        /// Gets the number of key columns.
        /// </summary>
        public int KeyCount => Keys.Length;

        /// <summary>
        /// Gets the number of columns, keys then output.
        /// </summary>
        public int Columns => Keys.Length + 1;

        /// <summary>
        /// Gets whether the output is an e-class id, merged by union: an eq-sort output, or a relation's row id.
        /// </summary>
        public bool OutputIsId => Kind == BridgeDiffFunctionKind.Relation || BridgeDiffSort.IsEq(Output);

        /// <summary>
        /// Gets whether a lookup of a missing key inserts a row with a fresh id (constructors and relations, which the
        /// frontend declares alike) rather than failing.
        /// </summary>
        public bool HasDefault => Kind is BridgeDiffFunctionKind.Constructor or BridgeDiffFunctionKind.Relation;

        /// <summary>
        /// Gets the sort of column <paramref name="column"/>: a key sort, or the output sort for the last column.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <returns>The sort.</returns>
        public int SortOf(int column) => column < Keys.Length ? Keys[column] : Output;

        /// <summary>
        /// Gets whether column <paramref name="column"/> holds e-class ids: an eq-sort key, or an output that
        /// <see cref="OutputIsId"/>.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <returns><c>true</c> for an id column.</returns>
        public bool IsIdColumn(int column) => column < Keys.Length ? BridgeDiffSort.IsEq(Keys[column]) : OutputIsId;

        /// <summary>
        /// Gets whether <paramref name="entry"/>, in this function's output column, stands for no output: the unit
        /// constant as a relation's output, egglog's <c>(R a b)</c>.
        /// </summary>
        /// <param name="entry">The entry of the output column.</param>
        /// <returns><c>true</c> when the entry names no output.</returns>
        public bool IsNoOutput(BridgeDiffEntry entry) => Kind == BridgeDiffFunctionKind.Relation && entry.IsVar == false && entry.Sort == BridgeDiffSort.Unit;

        /// <summary>
        /// Lists the declaration egglog-style, for example <c>(constructor Op0 (A A) A :subsume)</c>.
        /// </summary>
        /// <returns>The declaration.</returns>
        public override string ToString()
        {
            var keys = string.Join(" ", Keys.Select(BridgeDiffSort.Name));
            var subsume = CanSubsume ? " :subsume" : "";
            return Kind switch
            {
                BridgeDiffFunctionKind.Constructor => $"(constructor {Name} ({keys}) {BridgeDiffSort.Name(Output)}{subsume})",
                BridgeDiffFunctionKind.Relation => $"(relation {Name} ({keys})){(CanSubsume ? "" : " ; without a subsumption column")}",
                BridgeDiffFunctionKind.Min => $"(function {Name} ({keys}) i64 :merge (min old new))",
                BridgeDiffFunctionKind.Max => $"(function {Name} ({keys}) i64 :merge (max old new))",
                _ => $"(function {Name} ({keys}) {BridgeDiffSort.Name(Output)} :merge union)",
            };
        }

    }

}
