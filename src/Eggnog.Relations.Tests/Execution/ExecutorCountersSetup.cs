using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Runs the test suite with the executor's counters on (<c>EGGNOG_COUNTERS</c>, <see cref="ExecutorCounters"/>,
    /// colored-contexts spec §17 M10.2): the per-child cost tests read them, and the layered property tests check they
    /// reached the layered paths. The counters are a static read-only switch read once, so it is set here, before any
    /// engine type initializes. Set <c>EGGNOG_COUNTERS=0</c> to run the suite as production runs, without them.
    /// </summary>
    static class ExecutorCountersSetup
    {

        /// <summary>
        /// Turns the counters on unless the environment says otherwise.
        /// </summary>
        [ModuleInitializer]
        internal static void Enable()
        {
            if (Environment.GetEnvironmentVariable("EGGNOG_COUNTERS") is null)
                Environment.SetEnvironmentVariable("EGGNOG_COUNTERS", "1");
        }

    }

}
