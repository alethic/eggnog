using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A split rule compiled: its matcher, a naive program whose head is one collector of the key variables, the
    /// set variable and every other variable an assumption uses; the matcher's engine variant; and the assumption template.
    /// </summary>
    /// <remarks>
    /// The matcher is lowered once, naive, with its class constants as written (the engine canonicalizes them per
    /// context), and lives as long as the graph, like a global rule. Immutable once built; thread-safe.
    /// </remarks>
    internal sealed class SplitProgram
    {

        /// <summary>
        /// Initializes a new instance; the matcher's variant is not lowered yet.
        /// </summary>
        /// <param name="matcher">The matcher's program.</param>
        /// <param name="variants">The matcher's engine variants: the naive one only.</param>
        /// <param name="columns">The column types of a match, in column order.</param>
        /// <param name="keyColumns">The key's positions in a match.</param>
        /// <param name="setColumn">The set's position in a match.</param>
        /// <param name="elementType">The case variable's column type.</param>
        /// <param name="countOf">Counts the elements of a set cell, given the set's column type.</param>
        /// <param name="elementAt">Decodes one element of a set cell by position, given the set's column type.</param>
        /// <param name="assumptions">The assumption template.</param>
        public SplitProgram(RuleProgram matcher, RuleVariants variants, ColumnType[] columns, int[] keyColumns, int setColumn,
            ColumnType elementType, Func<ColumnType, uint, int> countOf, Func<ColumnType, uint, int, object> elementAt, SplitAssumptions assumptions)
        {
            Matcher = matcher;
            Variants = variants;
            Columns = columns;
            KeyColumns = keyColumns;
            SetColumn = setColumn;
            ElementType = elementType;
            CountOf = countOf;
            ElementAt = elementAt;
            Template = assumptions;
            var sorts = new List<int>();
            for (var i = 0; i < columns.Length; i++)
                if (columns[i].InvolvesClasses)
                    sorts.Add(i);

            SortColumns = sorts.ToArray();
        }

        /// <summary>
        /// Gets the matcher: a naive program whose only head operation collects a match.
        /// </summary>
        public RuleProgram Matcher { get; }

        /// <summary>
        /// Gets the matcher's engine variants: the naive one only, described <c>$split/{name}#{serial}</c>.
        /// </summary>
        public RuleVariants Variants { get; }

        /// <summary>
        /// Gets the column types of a match, in column order: the key's, the set's, then the other assumption variables'.
        /// </summary>
        public ColumnType[] Columns { get; }

        /// <summary>
        /// Gets the number of columns of a match.
        /// </summary>
        public int Width => Columns.Length;

        /// <summary>
        /// Gets the columns whose cells are classes, ascending: the ones a match is canonicalized on.
        /// </summary>
        public int[] SortColumns { get; }

        /// <summary>
        /// Gets the key's positions in a match: the cells of a request's <c>SplitKey</c>, which a split step compares to
        /// skip a request a live split already covers.
        /// </summary>
        public int[] KeyColumns { get; }

        /// <summary>
        /// Gets the set's position in a match.
        /// </summary>
        public int SetColumn { get; }

        /// <summary>
        /// Gets the case variable's position in the assumption bindings: after a match's columns.
        /// </summary>
        public int ElementColumn => Columns.Length;

        /// <summary>
        /// Gets the case variable's column type.
        /// </summary>
        public ColumnType ElementType { get; }

        /// <summary>
        /// Gets the number of elements of a set cell, given the set's column type: one case per element. Reads the
        /// set's container; allocates nothing.
        /// </summary>
        public Func<ColumnType, uint, int> CountOf { get; }

        /// <summary>
        /// Gets the element of a set cell at a position in set order (<see cref="ElementOrder{T}"/>), given the set's
        /// column type: case <c>i</c>'s element. Decodes the set, and boxes a value-type element.
        /// </summary>
        public Func<ColumnType, uint, int, object> ElementAt { get; }

        /// <summary>
        /// Gets the assumptions as written: ground once a match and an element are bound.
        /// </summary>
        public Fact[] Assumptions => Template.Facts;

        /// <summary>
        /// Gets the assumption template, which grounds the assumptions for one case.
        /// </summary>
        public SplitAssumptions Template { get; }

    }

}
