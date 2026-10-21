using System;

namespace Eggnog.AotCheck
{

    /// <summary>
    /// The entry point: runs the two examples of the README and checks what they print, so that a NativeAOT build of
    /// the library that computes something else fails by its exit code.
    /// </summary>
    internal static class Program
    {

        /// <summary>
        /// Runs the examples.
        /// </summary>
        /// <returns>0 when every example gives the README's result; 1 otherwise.</returns>
        static int Main()
        {
            var failed = 0;
            failed += Check("quick start", QuickStart(), "same=True best=(Add (Var \"x\") (Num 5))");
            failed += Check("scopes and case splits", ScopesAndCaseSplits(), "mustStop=True mayGo=True atRoot=False");
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// Prints an example's result and compares it with the expected one.
        /// </summary>
        /// <param name="name">The example's name.</param>
        /// <param name="actual">What the example gave.</param>
        /// <param name="expected">What the README says it gives.</param>
        /// <returns>0 when the two agree; 1 otherwise.</returns>
        static int Check(string name, string actual, string expected)
        {
            if (actual == expected)
            {
                Console.WriteLine($"{name}: {actual}");
                return 0;
            }

            Console.WriteLine($"{name}: {actual} (expected {expected})");
            return 1;
        }

        /// <summary>
        /// The quick start: rewrite rules over arithmetic terms, saturation, and extraction of the cheapest equivalent
        /// term.
        /// </summary>
        /// <returns>Whether the two terms are equal, and the extracted term.</returns>
        static string QuickStart()
        {
            var graph = new EGraph();
            graph.DefineSort<Expr>("Expr");
            var num = graph.DefineConstructor<long, Expr>("Num");
            var name = graph.DefineConstructor<string, Expr>("Var");
            var add = graph.DefineConstructor<Expr, Expr, Expr>("Add");
            var mul = graph.DefineConstructor<Expr, Expr, Expr>("Mul");

            var rules = graph.DefineRuleset("arith");
            rules.DefineRule("commute add", r =>
            {
                var (a, b) = r.Vars<Expr, Expr>("a", "b");
                r.Rewrite(add.Of(a, b), add.Of(b, a));
            });
            rules.DefineRule("times one", r =>
            {
                var a = r.Var<Expr>("a");
                r.Rewrite(mul.Of(a, num.Of(1L)), a);
            });
            rules.DefineRule("fold add", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.Rewrite(add.Of(num.Of(x), num.Of(y)), num.Of(Primitives.Add.Of(x, y)));
            });

            var root = graph.Root;
            Expr e = root.Make(add.Of(mul.Of(name.Of("x"), num.Of(1L)), add.Of(num.Of(2L), num.Of(3L))));
            root.Run(Schedule.Saturate(rules));

            bool same = root.AreEqual(e, add.Of(num.Of(5L), name.Of("x")));
            ExtractedTerm best = root.Extract(e);
            return $"same={same} best={best}";
        }

        /// <summary>
        /// The repository README's second example: a frozen base shared by queries, a query scope per question, and a
        /// case split on a domain whose every case proves the answer, which the lift brings into the query.
        /// </summary>
        /// <returns>The three answers the README states.</returns>
        static string ScopesAndCaseSplits()
        {
            var graph = new EGraph();
            var signal = graph.DefineRelation<string>("signal");
            var color = graph.DefineFunction<string, Set<string>>("color", ValueLattice.Intersect<string>());
            var stop = graph.DefineRelation<string>("stop");

            var rules = graph.DefineRuleset("traffic");
            foreach (var c in new[] { "red", "amber" })
            {
                rules.DefineRule($"{c} means stop", r =>
                {
                    var s = r.Var<string>("s");
                    r.When(signal.Of(s), color.Of(s).Is(Set.Of(c)));
                    r.Then(stop.Of(s));
                });
            }

            var byColor = graph.DefineDomainSplit("by color", color, [signal]);
            var schedule = Schedule.Saturate(rules, Schedule.Split(byColor, rules));

            graph.Root.Assume(signal.Of("north"));
            graph.Root.Freeze();

            using var q1 = graph.Root.CreateChild();
            q1.Assume(color.Of("north").Is(Set.Of("red", "amber")));
            q1.Run(schedule);
            bool mustStop = q1.Check(stop.Of("north"));

            using var q2 = graph.Root.CreateChild();
            q2.Assume(color.Of("north").Is(Set.Of("red", "green")));
            q2.Run(schedule);
            bool mayGo = q2.Check(stop.Of("north")) == false;

            bool atRoot = graph.Root.Check(stop.Of("north"));
            return $"mustStop={mustStop} mayGo={mayGo} atRoot={atRoot}";
        }

    }

}
