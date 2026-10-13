using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A constraint on the cells of a row: egglog's <c>Constraint</c> (core-relations/src/table_spec.rs:92-101).
    /// Either two columns are equal (<see cref="ConstraintKind.Eq"/>) or one column compares to a constant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tagged struct in place of the Rust enum with data: <see cref="Kind"/> says which variant it is,
    /// <see cref="Column"/> is the (left) column, <see cref="OtherColumn"/> the right column of an
    /// <see cref="ConstraintKind.Eq"/>, and <see cref="Constant"/> the constant of the others. Comparisons are unsigned
    /// comparisons of the raw cells, as egglog compares <c>Value</c>s by their <c>u32</c> representation.
    /// </para>
    /// <para>
    /// Constraints never test staleness: a tombstoned row keeps its other cells, so whoever evaluates a constraint on a
    /// physical row checks <see cref="Value.StaleRep"/> first. The tables do so on every path, where egglog's
    /// constrained scans skip the tombstone check (core-relations/src/table/mod.rs:429-439, 1018-1025).
    /// </para>
    /// </remarks>
    internal readonly record struct Constraint
    {

        Constraint(ConstraintKind kind, int column, int otherColumn, uint value)
        {
            Kind = kind;
            Column = column;
            OtherColumn = otherColumn;
            Constant = value;
        }

        /// <summary>
        /// Gets the kind of constraint.
        /// </summary>
        public ConstraintKind Kind { get; }

        /// <summary>
        /// Gets the constrained column; the left column of an <see cref="ConstraintKind.Eq"/>.
        /// </summary>
        public int Column { get; }

        /// <summary>
        /// Gets the right column of an <see cref="ConstraintKind.Eq"/>; -1 for the other kinds.
        /// </summary>
        public int OtherColumn { get; }

        /// <summary>
        /// Gets the constant the column is compared with; zero for <see cref="ConstraintKind.Eq"/>.
        /// </summary>
        public uint Constant { get; }

        /// <summary>
        /// Creates <c>row[left] == row[right]</c>.
        /// </summary>
        /// <param name="left">The left column.</param>
        /// <param name="right">The right column.</param>
        /// <returns>The constraint.</returns>
        public static Constraint Eq(ColumnId left, ColumnId right)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(left.Rep, nameof(left));
            ArgumentOutOfRangeException.ThrowIfNegative(right.Rep, nameof(right));
            return new Constraint(ConstraintKind.Eq, left.Rep, right.Rep, 0);
        }

        /// <summary>
        /// Creates <c>row[column] == value</c>.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="value">The constant.</param>
        /// <returns>The constraint.</returns>
        public static Constraint EqConst(ColumnId column, Value value) => Const(ConstraintKind.EqConst, column, value);

        /// <summary>
        /// Creates <c>row[column] &lt; value</c>.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="value">The constant.</param>
        /// <returns>The constraint.</returns>
        public static Constraint LtConst(ColumnId column, Value value) => Const(ConstraintKind.LtConst, column, value);

        /// <summary>
        /// Creates <c>row[column] &gt; value</c>.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="value">The constant.</param>
        /// <returns>The constraint.</returns>
        public static Constraint GtConst(ColumnId column, Value value) => Const(ConstraintKind.GtConst, column, value);

        /// <summary>
        /// Creates <c>row[column] &lt;= value</c>.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="value">The constant.</param>
        /// <returns>The constraint.</returns>
        public static Constraint LeConst(ColumnId column, Value value) => Const(ConstraintKind.LeConst, column, value);

        /// <summary>
        /// Creates <c>row[column] &gt;= value</c>.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="value">The constant.</param>
        /// <returns>The constraint.</returns>
        public static Constraint GeConst(ColumnId column, Value value) => Const(ConstraintKind.GeConst, column, value);

        /// <summary>
        /// Gets whether <paramref name="row"/> satisfies the constraint. Does not test staleness. O(1).
        /// </summary>
        /// <param name="row">The row's cells.</param>
        /// <returns><c>true</c> if satisfied.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Evaluate(ReadOnlySpan<uint> row)
        {
            var cell = row[Column];
            return Kind == ConstraintKind.Eq ? cell == row[OtherColumn] : Compare(cell);
        }

        /// <summary>
        /// Gets whether the row whose first cell is <paramref name="data"/>[<paramref name="offset"/>] satisfies the
        /// constraint. The array form of <see cref="Evaluate(ReadOnlySpan{uint})"/> for compiled plans. O(1).
        /// </summary>
        /// <param name="data">The array holding the row.</param>
        /// <param name="offset">The index of the row's first cell.</param>
        /// <returns><c>true</c> if satisfied.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Evaluate(uint[] data, int offset)
        {
            var cell = data[offset + Column];
            return Kind == ConstraintKind.Eq ? cell == data[offset + OtherColumn] : Compare(cell);
        }

        /// <summary>
        /// Compares one cell with the constant of a constant constraint; for <see cref="ConstraintKind.Eq"/>, which
        /// needs two cells, use <see cref="Evaluate(ReadOnlySpan{uint})"/>. O(1).
        /// </summary>
        /// <param name="cell">The value of <see cref="Column"/>.</param>
        /// <returns><c>true</c> if the comparison holds.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Compare(uint cell)
        {
            switch (Kind)
            {
                case ConstraintKind.EqConst:
                    return cell == Constant;
                case ConstraintKind.LtConst:
                    return cell < Constant;
                case ConstraintKind.GtConst:
                    return cell > Constant;
                case ConstraintKind.LeConst:
                    return cell <= Constant;
                case ConstraintKind.GeConst:
                    return cell >= Constant;
                default:
                    throw new InvalidOperationException("An Eq constraint compares two columns, not a column and a constant.");
            }
        }

        /// <summary>
        /// Gets whether <paramref name="row"/> satisfies every constraint: egglog's <c>eval_constraints</c>
        /// (core-relations/src/table/mod.rs:1007-1016). Does not test staleness. O(constraints).
        /// </summary>
        /// <param name="constraints">The constraints.</param>
        /// <param name="row">The row's cells.</param>
        /// <returns><c>true</c> if all are satisfied (vacuously for none).</returns>
        public static bool EvaluateAll(ReadOnlySpan<Constraint> constraints, ReadOnlySpan<uint> row)
        {
            for (var i = 0; i < constraints.Length; i++)
                if (constraints[i].Evaluate(row) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Kind switch
            {
                ConstraintKind.Eq => $"c{Column} == c{OtherColumn}",
                ConstraintKind.EqConst => $"c{Column} == {Constant}",
                ConstraintKind.LtConst => $"c{Column} < {Constant}",
                ConstraintKind.GtConst => $"c{Column} > {Constant}",
                ConstraintKind.LeConst => $"c{Column} <= {Constant}",
                _ => $"c{Column} >= {Constant}",
            };
        }

        static Constraint Const(ConstraintKind kind, ColumnId column, Value value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(column.Rep, nameof(column));
            return new Constraint(kind, column.Rep, -1, value.Rep);
        }

    }

}
