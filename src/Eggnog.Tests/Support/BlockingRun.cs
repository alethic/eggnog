using System;
using System.Threading;
using System.Threading.Tasks;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A run of the root on another thread that blocks inside a filter until released: while it is blocked, the run's
    /// thread holds the root's guard and the graph's engine lock.
    /// </summary>
    public sealed class BlockingRun : IDisposable
    {

        readonly ManualResetEventSlim started = new();
        readonly ManualResetEventSlim release = new();
        Task<RunReport>? run;

        /// <summary>
        /// Defines the blocking rule and its seed row.
        /// </summary>
        public BlockingRun()
        {
            Graph = new EGraph();
            Seed = Graph.DefineRelation<long>("seed");
            Done = Graph.DefineRelation<long>("done");
            Rules = Graph.DefineRuleset("blocking");
            Rules.DefineRule("block once", r =>
            {
                var x = r.Var<long>("x");
                r.When(Seed.Of(x));
                r.Where(x, _ =>
                {
                    started.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return true;
                });
                r.Then(Done.Of(x));
            });
            Graph.Root.Assume(Seed.Of(1L));
        }

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets the relation the blocking rule reads.</summary>
        public Relation<long> Seed { get; }

        /// <summary>Gets the relation the blocking rule writes.</summary>
        public Relation<long> Done { get; }

        /// <summary>Gets the ruleset of the blocking rule.</summary>
        public Ruleset Rules { get; }

        /// <summary>
        /// Starts the run on a pool thread and waits until it blocks.
        /// </summary>
        public void Start()
        {
            run = Task.Run(() => Graph.Root.Run(Rules));
            if (started.Wait(TimeSpan.FromSeconds(30)) == false)
                throw new TimeoutException("The run did not reach its blocking filter.");
        }

        /// <summary>
        /// Releases the run and waits for its report.
        /// </summary>
        /// <returns>The report.</returns>
        public RunReport Finish()
        {
            release.Set();
            return run!.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Releases the run if it is still blocked.
        /// </summary>
        public void Dispose()
        {
            release.Set();
            run?.Wait(TimeSpan.FromSeconds(30));
        }

    }

}
