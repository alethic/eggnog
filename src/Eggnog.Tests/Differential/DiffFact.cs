using System;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A fact of a random program: a relation row, an equality, a disequality, a filter or an existence. In a body a
    /// condition, in a head or a host write a write, in an <c>Until</c> a host check. Immutable.
    /// </summary>
    internal sealed class DiffFact
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        DiffFact(DiffFactKind kind, DiffTable? table, DiffTerm[] arguments, DiffTerm? left, DiffTerm? right)
        {
            Kind = kind;
            Table = table;
            Arguments = arguments;
            Left = left;
            Right = right;
        }

        /// <summary>Gets what the fact is.</summary>
        public DiffFactKind Kind { get; }

        /// <summary>Gets a relation row's relation.</summary>
        public DiffTable? Table { get; }

        /// <summary>Gets a relation row's arguments.</summary>
        public DiffTerm[] Arguments { get; }

        /// <summary>Gets an equality's or disequality's left side, a filter's computation, or an existence's
        /// application.</summary>
        public DiffTerm? Left { get; }

        /// <summary>Gets an equality's or disequality's right side.</summary>
        public DiffTerm? Right { get; }

        /// <summary>
        /// A relation row.
        /// </summary>
        /// <param name="relation">The relation.</param>
        /// <param name="arguments">One term per column.</param>
        /// <returns>The fact.</returns>
        public static DiffFact Row(DiffTable relation, params DiffTerm[] arguments) => new(DiffFactKind.Relation, relation, arguments, null, null);

        /// <summary>
        /// An equality.
        /// </summary>
        /// <param name="left">One side.</param>
        /// <param name="right">The other side, of the same type.</param>
        /// <returns>The fact.</returns>
        public static DiffFact Equal(DiffTerm left, DiffTerm right) => new(DiffFactKind.Equal, null, Array.Empty<DiffTerm>(), left, right);

        /// <summary>
        /// A disequality.
        /// </summary>
        /// <param name="left">One side.</param>
        /// <param name="right">The other side, of the same type.</param>
        /// <returns>The fact.</returns>
        public static DiffFact NotEqual(DiffTerm left, DiffTerm right) => new(DiffFactKind.NotEqual, null, Array.Empty<DiffTerm>(), left, right);

        /// <summary>
        /// A filter on a boolean computation.
        /// </summary>
        /// <param name="condition">The computation.</param>
        /// <returns>The fact.</returns>
        public static DiffFact Filter(DiffTerm condition) => new(DiffFactKind.Filter, null, Array.Empty<DiffTerm>(), condition, null);

        /// <summary>
        /// An existence.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <returns>The fact.</returns>
        public static DiffFact Exists(DiffTerm application) => new(DiffFactKind.Exists, null, Array.Empty<DiffTerm>(), application, null);

        /// <inheritdoc />
        public override string ToString() => ProgramPrinter.Fact(this);

    }

}
