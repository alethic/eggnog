using System;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Plans as lines of text, for golden comparisons.
    /// </summary>
    static class PlanLines
    {

        public static string[] Of(PlanTemplate plan) =>
            plan.ToString()!.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();

    }

}
