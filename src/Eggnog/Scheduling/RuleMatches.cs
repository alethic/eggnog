using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A rule's matches in a step of <see cref="Schedule.RunWith"/>, for its <see cref="IRuleScheduler"/> to choose from:
    /// egglog's <c>Matches</c>. A match holds the values of the variables the rule's head uses (<see cref="Variables"/>),
    /// a class as its canonical member in the scope that steps; those the scheduler does not choose wait for the next
    /// step. Valid only during <see cref="IRuleScheduler.FilterMatches"/>.
    /// </summary>
    public sealed class RuleMatches
    {

        readonly uint[] cells;
        readonly ColumnType[] types;
        readonly Scope scope;
        readonly List<int> chosen = new();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cells">The matches' cells, one tuple after another.</param>
        /// <param name="count">The number of matches.</param>
        /// <param name="variables">The names of a tuple's variables.</param>
        /// <param name="types">The column types of a tuple's variables.</param>
        /// <param name="scope">The scope that steps, which decodes a class.</param>
        internal RuleMatches(uint[] cells, int count, IReadOnlyList<string> variables, ColumnType[] types, Scope scope)
        {
            this.cells = cells;
            this.types = types;
            this.scope = scope;
            Count = count;
            Variables = variables;
        }

        /// <summary>
        /// Gets the number of matches: egglog's <c>match_size</c>.
        /// </summary>
        public int Count { get; }

        /// <summary>
        /// Gets the names of a match's variables, those the rule's head uses, in their order in a match; a term the head
        /// shares with the body, which egglog names <c>__v…</c>, is named <c>$</c> and its register.
        /// </summary>
        public IReadOnlyList<string> Variables { get; }

        /// <summary>
        /// Gets the number of values in a match: egglog's <c>tuple_len</c>.
        /// </summary>
        public int TupleLength => types.Length;

        /// <summary>
        /// Gets a match: egglog's <c>get_match</c>.
        /// </summary>
        /// <param name="index">The match's index, from 0 to <see cref="Count"/> less 1.</param>
        /// <returns>The match.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
        public RuleMatch this[int index]
        {
            get
            {
                CheckIndex(index);
                return new RuleMatch(this, index);
            }
        }

        /// <summary>
        /// Chooses a match to fire in this step: egglog's <c>choose</c>. Choosing one twice fires it once.
        /// </summary>
        /// <param name="index">The match's index.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
        public void Choose(int index)
        {
            CheckIndex(index);
            chosen.Add(index);
        }

        /// <summary>
        /// Chooses every match: egglog's <c>choose_all</c>.
        /// </summary>
        public void ChooseAll() => AllChosen = true;

        /// <summary>
        /// Gets whether every match is chosen.
        /// </summary>
        internal bool AllChosen { get; private set; }

        /// <summary>
        /// Gets the indexes chosen one by one, in the order chosen, with repeats.
        /// </summary>
        internal List<int> Chosen => chosen;

        /// <summary>
        /// Gets the value of variable <paramref name="variable"/> in match <paramref name="index"/>.
        /// </summary>
        /// <exception cref="ArgumentException">No variable of the match has the name.</exception>
        internal object Get(int index, string variable)
        {
            ArgumentNullException.ThrowIfNull(variable);
            for (var i = 0; i < types.Length; i++)
                if (string.Equals(Variables[i], variable, StringComparison.Ordinal))
                    return types[i].DecodeObject(cells[index * types.Length + i], scope);

            throw new ArgumentException(
                $"A match has no variable '{variable}'; its variables are {string.Join(", ", Variables)}.", nameof(variable));
        }

        /// <summary>
        /// Checks a match's index.
        /// </summary>
        void CheckIndex(int index)
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"There are {Count} matches.");
        }

    }

}
