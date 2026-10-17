using System;
using System.IO;
using System.Text;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// egglog's <c>tests/math-microbenchmark.egg</c> written against the library's public API: the datatype
    /// <c>Math</c> with its 13 constructors, its 24 rewrites, its seven seed expressions and <c>(run 11)</c>, on a graph
    /// with a given degree of parallelism (<see cref="EGraphOptions.DegreeOfParallelism"/>, egglog's <c>-j</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor declares the theory and adds the seed expressions in the file's order; <see cref="Run"/> is the
    /// file's <c>(run 11)</c>, <see cref="Schedule.Repeat"/> of 11 passes of the one ruleset, which, as egglog's
    /// <c>run</c>, stops early after a pass that changes nothing. <see cref="WriteSizes"/> prints the table sizes as the
    /// file's <c>(print-size)</c> does, and <see cref="Verify"/> checks them against <see cref="ExpectedSizes"/>.
    /// </para>
    /// <para>
    /// The expected sizes are what <c>egglog -j 1 tests/math-microbenchmark.egg</c> prints, built with
    /// <c>cargo +1.91.0 build --release</c> from egglog at the commit in <c>ext/egglog</c>; <c>-j 8</c> prints the same.
    /// egglog's rewrite <c>(rewrite lhs rhs)</c> is <see cref="RuleBuilder.Rewrite"/>: a match of the left side
    /// unions its class with the right side's. The run makes all eleven passes; the last still changes the graph.
    /// </para>
    /// </remarks>
    internal sealed class MathMicrobenchmarkProgram
    {

        /// <summary>
        /// The most passes of the ruleset: the file's <c>(run 11)</c>.
        /// </summary>
        public const int Iterations = 11;

        /// <summary>
        /// The constructor names in the order egglog's <c>(print-size)</c> lists them (by name).
        /// </summary>
        static readonly string[] tableNames = ["Add", "Const", "Cos", "Diff", "Div", "Integral", "Ln", "Mul", "Pow", "Sin", "Sqrt", "Sub", "Var"];

        /// <summary>
        /// The size of each table after the run, in the order of <see cref="tableNames"/>: what egglog prints.
        /// </summary>
        static readonly long[] expectedSizes = [641_743, 5, 1, 13_504, 3, 32_434, 1, 345_075, 2, 1, 1, 15_123, 3];

        readonly EGraph graph;
        readonly Ruleset rules;
        readonly TableHandle[] tables;

        /// <summary>
        /// Declares the theory and adds the seed expressions, as the file does before its <c>(run 11)</c>.
        /// </summary>
        /// <param name="degreeOfParallelism">The graph's degree of parallelism: egglog's <c>-j</c>.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeOfParallelism"/> is less than 1.</exception>
        public MathMicrobenchmarkProgram(int degreeOfParallelism)
        {
            graph = new EGraph(new EGraphOptions { DegreeOfParallelism = degreeOfParallelism });
            graph.DefineSort<MathExpr>("Math");
            var diff = graph.DefineConstructor<MathExpr, MathExpr, MathExpr>("Diff");
            var integral = graph.DefineConstructor<MathExpr, MathExpr, MathExpr>("Integral");
            var add = graph.DefineConstructor<MathExpr, MathExpr, MathExpr>("Add");
            var sub = graph.DefineConstructor<MathExpr, MathExpr, MathExpr>("Sub");
            var mul = graph.DefineConstructor<MathExpr, MathExpr, MathExpr>("Mul");
            var div = graph.DefineConstructor<MathExpr, MathExpr, MathExpr>("Div");
            var pow = graph.DefineConstructor<MathExpr, MathExpr, MathExpr>("Pow");
            var ln = graph.DefineConstructor<MathExpr, MathExpr>("Ln");
            var sqrt = graph.DefineConstructor<MathExpr, MathExpr>("Sqrt");
            var sin = graph.DefineConstructor<MathExpr, MathExpr>("Sin");
            var cos = graph.DefineConstructor<MathExpr, MathExpr>("Cos");
            var constant = graph.DefineConstructor<long, MathExpr>("Const");
            var variable = graph.DefineConstructor<string, MathExpr>("Var");
            tables = [add, constant, cos, diff, div, integral, ln, mul, pow, sin, sqrt, sub, variable];

            rules = graph.DefineRuleset("math");
            var count = 0;
            void Rewrite(Func<RuleBuilder, (Term<MathExpr> Left, Term<MathExpr> Right)> define) =>
                rules.DefineRule($"rewrite {count++}", r =>
                {
                    var (left, right) = define(r);
                    r.Rewrite(left, right);
                });

            Rewrite(r => { var (a, b) = r.Vars<MathExpr, MathExpr>("a", "b"); return (add.Of(a, b), add.Of(b, a)); });
            Rewrite(r => { var (a, b) = r.Vars<MathExpr, MathExpr>("a", "b"); return (mul.Of(a, b), mul.Of(b, a)); });
            Rewrite(r => { var (a, b, c) = r.Vars<MathExpr, MathExpr, MathExpr>("a", "b", "c"); return (add.Of(a, add.Of(b, c)), add.Of(add.Of(a, b), c)); });
            Rewrite(r => { var (a, b, c) = r.Vars<MathExpr, MathExpr, MathExpr>("a", "b", "c"); return (mul.Of(a, mul.Of(b, c)), mul.Of(mul.Of(a, b), c)); });

            Rewrite(r => { var (a, b) = r.Vars<MathExpr, MathExpr>("a", "b"); return (sub.Of(a, b), add.Of(a, mul.Of(constant.Of(-1L), b))); });

            Rewrite(r => { var a = r.Var<MathExpr>("a"); return (add.Of(a, constant.Of(0L)), a); });
            Rewrite(r => { var a = r.Var<MathExpr>("a"); return (mul.Of(a, constant.Of(0L)), constant.Of(0L)); });
            Rewrite(r => { var a = r.Var<MathExpr>("a"); return (mul.Of(a, constant.Of(1L)), a); });

            Rewrite(r => { var a = r.Var<MathExpr>("a"); return (sub.Of(a, a), constant.Of(0L)); });

            Rewrite(r => { var (a, b, c) = r.Vars<MathExpr, MathExpr, MathExpr>("a", "b", "c"); return (mul.Of(a, add.Of(b, c)), add.Of(mul.Of(a, b), mul.Of(a, c))); });
            Rewrite(r => { var (a, b, c) = r.Vars<MathExpr, MathExpr, MathExpr>("a", "b", "c"); return (add.Of(mul.Of(a, b), mul.Of(a, c)), mul.Of(a, add.Of(b, c))); });

            Rewrite(r => { var (a, b, c) = r.Vars<MathExpr, MathExpr, MathExpr>("a", "b", "c"); return (mul.Of(pow.Of(a, b), pow.Of(a, c)), pow.Of(a, add.Of(b, c))); });
            Rewrite(r => { var x = r.Var<MathExpr>("x"); return (pow.Of(x, constant.Of(1L)), x); });
            Rewrite(r => { var x = r.Var<MathExpr>("x"); return (pow.Of(x, constant.Of(2L)), mul.Of(x, x)); });

            Rewrite(r => { var (x, a, b) = r.Vars<MathExpr, MathExpr, MathExpr>("x", "a", "b"); return (diff.Of(x, add.Of(a, b)), add.Of(diff.Of(x, a), diff.Of(x, b))); });
            Rewrite(r => { var (x, a, b) = r.Vars<MathExpr, MathExpr, MathExpr>("x", "a", "b"); return (diff.Of(x, mul.Of(a, b)), add.Of(mul.Of(a, diff.Of(x, b)), mul.Of(b, diff.Of(x, a)))); });

            Rewrite(r => { var x = r.Var<MathExpr>("x"); return (diff.Of(x, sin.Of(x)), cos.Of(x)); });
            Rewrite(r => { var x = r.Var<MathExpr>("x"); return (diff.Of(x, cos.Of(x)), mul.Of(constant.Of(-1L), sin.Of(x))); });

            Rewrite(r => { var x = r.Var<MathExpr>("x"); return (integral.Of(constant.Of(1L), x), x); });
            Rewrite(r => { var x = r.Var<MathExpr>("x"); return (integral.Of(cos.Of(x), x), sin.Of(x)); });
            Rewrite(r => { var x = r.Var<MathExpr>("x"); return (integral.Of(sin.Of(x), x), mul.Of(constant.Of(-1L), cos.Of(x))); });
            Rewrite(r => { var (f, g, x) = r.Vars<MathExpr, MathExpr, MathExpr>("f", "g", "x"); return (integral.Of(add.Of(f, g), x), add.Of(integral.Of(f, x), integral.Of(g, x))); });
            Rewrite(r => { var (f, g, x) = r.Vars<MathExpr, MathExpr, MathExpr>("f", "g", "x"); return (integral.Of(sub.Of(f, g), x), sub.Of(integral.Of(f, x), integral.Of(g, x))); });
            Rewrite(r =>
            {
                var (a, b, x) = r.Vars<MathExpr, MathExpr, MathExpr>("a", "b", "x");
                return (integral.Of(mul.Of(a, b), x), sub.Of(mul.Of(a, integral.Of(b, x)), integral.Of(mul.Of(diff.Of(x, a), integral.Of(b, x)), x)));
            });

            var root = graph.Root;
            var vx = variable.Of("x");
            var vy = variable.Of("y");
            var vfive = variable.Of("five");
            root.Make(integral.Of(ln.Of(vx), vx));
            root.Make(integral.Of(add.Of(vx, cos.Of(vx)), vx));
            root.Make(integral.Of(mul.Of(cos.Of(vx), vx), vx));
            root.Make(diff.Of(vx, add.Of(constant.Of(1L), mul.Of(constant.Of(2L), vx))));
            root.Make(diff.Of(vx, sub.Of(pow.Of(vx, constant.Of(3L)), mul.Of(constant.Of(7L), pow.Of(vx, constant.Of(2L))))));
            root.Make(add.Of(mul.Of(vy, add.Of(vx, vy)), sub.Of(add.Of(vx, constant.Of(2L)), add.Of(vx, vx))));
            root.Make(div.Of(constant.Of(1L),
                sub.Of(div.Of(add.Of(constant.Of(1L), sqrt.Of(vfive)), constant.Of(2L)),
                    div.Of(sub.Of(constant.Of(1L), sqrt.Of(vfive)), constant.Of(2L)))));
        }

        /// <summary>
        /// Gets the constructor names in the order <c>(print-size)</c> lists them.
        /// </summary>
        public static ReadOnlySpan<string> TableNames => tableNames;

        /// <summary>
        /// Gets the table sizes egglog prints after the run, in the order of <see cref="TableNames"/>.
        /// </summary>
        public static ReadOnlySpan<long> ExpectedSizes => expectedSizes;

        /// <summary>
        /// Gets the graph.
        /// </summary>
        public EGraph Graph => graph;

        /// <summary>
        /// Runs the file's <c>(run 11)</c> at the root.
        /// </summary>
        /// <returns>The run's report.</returns>
        public RunReport Run() => graph.Root.Run(Schedule.Repeat(Iterations, Schedule.Run(rules)));

        /// <summary>
        /// Gets the number of rows in the table named <paramref name="name"/> at the root.
        /// </summary>
        /// <param name="name">A name of <see cref="TableNames"/>.</param>
        /// <returns>The row count.</returns>
        /// <exception cref="ArgumentException">No constructor has that name.</exception>
        public long Size(string name)
        {
            var index = Array.IndexOf(tableNames, name);
            if (index < 0)
                throw new ArgumentException($"No constructor is named '{name}'.", nameof(name));

            return graph.Root.CountRows(tables[index]);
        }

        /// <summary>
        /// Writes the sizes as egglog's <c>(print-size)</c> prints them: one <c>(Name size)</c> per table, in name order,
        /// the whole list in parentheses.
        /// </summary>
        /// <param name="log">The writer.</param>
        public void WriteSizes(TextWriter log)
        {
            ArgumentNullException.ThrowIfNull(log);
            for (var i = 0; i < tableNames.Length; i++)
                log.WriteLine($"{(i == 0 ? "((" : " (")}{tableNames[i]} {graph.Root.CountRows(tables[i])}){(i == tableNames.Length - 1 ? ")" : "")}");
        }

        /// <summary>
        /// Checks the run against egglog's: all <see cref="Iterations"/> passes ran without saturating (the file's run
        /// never saturates; egglog does not print its pass count, but its sizes are those of the eleventh pass), and
        /// every table has the size egglog prints. O(1) per table.
        /// </summary>
        /// <param name="report">The report <see cref="Run"/> returned.</param>
        /// <exception cref="InvalidOperationException">The result differs from egglog's.</exception>
        public void Verify(RunReport report)
        {
            ArgumentNullException.ThrowIfNull(report);

            var errors = new StringBuilder();
            if (report.Iterations != Iterations || report.Saturated)
                errors.Append($" ran {report.Iterations} passes (saturated={(report.Saturated ? "true" : "false")}), egglog runs {Iterations} without saturating;");

            for (var i = 0; i < tableNames.Length; i++)
            {
                var size = graph.Root.CountRows(tables[i]);
                if (size != expectedSizes[i])
                    errors.Append($" {tableNames[i]} has {size} rows, egglog has {expectedSizes[i]};");
            }

            if (errors.Length > 0)
                throw new InvalidOperationException("The math microbenchmark's result differs from egglog's:" + errors.ToString().TrimEnd(';'));
        }

    }

}
