using System;
using System.Globalization;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;

using LibraryGraph = Eggnog.EGraph;
using LibraryScope = Eggnog.Scope;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The console app's plan footprint mode, <c>--plan-footprint [rules...]</c>: the bytes a freeze spends on planning
    /// the rules a scope's descendants can run, per rule, on a synthetic theory of 100 and 1,000 global rules (or the
    /// counts given). Counts and bytes only: no time is measured, so the figures need no idle machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The theory has <see cref="RelationCount"/> binary relations over strings and the given number of global Horn
    /// rules, alternately joins of two and three atoms over relations chosen by the rule's index, each writing one
    /// relation. Under a frozen, empty root, two sibling scopes receive the same declarations
    /// (<see cref="FactsPerRelation"/> facts in every relation) without any run. The first is frozen, which plans every
    /// rule on its statistics; then the second, which plans nothing, since every rule has a plan by then. The difference
    /// between the two freezes is the planning: allocated bytes are <see cref="GC.GetAllocatedBytesForCurrentThread"/>
    /// around each freeze, retained bytes the managed heap after two forced, blocking, compacting collections before and
    /// after each. A warm-up on a small theory runs first, so neither figure includes the first use of a code path.
    /// </para>
    /// <para>
    /// The per-rule figures are what one more plan per frozen scope would cost, for replanning rules on each frozen
    /// scope's statistics.
    /// </para>
    /// </remarks>
    internal static class PlanFootprint
    {

        /// <summary>
        /// The command-line option that selects this mode; rule counts may follow it.
        /// </summary>
        public const string Option = "--plan-footprint";

        /// <summary>
        /// The number of relations of the synthetic theory.
        /// </summary>
        const int RelationCount = 32;

        /// <summary>
        /// The facts each sibling scope declares in every relation.
        /// </summary>
        const int FactsPerRelation = 16;

        /// <summary>
        /// Runs the measurement for each rule count and prints one table.
        /// </summary>
        /// <param name="args">Rule counts; none for 100 and 1,000.</param>
        /// <returns>0; 2 for a bad command line.</returns>
        public static int Run(ReadOnlySpan<string> args)
        {
            var counts = new int[args.Length == 0 ? 2 : args.Length];
            if (args.Length == 0)
            {
                counts[0] = 100;
                counts[1] = 1_000;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out counts[i]) == false || counts[i] < 1)
                {
                    Console.Error.WriteLine($"usage: Eggnog.Benchmarks {Option} [rules...]");
                    return 2;
                }
            }

            Measure(10);
            Console.WriteLine($"## Plans made at a sibling scope's freeze, synthetic theory ({RelationCount} relations, {FactsPerRelation} facts per relation per scope), server GC: {GCSettings.IsServerGC}");
            Console.WriteLine();
            Console.WriteLine("| Rules | First freeze allocated | Second freeze allocated | Allocated per plan | First freeze retained | Second freeze retained | Retained per plan | Retained per scope at one plan per rule |");
            Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var rules in counts)
            {
                var (firstAllocated, secondAllocated, firstRetained, secondRetained) = Measure(rules);
                var allocatedPerPlan = (firstAllocated - secondAllocated) / (double)rules;
                var retainedPerPlan = (firstRetained - secondRetained) / (double)rules;
                Console.WriteLine($"| {rules:N0} | {Kib(firstAllocated)} | {Kib(secondAllocated)} | {Kib(allocatedPerPlan)} | {Kib(firstRetained)} | {Kib(secondRetained)} | {Kib(retainedPerPlan)} | {Kib(retainedPerPlan * rules)} |");
            }

            return 0;
        }

        /// <summary>
        /// Builds the theory with <paramref name="rules"/> rules, freezes the root, declares two sibling scopes, and
        /// freezes them in turn. Not inlined, so the graph is unreachable once it returns.
        /// </summary>
        /// <returns>Bytes allocated by each freeze and retained after each.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static (long FirstAllocated, long SecondAllocated, long FirstRetained, long SecondRetained) Measure(int rules)
        {
            var graph = new LibraryGraph();
            var relations = Enumerable.Range(0, RelationCount).Select(i => graph.DefineRelation<string, string>($"r{i}")).ToArray();
            var theory = graph.DefineRuleset("theory");
            for (var i = 0; i < rules; i++)
            {
                var a = relations[i % RelationCount];
                var b = relations[(i * 7 + 3) % RelationCount];
                var c = relations[(i * 11 + 5) % RelationCount];
                var head = relations[(i * 13 + 1) % RelationCount];
                var three = i % 2 == 1;
                theory.DefineRule($"rule {i}", r =>
                {
                    var (x, y, z) = r.Vars<string, string, string>("x", "y", "z");
                    if (three)
                    {
                        var w = r.Var<string>("w");
                        r.When(a.Of(x, y), b.Of(y, z), c.Of(z, w));
                        r.Then(head.Of(x, w));
                    }
                    else
                    {
                        r.When(a.Of(x, y), b.Of(y, z));
                        r.Then(head.Of(x, z));
                    }
                });
            }

            graph.Root.Freeze();
            var first = graph.Root.CreateChild();
            var second = graph.Root.CreateChild();
            Declare(first, relations);
            Declare(second, relations);

            var heap0 = CollectedHeap();
            var allocated0 = GC.GetAllocatedBytesForCurrentThread();
            first.Freeze();
            var allocated1 = GC.GetAllocatedBytesForCurrentThread();
            var heap1 = CollectedHeap();
            var allocated2 = GC.GetAllocatedBytesForCurrentThread();
            second.Freeze();
            var allocated3 = GC.GetAllocatedBytesForCurrentThread();
            var heap2 = CollectedHeap();
            GC.KeepAlive(graph);
            GC.KeepAlive(first);
            GC.KeepAlive(second);
            return (allocated1 - allocated0, allocated3 - allocated2, heap1 - heap0, heap2 - heap1);
        }

        /// <summary>
        /// Declares <see cref="FactsPerRelation"/> facts in every relation of the theory in <paramref name="scope"/>.
        /// </summary>
        static void Declare(LibraryScope scope, Relation<string, string>[] relations)
        {
            foreach (var relation in relations)
                scope.Assume(Enumerable.Range(0, FactsPerRelation).Select(j => relation.Of($"p{j}", $"p{j + 1}")).ToArray());
        }

        /// <summary>
        /// Collects twice, forced, blocking and compacting (the large object heap included), and gets the managed heap's
        /// size.
        /// </summary>
        static long CollectedHeap()
        {
            for (var i = 0; i < 2; i++)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            return GC.GetTotalMemory(false);
        }

        static string Kib(double bytes) => (bytes / 1024).ToString("N1", CultureInfo.InvariantCulture) + " KB";

    }

}
