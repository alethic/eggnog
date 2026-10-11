using System;
using System.Diagnostics;
using System.IO;
using System.Text;

using static Eggnog.Relations.RuleTerm;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The program of egglog-bridge's <c>examples/math.rs</c>: the math benchmark of the egg and egglog suites
    /// (<c>examples/math.egg</c>) written against the bridge API, with 13 constructors, 24 rules and seven seed
    /// expressions, run for at most <see cref="N"/> iterations on an e-graph with the default parallelism.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor is the part of the Rust <c>main</c> between <c>let start = Instant::now()</c> and the iteration
    /// loop, in the same order: <c>EGraph::default()</c> (here <see cref="Eggnog.Relations.EGraph.EGraph()"/>, which
    /// uses every processor as rayon's global pool does), the <c>Rational64</c> and <c>&amp;'static str</c> base types
    /// (here <see cref="Rational"/> through <see cref="InternedCodec{T}"/> and <see cref="StringCodec"/>), the 13 tables
    /// (each a constructor: <see cref="DefaultVal.FreshId"/>, <see cref="MergeFn.UnionId"/>, no subsumption), the
    /// constants <c>0</c>, <c>1</c>, <c>-1</c> and <c>2</c>, the rules (<c>define_rule!</c>, here
    /// <see cref="ExpressionBuilder.DefineRule"/>), and the seed terms (<c>add_expressions!</c>, here
    /// <see cref="ExpressionBuilder.AddExpressions"/>, one <see cref="EGraph.AddTerm"/> and flush per call, innermost
    /// first). <see cref="Run"/> is the loop and <see cref="WriteTableSizes"/> the size report that follows it.
    /// </para>
    /// <para>
    /// With <see cref="N"/> = 12 egglog's run never saturates and ends with the sizes in <see cref="ExpectedSizes"/>
    /// (printed by <c>math.exe</c>, built from egglog at the commit in <c>ext/egglog</c>); <see cref="Verify"/> checks
    /// them. The run builds about 16 million rows (11.4 million in <c>add</c>), so one program takes seconds and
    /// gigabytes.
    /// </para>
    /// </remarks>
    internal sealed class BridgeMathProgram
    {

        /// <summary>
        /// The maximum number of iterations: <c>math.rs</c>'s <c>const N: usize = 12</c>.
        /// </summary>
        public const int N = 12;

        /// <summary>
        /// The function names in the order <c>math.rs</c> declares the tables and prints their sizes.
        /// </summary>
        static readonly string[] functionNames = ["diff", "integral", "add", "sub", "mul", "div", "pow", "ln", "sqrt", "sin", "cos", "rat", "var"];

        /// <summary>
        /// The size of each table after the run, in the order of <see cref="functionNames"/>: what <c>math.exe</c>
        /// prints (both repetitions agree).
        /// </summary>
        static readonly int[] expectedSizes = [100_395, 224_150, 11_396_002, 78_586, 4_188_378, 3, 2, 1, 1, 1, 1, 5, 3];

        readonly EGraph egraph;
        readonly FunctionId[] functions;
        readonly EGraphRuleId[] rules;

        /// <summary>
        /// Builds the e-graph, its tables and rules, and adds the seed expressions, as <c>math.rs</c> does before its
        /// iteration loop. Part of the time <c>math.rs</c> measures.
        /// </summary>
        public BridgeMathProgram()
        {
            egraph = new EGraph();
            var rationalTy = ColumnTy.Base(egraph.BaseValues.RegisterType<InternedCodec<Rational>, Rational>());
            var stringTy = ColumnTy.Base(egraph.BaseValues.RegisterType<StringCodec, string>());

            // tables
            var diff = AddConstructor(egraph, "diff", ColumnTy.Id, ColumnTy.Id, ColumnTy.Id);
            var integral = AddConstructor(egraph, "integral", ColumnTy.Id, ColumnTy.Id, ColumnTy.Id);
            var add = AddConstructor(egraph, "add", ColumnTy.Id, ColumnTy.Id, ColumnTy.Id);
            var sub = AddConstructor(egraph, "sub", ColumnTy.Id, ColumnTy.Id, ColumnTy.Id);
            var mul = AddConstructor(egraph, "mul", ColumnTy.Id, ColumnTy.Id, ColumnTy.Id);
            var div = AddConstructor(egraph, "div", ColumnTy.Id, ColumnTy.Id, ColumnTy.Id);
            var pow = AddConstructor(egraph, "pow", ColumnTy.Id, ColumnTy.Id, ColumnTy.Id);
            var ln = AddConstructor(egraph, "ln", ColumnTy.Id, ColumnTy.Id);
            var sqrt = AddConstructor(egraph, "sqrt", ColumnTy.Id, ColumnTy.Id);
            var sin = AddConstructor(egraph, "sin", ColumnTy.Id, ColumnTy.Id);
            var cos = AddConstructor(egraph, "cos", ColumnTy.Id, ColumnTy.Id);
            var rat = AddConstructor(egraph, "rat", rationalTy, ColumnTy.Id);
            var varFn = AddConstructor(egraph, "var", stringTy, ColumnTy.Id);
            functions = [diff, integral, add, sub, mul, div, pow, ln, sqrt, sin, cos, rat, varFn];

            var zero = egraph.BaseValueConstant<InternedCodec<Rational>, Rational>(new Rational(0, 1));
            var one = egraph.BaseValueConstant<InternedCodec<Rational>, Rational>(new Rational(1, 1));
            var neg1 = egraph.BaseValueConstant<InternedCodec<Rational>, Rational>(new Rational(-1, 1));
            var two = egraph.BaseValueConstant<InternedCodec<Rational>, Rational>(new Rational(2, 1));

            EGraphRuleId Rule(RuleFact[] lhs, params RuleCommand[] rhs) => ExpressionBuilder.DefineRule(egraph, lhs, rhs);

            rules =
            [
                // ((-> (add x y) id)) => ((set (add y x) id))
                Rule([Call(add, "x", "y").Is("id")], RuleCommand.Set(Call(add, "y", "x"), "id")),
                // ((-> (mul x y) id)) => ((set (mul y x) id))
                Rule([Call(mul, "x", "y").Is("id")], RuleCommand.Set(Call(mul, "y", "x"), "id")),
                // ((-> (add x (add y z)) id)) => ((set (add (add x y) z) id))
                Rule([Call(add, "x", Call(add, "y", "z")).Is("id")], RuleCommand.Set(Call(add, Call(add, "x", "y"), "z"), "id")),
                // ((-> (mul x (mul y z)) id)) => ((set (mul (mul x y) z) id))
                Rule([Call(mul, "x", Call(mul, "y", "z")).Is("id")], RuleCommand.Set(Call(mul, Call(mul, "x", "y"), "z"), "id")),
                // ((-> (sub x y) id)) => ((set (add x (mul (rat {neg1}) y)) id))
                Rule([Call(sub, "x", "y").Is("id")], RuleCommand.Set(Call(add, "x", Call(mul, Call(rat, neg1), "y")), "id")),
                // ((-> (add a (rat {zero})) id)) => ((union a id))
                Rule([Call(add, "a", Call(rat, zero)).Is("id")], RuleCommand.Union("a", "id")),
                // ((-> (rat {zero}) z_id) (-> (mul a z_id) id)) => ((union id z_id))
                Rule([Call(rat, zero).Is("z_id"), Call(mul, "a", "z_id").Is("id")], RuleCommand.Union("id", "z_id")),
                // ((-> (mul a (rat {one})) id)) => ((union a id))
                Rule([Call(mul, "a", Call(rat, one)).Is("id")], RuleCommand.Union("a", "id")),
                // ((-> (sub x x) id)) => ((union id (rat {zero})))
                Rule([Call(sub, "x", "x").Is("id")], RuleCommand.Union("id", Call(rat, zero))),
                // ((-> (mul x (add b c)) id)) => ((set (add (mul x b) (mul x c)) id))
                Rule([Call(mul, "x", Call(add, "b", "c")).Is("id")], RuleCommand.Set(Call(add, Call(mul, "x", "b"), Call(mul, "x", "c")), "id")),
                // ((-> (add (mul x a) (mul x b)) id)) => ((set (mul x (add a b)) id))
                Rule([Call(add, Call(mul, "x", "a"), Call(mul, "x", "b")).Is("id")], RuleCommand.Set(Call(mul, "x", Call(add, "a", "b")), "id")),
                // ((-> (mul (pow a b) (pow a c)) id)) => ((set (pow a (add b c)) id))
                Rule([Call(mul, Call(pow, "a", "b"), Call(pow, "a", "c")).Is("id")], RuleCommand.Set(Call(pow, "a", Call(add, "b", "c")), "id")),
                // ((-> (pow x (rat {one})) id)) => ((union x id))
                Rule([Call(pow, "x", Call(rat, one)).Is("id")], RuleCommand.Union("x", "id")),
                // ((-> (pow x (rat {two})) id)) => ((set (mul x x) id))
                Rule([Call(pow, "x", Call(rat, two)).Is("id")], RuleCommand.Set(Call(mul, "x", "x"), "id")),
                // ((-> (diff x (add a b)) id)) => ((set (add (diff x a) (diff x b)) id))
                Rule([Call(diff, "x", Call(add, "a", "b")).Is("id")], RuleCommand.Set(Call(add, Call(diff, "x", "a"), Call(diff, "x", "b")), "id")),
                // ((-> (diff x (mul a b)) id)) => ((set (add (mul a (diff x b)) (mul b (diff x a))) id))
                Rule([Call(diff, "x", Call(mul, "a", "b")).Is("id")], RuleCommand.Set(Call(add, Call(mul, "a", Call(diff, "x", "b")), Call(mul, "b", Call(diff, "x", "a"))), "id")),
                // ((-> (diff x (sin x)) id)) => ((set (cos x) id))
                Rule([Call(diff, "x", Call(sin, "x")).Is("id")], RuleCommand.Set(Call(cos, "x"), "id")),
                // ((-> (diff x (cos x)) id)) => ((set (mul (rat {neg1}) (sin x)) id))
                Rule([Call(diff, "x", Call(cos, "x")).Is("id")], RuleCommand.Set(Call(mul, Call(rat, neg1), Call(sin, "x")), "id")),
                // ((-> (integral (rat {one}) x) id)) => ((union id x))
                Rule([Call(integral, Call(rat, one), "x").Is("id")], RuleCommand.Union("id", "x")),
                // ((-> (integral (cos x) x) id)) => ((set (sin x) id))
                Rule([Call(integral, Call(cos, "x"), "x").Is("id")], RuleCommand.Set(Call(sin, "x"), "id")),
                // ((-> (integral (sin x) x) id)) => ((set (mul (rat {neg1}) (cos x)) id))
                Rule([Call(integral, Call(sin, "x"), "x").Is("id")], RuleCommand.Set(Call(mul, Call(rat, neg1), Call(cos, "x")), "id")),
                // ((-> (integral (add f g) x) id)) => ((set (add (integral f x) (integral g x)) id))
                Rule([Call(integral, Call(add, "f", "g"), "x").Is("id")], RuleCommand.Set(Call(add, Call(integral, "f", "x"), Call(integral, "g", "x")), "id")),
                // ((-> (integral (sub f g) x) id)) => ((set (sub (integral f x) (integral g x)) id))
                Rule([Call(integral, Call(sub, "f", "g"), "x").Is("id")], RuleCommand.Set(Call(sub, Call(integral, "f", "x"), Call(integral, "g", "x")), "id")),
                // ((-> (integral (mul a b) x) id))
                // => ((set (sub (mul a (integral b x)) (integral (mul (diff x a) (integral b x)) x)) id))
                Rule([Call(integral, Call(mul, "a", "b"), "x").Is("id")],
                    RuleCommand.Set(Call(sub, Call(mul, "a", Call(integral, "b", "x")), Call(integral, Call(mul, Call(diff, "x", "a"), Call(integral, "b", "x")), "x")), "id")),
            ];

            {
                RuleTerm R(long n) => RuleEntry.Const(egraph.BaseValues.Get<InternedCodec<Rational>, Rational>(new Rational(n, 1)), rationalTy);
                RuleTerm S(string s) => RuleEntry.Const(egraph.BaseValues.Get<StringCodec, string>(s), stringTy);

                // In math.rs's order: the rationals 1, 2, 3 and 7, then the strings "x", "y" and "five".
                var oneValue = R(1);
                var twoValue = R(2);
                var three = R(3);
                var seven = R(7);
                var xStr = S("x");
                var yStr = S("y");
                var fiveStr = S("five");
                ExpressionBuilder.AddExpressions(egraph,
                    // (integral (ln (var x_str)) (var x_str))
                    Call(integral, Call(ln, Call(varFn, xStr)), Call(varFn, xStr)),
                    // (integral (add (var x_str) (cos (var x_str))) (var x_str))
                    Call(integral, Call(add, Call(varFn, xStr), Call(cos, Call(varFn, xStr))), Call(varFn, xStr)),
                    // (integral (mul (cos (var x_str)) (var x_str)) (var x_str))
                    Call(integral, Call(mul, Call(cos, Call(varFn, xStr)), Call(varFn, xStr)), Call(varFn, xStr)),
                    // (diff (var x_str) (add (rat one) (mul (rat two) (var x_str))))
                    Call(diff, Call(varFn, xStr), Call(add, Call(rat, oneValue), Call(mul, Call(rat, twoValue), Call(varFn, xStr)))),
                    // (diff (var x_str) (sub (pow (var x_str) (rat three)) (mul (rat seven) (pow (var x_str) (rat two)))))
                    Call(diff, Call(varFn, xStr), Call(sub, Call(pow, Call(varFn, xStr), Call(rat, three)), Call(mul, Call(rat, seven), Call(pow, Call(varFn, xStr), Call(rat, twoValue))))),
                    // (add (mul (var y_str) (add (var x_str) (var y_str))) (sub (add (var x_str) (rat two)) (add (var x_str) (var x_str))))
                    Call(add, Call(mul, Call(varFn, yStr), Call(add, Call(varFn, xStr), Call(varFn, yStr))), Call(sub, Call(add, Call(varFn, xStr), Call(rat, twoValue)), Call(add, Call(varFn, xStr), Call(varFn, xStr)))),
                    // (div (rat one) (sub (div (add (rat one) (sqrt (var five_str))) (rat two)) (div (sub (rat one) (sqrt (var five_str))) (rat two))))
                    Call(div, Call(rat, oneValue), Call(sub, Call(div, Call(add, Call(rat, oneValue), Call(sqrt, Call(varFn, fiveStr))), Call(rat, twoValue)), Call(div, Call(sub, Call(rat, oneValue), Call(sqrt, Call(varFn, fiveStr))), Call(rat, twoValue)))));
            }
        }

        /// <summary>
        /// Gets the function names in the order <c>math.rs</c> prints their sizes.
        /// </summary>
        public static ReadOnlySpan<string> FunctionNames => functionNames;

        /// <summary>
        /// Gets the table sizes <c>math.exe</c> prints after its run, in the order of <see cref="FunctionNames"/>.
        /// </summary>
        public static ReadOnlySpan<int> ExpectedSizes => expectedSizes;

        /// <summary>
        /// Gets the e-graph.
        /// </summary>
        public EGraph EGraph => egraph;

        /// <summary>
        /// Runs the rules for up to <see cref="N"/> iterations, stopping after the first that changes nothing: the loop
        /// of <c>math.rs</c>. With <paramref name="log"/>, writes <c>math.rs</c>'s line after each iteration
        /// (<c>Iteration {i} finished, duration={:?} (saturated={})</c>).
        /// </summary>
        /// <param name="log">Where to write the iteration lines; <c>null</c> for none.</param>
        /// <returns>The iterations run, and whether the last one changed nothing.</returns>
        public (int Iterations, bool Saturated) Run(TextWriter? log)
        {
            for (var i = 0; i < N; i++)
            {
                var start = Stopwatch.GetTimestamp();
                var changed = egraph.RunRules(rules).Changed;
                if (log != null)
                    log.WriteLine($"Iteration {i} finished, duration={RustDuration.Format(RustDuration.ElapsedNanoseconds(start))} (saturated={(changed ? "false" : "true")})");

                if (changed == false)
                    return (i + 1, true);
            }

            return (N, false);
        }

        /// <summary>
        /// Writes <c>math.rs</c>'s size lines, <c>Function {name} has size {n}</c>, one per table in declaration order.
        /// </summary>
        /// <param name="log">The writer.</param>
        public void WriteTableSizes(TextWriter log)
        {
            ArgumentNullException.ThrowIfNull(log);
            for (var i = 0; i < functions.Length; i++)
                log.WriteLine($"Function {functionNames[i]} has size {egraph.TableSize(functions[i])}");
        }

        /// <summary>
        /// Checks the run against <c>math.exe</c>'s: all <see cref="N"/> iterations ran without saturating, and every
        /// table has the size egglog prints. O(1) per table.
        /// </summary>
        /// <param name="iterations">The iterations <see cref="Run"/> reported.</param>
        /// <param name="saturated">Whether <see cref="Run"/> reported saturation.</param>
        /// <exception cref="InvalidOperationException">The run differs from egglog's.</exception>
        public void Verify(int iterations, bool saturated)
        {
            var errors = new StringBuilder();
            if (iterations != N || saturated)
                errors.Append($" ran {iterations} iterations (saturated={(saturated ? "true" : "false")}), egglog runs {N} without saturating;");

            for (var i = 0; i < functions.Length; i++)
            {
                var size = egraph.TableSize(functions[i]);
                if (size != expectedSizes[i])
                    errors.Append($" {functionNames[i]} has {size} rows, egglog has {expectedSizes[i]};");
            }

            if (errors.Length > 0)
                throw new InvalidOperationException("The math program's result differs from egglog's:" + errors.ToString().TrimEnd(';'));
        }

        /// <summary>
        /// Declares a constructor of <c>math.rs</c>: the given schema (keys, then the id output),
        /// <see cref="DefaultVal.FreshId"/>, <see cref="MergeFn.UnionId"/>, no subsumption.
        /// </summary>
        static FunctionId AddConstructor(EGraph egraph, string name, params ColumnTy[] schema)
        {
            return egraph.AddTable(new FunctionConfig
            {
                Schema = schema,
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = name,
                CanSubsume = false,
            });
        }

    }

}
