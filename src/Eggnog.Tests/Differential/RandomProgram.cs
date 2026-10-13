using System.Collections.Generic;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A random rule-layer program: tables, rulesets, queries, and a sequence of commands (host writes, rule
    /// definitions, runs, queries) that the differential test runs three ways.
    /// </summary>
    internal sealed class RandomProgram
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="seed">The seed it was generated from.</param>
        /// <param name="tables">The tables, in declaration order.</param>
        /// <param name="rulesets">The number of rulesets.</param>
        /// <param name="queries">The queries defined before the first command; later ones are
        /// <see cref="DiffCommandKind.DefineQuery"/> commands, numbered after these.</param>
        /// <param name="commands">The commands.</param>
        public RandomProgram(int seed, List<DiffTable> tables, int rulesets, List<DiffRule> queries, List<DiffCommand> commands)
        {
            Seed = seed;
            Tables = tables;
            Rulesets = rulesets;
            Queries = queries;
            Commands = commands;
        }

        /// <summary>Gets the seed the program was generated from.</summary>
        public int Seed { get; }

        /// <summary>Gets the tables, in declaration order.</summary>
        public List<DiffTable> Tables { get; }

        /// <summary>Gets the number of rulesets, named <c>rs0</c>, <c>rs1</c>, ….</summary>
        public int Rulesets { get; }

        /// <summary>Gets the queries defined before the first command, numbered from 0; the
        /// <see cref="DiffCommandKind.DefineQuery"/> commands number theirs after these.</summary>
        public List<DiffRule> Queries { get; }

        /// <summary>Gets the commands.</summary>
        public List<DiffCommand> Commands { get; }

        /// <summary>
        /// Generates the program of a seed: the same seed always gives the same program.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <returns>The program.</returns>
        public static RandomProgram Generate(int seed) => new ProgramGenerator(seed).Generate();

        /// <summary>
        /// Prints the program as C# source against the public API, for a failure's message.
        /// </summary>
        /// <returns>The source.</returns>
        public override string ToString() => ProgramPrinter.Print(this);

    }

}
