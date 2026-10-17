using System.Globalization;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A graph with <see cref="NumericBound"/> registered and two functions over it, <c>lo</c> and <c>hi</c>, merged by
    /// user lattices: <c>lo</c> merges to the greater bound and joins cases to the lesser, <c>hi</c> mirrors that, and on
    /// a tie of value the open bound is the tighter one. Four relations of comparisons (<c>gt</c>, <c>ge</c>, <c>lt</c>,
    /// <c>le</c>, over a path and a <c>decimal</c>) give the bounds through the rules of <see cref="Bounds"/>.
    /// </summary>
    public sealed class NumericBoundTheory
    {

        /// <summary>
        /// Defines the theory in a new graph.
        /// </summary>
        public NumericBoundTheory()
        {
            Graph = new EGraph();
            Graph.Values.Register<decimal>("Decimal");
            Graph.Values.Register<NumericBound>("NumericBound", format: Format);
            Lo = Graph.DefineFunction<string, NumericBound>("lo", LowerLattice());
            Hi = Graph.DefineFunction<string, NumericBound>("hi", UpperLattice());
            Gt = Graph.DefineRelation<string, decimal>("gt");
            Ge = Graph.DefineRelation<string, decimal>("ge");
            Lt = Graph.DefineRelation<string, decimal>("lt");
            Le = Graph.DefineRelation<string, decimal>("le");
            Bounds = Graph.DefineRuleset("bounds");
            DefineBound("gt gives an open lower bound", Gt, Lo, true);
            DefineBound("ge gives a closed lower bound", Ge, Lo, false);
            DefineBound("lt gives an open upper bound", Lt, Hi, true);
            DefineBound("le gives a closed upper bound", Le, Hi, false);
        }

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets a path's lower bound, merged by <see cref="LowerLattice"/>.</summary>
        public Function<string, NumericBound> Lo { get; }

        /// <summary>Gets a path's upper bound, merged by <see cref="UpperLattice"/>.</summary>
        public Function<string, NumericBound> Hi { get; }

        /// <summary>Gets the comparisons <c>path &gt; value</c>.</summary>
        public Relation<string, decimal> Gt { get; }

        /// <summary>Gets the comparisons <c>path &gt;= value</c>.</summary>
        public Relation<string, decimal> Ge { get; }

        /// <summary>Gets the comparisons <c>path &lt; value</c>.</summary>
        public Relation<string, decimal> Lt { get; }

        /// <summary>Gets the comparisons <c>path &lt;= value</c>.</summary>
        public Relation<string, decimal> Le { get; }

        /// <summary>Gets the rules that derive <see cref="Lo"/> and <see cref="Hi"/> from the comparisons.</summary>
        public Ruleset Bounds { get; }

        /// <summary>
        /// Makes the lattice of lower bounds: the merge keeps the greater bound, an open bound winning a tie of value;
        /// the case join keeps the lesser, a closed bound winning a tie.
        /// </summary>
        /// <returns>The lattice.</returns>
        public static ValueLattice<NumericBound> LowerLattice() => new("lower-bound", TighterLower, LooserLower);

        /// <summary>
        /// Makes the lattice of upper bounds: the merge keeps the lesser bound, an open bound winning a tie of value;
        /// the case join keeps the greater, a closed bound winning a tie.
        /// </summary>
        /// <returns>The lattice.</returns>
        public static ValueLattice<NumericBound> UpperLattice() => new("upper-bound", TighterUpper, LooserUpper);

        /// <summary>
        /// Formats a bound as a literal: <c>(open 1.5)</c> or <c>(closed 2)</c>.
        /// </summary>
        /// <param name="bound">The bound.</param>
        /// <returns>The literal.</returns>
        public static string Format(NumericBound bound) =>
            (bound.Open ? "(open " : "(closed ") + bound.Value.ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>
        /// The tighter of two lower bounds; the first when they are equal.
        /// </summary>
        static NumericBound TighterLower(NumericBound a, NumericBound b)
        {
            if (a.Value != b.Value)
                return a.Value > b.Value ? a : b;

            return a.Open || b.Open == false ? a : b;
        }

        /// <summary>
        /// The looser of two lower bounds; the first when they are equal.
        /// </summary>
        static NumericBound LooserLower(NumericBound a, NumericBound b)
        {
            if (a.Value != b.Value)
                return a.Value < b.Value ? a : b;

            return a.Open == false || b.Open ? a : b;
        }

        /// <summary>
        /// The tighter of two upper bounds; the first when they are equal.
        /// </summary>
        static NumericBound TighterUpper(NumericBound a, NumericBound b)
        {
            if (a.Value != b.Value)
                return a.Value < b.Value ? a : b;

            return a.Open || b.Open == false ? a : b;
        }

        /// <summary>
        /// The looser of two upper bounds; the first when they are equal.
        /// </summary>
        static NumericBound LooserUpper(NumericBound a, NumericBound b)
        {
            if (a.Value != b.Value)
                return a.Value > b.Value ? a : b;

            return a.Open == false || b.Open ? a : b;
        }

        /// <summary>
        /// Defines the rule that turns each comparison of <paramref name="comparison"/> into a bound of
        /// <paramref name="bound"/>.
        /// </summary>
        void DefineBound(string name, Relation<string, decimal> comparison, Function<string, NumericBound> bound, bool open)
        {
            Bounds.DefineRule(name, r =>
            {
                var (p, v) = r.Vars<string, decimal>("p", "v");
                r.When(comparison.Of(p, v));
                r.Then(bound.Of(p).Is(Compute.Of(v, value => new NumericBound(value, open))));
            });
        }

    }

}
