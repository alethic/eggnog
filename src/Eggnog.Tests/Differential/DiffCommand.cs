using System;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A command of a random program: host writes, a rule definition, a run or a query. Immutable.
    /// </summary>
    internal sealed class DiffCommand
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        DiffCommand(DiffCommandKind kind, DiffFact[] facts, DiffTerm? term, int first, int second, DiffRule? rule, DiffSchedule? schedule,
            DiffQueryMode mode)
        {
            Kind = kind;
            Facts = facts;
            Term = term;
            First = first;
            Second = second;
            Rule = rule;
            Schedule = schedule;
            Mode = mode;
        }

        /// <summary>Gets what the command does.</summary>
        public DiffCommandKind Kind { get; }

        /// <summary>Gets an <c>Assume</c>'s ground facts.</summary>
        public DiffFact[] Facts { get; }

        /// <summary>Gets a <c>Make</c>'s ground constructor term.</summary>
        public DiffTerm? Term { get; }

        /// <summary>Gets a union's first handle, a run's iteration limit, or a query's index.</summary>
        public int First { get; }

        /// <summary>Gets a union's second handle.</summary>
        public int Second { get; }

        /// <summary>Gets a defined rule or query.</summary>
        public DiffRule? Rule { get; }

        /// <summary>Gets a run's schedule.</summary>
        public DiffSchedule? Schedule { get; }

        /// <summary>Gets how a query is asked.</summary>
        public DiffQueryMode Mode { get; }

        /// <summary>Host writes of ground facts, one batch.</summary>
        /// <param name="facts">The facts.</param>
        /// <returns>The command.</returns>
        public static DiffCommand Assume(params DiffFact[] facts) => new(DiffCommandKind.Assume, facts, null, 0, 0, null, null, default);

        /// <summary>A host-made term, whose class becomes the next handle.</summary>
        /// <param name="term">The ground constructor term.</param>
        /// <returns>The command.</returns>
        public static DiffCommand Make(DiffTerm term) => new(DiffCommandKind.Make, Array.Empty<DiffFact>(), term, 0, 0, null, null, default);

        /// <summary>A host union of two handles.</summary>
        /// <param name="a">One handle.</param>
        /// <param name="b">The other handle.</param>
        /// <returns>The command.</returns>
        public static DiffCommand Union(int a, int b) => new(DiffCommandKind.Union, Array.Empty<DiffFact>(), null, a, b, null, null, default);

        /// <summary>Defines a rule.</summary>
        /// <param name="rule">The rule.</param>
        /// <returns>The command.</returns>
        public static DiffCommand Define(DiffRule rule) => new(DiffCommandKind.DefineRule, Array.Empty<DiffFact>(), null, 0, 0, rule, null, default);

        /// <summary>Runs a schedule.</summary>
        /// <param name="schedule">The schedule.</param>
        /// <param name="iterationLimit">The run's iteration limit.</param>
        /// <returns>The command.</returns>
        public static DiffCommand Run(DiffSchedule schedule, int iterationLimit) =>
            new(DiffCommandKind.Run, Array.Empty<DiffFact>(), null, iterationLimit, 0, null, schedule, default);

        /// <summary>Defines a query, which takes the next query number.</summary>
        /// <param name="query">The query.</param>
        /// <returns>The command.</returns>
        public static DiffCommand DefineQuery(DiffRule query) => new(DiffCommandKind.DefineQuery, Array.Empty<DiffFact>(), null, 0, 0, query, null, default);

        /// <summary>Asks a query.</summary>
        /// <param name="query">The query's index.</param>
        /// <param name="mode">How.</param>
        /// <returns>The command.</returns>
        public static DiffCommand Query(int query, DiffQueryMode mode) => new(DiffCommandKind.Query, Array.Empty<DiffFact>(), null, query, 0, null, null, mode);

    }

}
