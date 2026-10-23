using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.RuleTerm;
using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The conformance suite of the bridge's rule layer: every test of egglog-bridge/src/tests.rs, ported with rules
    /// built through <see cref="EGraphRuleBuilder"/> and <see cref="ExpressionBuilder"/> (the port of the
    /// <c>define_rule!</c> and <c>add_expressions!</c> macros) and run through <see cref="EGraph.RunRules"/>.
    /// </summary>
    /// <remarks>
    /// <c>panic_functions_trigger_early_stop</c> (tests.rs:1480-1506) is
    /// <see cref="PanicTests.PanicFunctionsTriggerEarlyStop"/>; the compile-time <c>assert_send::&lt;EGraph&gt;</c>
    /// (tests.rs:1508-1511) has no C# counterpart, and <see cref="Math"/> runs two e-graphs on two threads as egglog
    /// does. Rust's <c>Rational64</c> is <see cref="Rational"/>, interned.
    /// </remarks>
    public class BridgeTests
    {

        /// <summary>
        /// egglog's <c>ac_test</c> (tests.rs:26-94): associativity and commutativity saturate two differently
        /// associated sums into one class. <paramref name="canSubsume"/> only adds a subsumption column to the tables.
        /// </summary>
        static void AcTest(bool canSubsume)
        {
            const int N = 5;
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var num = Constructor(egraph, "num", canSubsume, i64);
            var add = Constructor(egraph, "add", canSubsume, ColumnTy.Id, ColumnTy.Id);

            var addComm = ExpressionBuilder.DefineRule(egraph,
                [Call(add, "x", "y").Is("id")],
                [RuleCommand.Set(Call(add, "y", "x"), "id")]);
            var addAssoc = ExpressionBuilder.DefineRule(egraph,
                [Call(add, "x", Call(add, "y", "z")).Is("id")],
                [RuleCommand.Set(Call(add, Call(add, "x", "y"), "z"), "id")]);

            // Running these rules on an empty database changes nothing.
            Assert.False(egraph.RunRules(addComm, addAssoc).Changed);

            var ids = new List<Value>();
            for (var i = 0; i < N; i++)
                ids.Add(egraph.AddTerm(num, [Int(egraph, i)]));

            // (0 + ... + N) associated to the left and (N + ... + 0) to the right; saturated, they are equal.
            var prev = ids[0];
            foreach (var n in ids.Skip(1))
                prev = egraph.AddTerm(add, [n, prev]);

            var leftRoot = prev;
            prev = ids[^1];
            foreach (var n in ids.Take(N - 1))
                prev = egraph.AddTerm(add, [prev, n]);

            var rightRoot = prev;
            while (egraph.RunRules(addComm, addAssoc).Changed)
            {

            }

            Assert.Equal(egraph.GetCanonInUf(leftRoot), egraph.GetCanonInUf(rightRoot));
        }

        /// <summary>
        /// egglog's <c>ac</c> (tests.rs:96-99).
        /// </summary>
        [Fact]
        public void Ac() => AcTest(false);

        /// <summary>
        /// egglog's <c>ac_subsume</c> (tests.rs:101-104).
        /// </summary>
        [Fact]
        public void AcSubsume() => AcTest(true);

        /// <summary>
        /// egglog's <c>ac_fail</c> (tests.rs:106-181): commutativity restricted to a left operand of <c>num(1)</c>
        /// cannot reassociate the sums, and a constant in an atom constrains it.
        /// </summary>
        [Fact]
        public void AcFail()
        {
            const int N = 5;
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var one = egraph.BaseValueConstant<Int64Codec, long>(1);
            var num = Constructor(egraph, "num", false, i64);
            var add = Constructor(egraph, "add", false, ColumnTy.Id, ColumnTy.Id);

            var addComm = ExpressionBuilder.DefineRule(egraph,
                [Call(add, "x", "y").Is("id"), Call(num, one).Is("x")],
                [RuleCommand.Set(Call(add, "y", "x"), "id")]);
            var addAssoc = ExpressionBuilder.DefineRule(egraph,
                [Call(add, "x", Call(add, "y", "z")).Is("id")],
                [RuleCommand.Set(Call(add, Call(add, "x", "y"), "z"), "id")]);

            Assert.False(egraph.RunRules(addComm, addAssoc).Changed);

            var ids = new List<Value>();
            var numRows = new List<(FunctionId, Value[])>();
            for (var i = 0; i < N; i++)
            {
                var id = egraph.FreshId();
                ids.Add(id);
                numRows.Add((num, [Int(egraph, i), id]));
            }

            egraph.AddValues(numRows);

            var toAdd = new List<(FunctionId, Value[])>();
            var prev = ids[0];
            foreach (var n in ids.Skip(1))
            {
                var id = egraph.FreshId();
                toAdd.Add((add, [n, prev, id]));
                prev = id;
            }

            var leftRoot = toAdd[^1].Item2[2];
            prev = ids[^1];
            foreach (var n in ids.Take(N - 1))
            {
                var id = egraph.FreshId();
                toAdd.Add((add, [prev, n, id]));
                prev = id;
            }

            var rightRoot = toAdd[^1].Item2[2];
            egraph.AddValues(toAdd);

            while (egraph.RunRules(addComm, addAssoc).Changed)
            {

            }

            Assert.NotEqual(egraph.GetCanonInUf(leftRoot), egraph.GetCanonInUf(rightRoot));
        }

        /// <summary>
        /// egglog's <c>math</c> (tests.rs:183-188): <see cref="MathTest"/> on two threads at once, each with its own
        /// e-graph with the default parallelism.
        /// </summary>
        [Fact]
        public async Task Math()
        {
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Factory.StartNew(() => MathTest(new EGraph(), false), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// egglog's <c>math_subsume</c> (tests.rs:190-195).
        /// </summary>
        [Fact]
        public async Task MathSubsume()
        {
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Factory.StartNew(() => MathTest(new EGraph(), true), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// <see cref="MathTest"/> on a serial e-graph: serial runs are deterministic, and golden numbers come from
        /// them.
        /// </summary>
        [Fact]
        public void MathSerial() => MathTest(NewEGraph(), false);

        /// <summary>
        /// egglog's <c>math_test</c> (tests.rs:197-447): the math benchmark of the egg and egglog suites, eight
        /// iterations of 24 rules over seven expressions; every table must end with exactly the number of rows egglog's
        /// original implementation produces. <paramref name="canSubsume"/> only adds a subsumption column to the
        /// tables.
        /// </summary>
        static void MathTest(EGraph egraph, bool canSubsume)
        {
            const int N = 8;
            var rationalType = ColumnTy.Base(egraph.BaseValues.RegisterType<InternedCodec<Rational>, Rational>());
            var stringType = ColumnTy.Base(egraph.BaseValues.RegisterType<StringCodec, string>());

            FunctionId Ctor(string name, params ColumnTy[] keys) => Constructor(egraph, name, canSubsume, keys);

            var diff = Ctor("diff", ColumnTy.Id, ColumnTy.Id);
            var integral = Ctor("integral", ColumnTy.Id, ColumnTy.Id);
            var add = Ctor("add", ColumnTy.Id, ColumnTy.Id);
            var sub = Ctor("sub", ColumnTy.Id, ColumnTy.Id);
            var mul = Ctor("mul", ColumnTy.Id, ColumnTy.Id);
            var div = Ctor("div", ColumnTy.Id, ColumnTy.Id);
            var pow = Ctor("pow", ColumnTy.Id, ColumnTy.Id);
            var ln = Ctor("ln", ColumnTy.Id);
            var sqrt = Ctor("sqrt", ColumnTy.Id);
            var sin = Ctor("sin", ColumnTy.Id);
            var cos = Ctor("cos", ColumnTy.Id);
            var rat = Ctor("rat", rationalType);
            var varFn = Ctor("var", stringType);

            var zero = egraph.BaseValueConstant<InternedCodec<Rational>, Rational>(new Rational(0, 1));
            var one = egraph.BaseValueConstant<InternedCodec<Rational>, Rational>(new Rational(1, 1));
            var neg1 = egraph.BaseValueConstant<InternedCodec<Rational>, Rational>(new Rational(-1, 1));
            var two = egraph.BaseValueConstant<InternedCodec<Rational>, Rational>(new Rational(2, 1));

            EGraphRuleId Rule(RuleFact[] lhs, params RuleCommand[] rhs) => ExpressionBuilder.DefineRule(egraph, lhs, rhs);

            EGraphRuleId[] rules =
            [
                Rule([Call(add, "x", "y").Is("id")], RuleCommand.Set(Call(add, "y", "x"), "id")),
                Rule([Call(mul, "x", "y").Is("id")], RuleCommand.Set(Call(mul, "y", "x"), "id")),
                Rule([Call(add, "x", Call(add, "y", "z")).Is("id")], RuleCommand.Set(Call(add, Call(add, "x", "y"), "z"), "id")),
                Rule([Call(mul, "x", Call(mul, "y", "z")).Is("id")], RuleCommand.Set(Call(mul, Call(mul, "x", "y"), "z"), "id")),
                Rule([Call(sub, "x", "y").Is("id")], RuleCommand.Set(Call(add, "x", Call(mul, Call(rat, neg1), "y")), "id")),
                Rule([Call(add, "a", Call(rat, zero)).Is("id")], RuleCommand.Union("a", "id")),
                Rule([Call(rat, zero).Is("z_id"), Call(mul, "a", "z_id").Is("id")], RuleCommand.Union("id", "z_id")),
                Rule([Call(mul, "a", Call(rat, one)).Is("id")], RuleCommand.Union("a", "id")),
                Rule([Call(sub, "x", "x").Is("id")], RuleCommand.Union("id", Call(rat, zero))),
                Rule([Call(mul, "x", Call(add, "b", "c")).Is("id")], RuleCommand.Set(Call(add, Call(mul, "x", "b"), Call(mul, "x", "c")), "id")),
                Rule([Call(add, Call(mul, "x", "a"), Call(mul, "x", "b")).Is("id")], RuleCommand.Set(Call(mul, "x", Call(add, "a", "b")), "id")),
                Rule([Call(mul, Call(pow, "a", "b"), Call(pow, "a", "c")).Is("id")], RuleCommand.Set(Call(pow, "a", Call(add, "b", "c")), "id")),
                Rule([Call(pow, "x", Call(rat, one)).Is("id")], RuleCommand.Union("x", "id")),
                Rule([Call(pow, "x", Call(rat, two)).Is("id")], RuleCommand.Set(Call(mul, "x", "x"), "id")),
                Rule([Call(diff, "x", Call(add, "a", "b")).Is("id")], RuleCommand.Set(Call(add, Call(diff, "x", "a"), Call(diff, "x", "b")), "id")),
                Rule([Call(diff, "x", Call(mul, "a", "b")).Is("id")], RuleCommand.Set(Call(add, Call(mul, "a", Call(diff, "x", "b")), Call(mul, "b", Call(diff, "x", "a"))), "id")),
                Rule([Call(diff, "x", Call(sin, "x")).Is("id")], RuleCommand.Set(Call(cos, "x"), "id")),
                Rule([Call(diff, "x", Call(cos, "x")).Is("id")], RuleCommand.Set(Call(mul, Call(rat, neg1), Call(sin, "x")), "id")),
                Rule([Call(integral, Call(rat, one), "x").Is("id")], RuleCommand.Union("id", "x")),
                Rule([Call(integral, Call(cos, "x"), "x").Is("id")], RuleCommand.Set(Call(sin, "x"), "id")),
                Rule([Call(integral, Call(sin, "x"), "x").Is("id")], RuleCommand.Set(Call(mul, Call(rat, neg1), Call(cos, "x")), "id")),
                Rule([Call(integral, Call(add, "f", "g"), "x").Is("id")], RuleCommand.Set(Call(add, Call(integral, "f", "x"), Call(integral, "g", "x")), "id")),
                Rule([Call(integral, Call(sub, "f", "g"), "x").Is("id")], RuleCommand.Set(Call(sub, Call(integral, "f", "x"), Call(integral, "g", "x")), "id")),
                Rule([Call(integral, Call(mul, "a", "b"), "x").Is("id")],
                    RuleCommand.Set(Call(sub, Call(mul, "a", Call(integral, "b", "x")), Call(integral, Call(mul, Call(diff, "x", "a"), Call(integral, "b", "x")), "x")), "id")),
            ];

            {
                Value R(long n) => egraph.BaseValues.Get<InternedCodec<Rational>, Rational>(new Rational(n, 1));
                Value S(string s) => egraph.BaseValues.Get<StringCodec, string>(s);
                var (r1, r2, r3, r7) = (R(1), R(2), R(3), R(7));
                var (x, y, five) = (S("x"), S("y"), S("five"));
                ExpressionBuilder.AddExpressions(egraph,
                    Call(integral, Call(ln, Call(varFn, x)), Call(varFn, x)),
                    Call(integral, Call(add, Call(varFn, x), Call(cos, Call(varFn, x))), Call(varFn, x)),
                    Call(integral, Call(mul, Call(cos, Call(varFn, x)), Call(varFn, x)), Call(varFn, x)),
                    Call(diff, Call(varFn, x), Call(add, Call(rat, r1), Call(mul, Call(rat, r2), Call(varFn, x)))),
                    Call(diff, Call(varFn, x), Call(sub, Call(pow, Call(varFn, x), Call(rat, r3)), Call(mul, Call(rat, r7), Call(pow, Call(varFn, x), Call(rat, r2))))),
                    Call(add, Call(mul, Call(varFn, y), Call(add, Call(varFn, x), Call(varFn, y))), Call(sub, Call(add, Call(varFn, x), Call(rat, r2)), Call(add, Call(varFn, x), Call(varFn, x)))),
                    Call(div, Call(rat, r1), Call(sub, Call(div, Call(add, Call(rat, r1), Call(sqrt, Call(varFn, five))), Call(rat, r2)), Call(div, Call(sub, Call(rat, r1), Call(sqrt, Call(varFn, five))), Call(rat, r2)))));
            }

            for (var i = 0; i < N; i++)
            {
                if (egraph.RunRules(rules).Changed == false)
                    break;
            }

            // Numbers validated against the egglog implementation.
            Assert.Equal(338, egraph.TableSize(diff));
            Assert.Equal(782, egraph.TableSize(integral));
            Assert.Equal(483, egraph.TableSize(sub));
            Assert.Equal(3, egraph.TableSize(div));
            Assert.Equal(2, egraph.TableSize(pow));
            Assert.Equal(1, egraph.TableSize(ln));
            Assert.Equal(1, egraph.TableSize(sqrt));
            Assert.Equal(1, egraph.TableSize(sin));
            Assert.Equal(1, egraph.TableSize(cos));
            Assert.Equal(5, egraph.TableSize(rat));
            Assert.Equal(3, egraph.TableSize(varFn));
            Assert.Equal(2977, egraph.TableSize(add));
            Assert.Equal(3516, egraph.TableSize(mul));
        }

        /// <summary>
        /// Registers egglog's <c>vec-push</c> test primitive (tests.rs:460-476): a vector with one more element,
        /// interned.
        /// </summary>
        static ExternalFunctionId RegisterVecPush(EGraph egraph)
        {
            egraph.RegisterContainerType<VecContainer>();
            return egraph.RegisterExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                if (args.Length != 2)
                    throw new InvalidOperationException($"[vec-push] expected 2 values, got {args.Length}");

                result = 0;
                if (state.Containers.TryGetContainer<VecContainer>(args[0], out var vec) == false)
                    return false;

                uint[] pushed = [.. vec.Values, args[1]];
                result = state.Containers.GetOrInsert(new VecContainer(pushed), state);
                return true;
            }));
        }

        /// <summary>
        /// Registers egglog's <c>vec-last</c> test primitive (tests.rs:478-492): the last element, nothing for an empty
        /// vector.
        /// </summary>
        static ExternalFunctionId RegisterVecLast(EGraph egraph)
        {
            egraph.RegisterContainerType<VecContainer>();
            return egraph.RegisterExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                if (args.Length != 1)
                    throw new InvalidOperationException($"[vec-last] expected 1 value, got {args.Length}");

                result = 0;
                if (state.Containers.TryGetContainer<VecContainer>(args[0], out var vec) == false || vec.Length == 0)
                    return false;

                result = vec.Values[^1];
                return true;
            }));
        }

        /// <summary>
        /// egglog's <c>dump_vecs</c> (tests.rs:494-500), as sorted text.
        /// </summary>
        static List<string> DumpVecs(EGraph egraph)
        {
            var collector = new ContainerCollector<VecContainer>();
            egraph.ContainerValues.ForEach<VecContainer, ContainerCollector<VecContainer>>(ref collector);
            return collector.Items.Select(item => "[" + string.Join(",", item.Container.Values.ToArray()) + "]").OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Formats vectors the way <see cref="DumpVecs"/> does, sorted.
        /// </summary>
        static List<string> Vecs(params uint[][] vecs) => vecs.Select(v => "[" + string.Join(",", v) + "]").OrderBy(s => s, StringComparer.Ordinal).ToList();

        /// <summary>
        /// egglog's <c>container_test</c> (tests.rs:508-709): rules that build vectors through container primitives
        /// (one of which can fail), and a rule that evaluates additions, whose unions then collapse the vectors through
        /// container rebuilding.
        /// </summary>
        static void ContainerTest()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var num = Constructor(egraph, "num", false, i64);
            var add = Constructor(egraph, "add", false, ColumnTy.Id, ColumnTy.Id);
            var vecTable = Constructor(egraph, "vec", false, ColumnTy.Id);
            var intAdd = Register(egraph, (state, args) => state.BaseValues.Get<Int64Codec, long>(state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0])) + state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1]))).Rep);
            var vecLast = RegisterVecLast(egraph);
            var vecPush = RegisterVecPush(egraph);

            // Add 0 and 1 to the database.
            var ids = new List<Value>();
            var numRows = new List<(FunctionId, Value[])>();
            for (var i = 0; i <= 1; i++)
            {
                var id = egraph.FreshId();
                ids.Add(id);
                numRows.Add((num, [Int(egraph, i), id]));
            }

            egraph.AddValues(numRows);

            var emptyVec = egraph.GetContainerValue(new VecContainer());
            var vec1 = egraph.GetContainerValue(new VecContainer(ids[1].Rep));
            var emptyVecId = egraph.FreshId();
            var vec1Id = egraph.FreshId();
            egraph.AddValues([(vecTable, [emptyVec, emptyVecId]), (vecTable, [vec1, vec1Id])]);

            EGraphRuleId vecExpand;
            {
                var rb = egraph.NewRule("", true);
                var vec = rb.NewVar(ColumnTy.Id);
                var vecId = rb.NewVar(ColumnTy.Id);
                var last = rb.NewVar(ColumnTy.Id);
                rb.QueryTable(vecTable, [vec, vecId], false);
                rb.QueryPrim(vecLast, [vec, last], ColumnTy.Id);
                var zeroId = RuleEntry.Const(ids[0], i64);
                var addLast0 = rb.Lookup(add, [last, zeroId], () => "add_last_0");
                var add0Last = rb.Lookup(add, [zeroId, last], () => "add_0_last");
                var newVec1 = rb.CallExternalFunc(vecPush, [vec, addLast0], ColumnTy.Id, () => "");
                var newVec2 = rb.CallExternalFunc(vecPush, [vec, add0Last], ColumnTy.Id, () => "");
                rb.Lookup(vecTable, [newVec1], () => "");
                rb.Lookup(vecTable, [newVec2], () => "");
                vecExpand = rb.Build();
            }

            EGraphRuleId evalAdd;
            {
                var rb = egraph.NewRule("", true);
                var lhsRaw = rb.NewVar(i64);
                var lhsId = rb.NewVar(ColumnTy.Id);
                var rhsRaw = rb.NewVar(i64);
                var rhsId = rb.NewVar(ColumnTy.Id);
                var addId = rb.NewVar(ColumnTy.Id);
                rb.QueryTable(num, [lhsRaw, lhsId], false);
                rb.QueryTable(num, [rhsRaw, rhsId], false);
                rb.QueryTable(add, [lhsId, rhsId, addId], false);
                var evaled = rb.CallExternalFunc(intAdd, [lhsRaw, rhsRaw], i64, () => "");
                var boxed = rb.Lookup(num, [evaled], () => "");
                rb.Union(addId, boxed);
                evalAdd = rb.Build();
            }

            Assert.Equal(Vecs([], [egraph.GetCanonInUf(ids[1]).Rep]), DumpVecs(egraph));

            Assert.True(egraph.RunRules(vecExpand).Changed);
            Assert.Equal(4, DumpVecs(egraph).Count);

            // Two new vectors with a last element, each spawning two more.
            Assert.True(egraph.RunRules(vecExpand).Changed);
            Assert.Equal(8, DumpVecs(egraph).Count);

            // Four new vectors with a last element, each spawning two more.
            Assert.True(egraph.RunRules(vecExpand).Changed);
            Assert.Equal(16, DumpVecs(egraph).Count);

            // Saturating eval_add collapses the vectors.
            var saturated = false;
            for (var i = 0; i < 20; i++)
            {
                saturated = egraph.RunRules(evalAdd).Changed == false;
                if (saturated)
                    break;
            }

            Assert.True(saturated, "failed to saturate after 20 iterations");
            var oneId = egraph.GetCanonInUf(ids[1]).Rep;
            Assert.Equal(Vecs([], [oneId], [oneId, oneId], [oneId, oneId, oneId], [oneId, oneId, oneId, oneId]), DumpVecs(egraph));
        }

        /// <summary>
        /// egglog's <c>basic_container</c> (tests.rs:711-717), eight times as in egglog.
        /// </summary>
        [Fact]
        public void BasicContainer()
        {
            for (var i = 0; i < 8; i++)
                ContainerTest();
        }

        /// <summary>
        /// egglog's <c>run_query_prim_container_match_case</c> (tests.rs:719-804): a left-hand-side primitive that
        /// reads the contents of a container must be re-evaluated once rebuilding changes what the container means,
        /// which the re-stamping of its parent rows achieves (with <paramref name="seedCanonical"/>, the rebuilt
        /// contents collide with an existing container instead). Returns whether the rules unified <c>l</c>'s output
        /// with <c>b</c>.
        /// </summary>
        static bool RunQueryPrimContainerMatchCase(bool semiNaive, bool seedCanonical)
        {
            var egraph = NewEGraph();
            var k = Constructor(egraph, "k", false, ColumnTy.Id);
            var w = Constructor(egraph, "w", false, ColumnTy.Id);
            var l = Constructor(egraph, "l", false, ColumnTy.Id);
            egraph.RegisterContainerType<VecContainer>();

            var b = egraph.FreshId();
            var kB = egraph.AddTerm(k, [b]);
            if (seedCanonical)
                egraph.GetContainerValue(new VecContainer(kB.Rep));

            var wKB = egraph.AddTerm(w, [kB]);
            var vec = egraph.GetContainerValue(new VecContainer(wKB.Rep));
            var lId = egraph.AddTerm(l, [vec]);

            var rawK = egraph.GetTable(k);
            var matchSingletonK = egraph.RegisterExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                if (args.Length != 1)
                    throw new InvalidOperationException($"match_singleton_k expected 1 arg, got {args.Length}");

                result = 0;
                if (state.Containers.TryGetContainer<VecContainer>(args[0], out var container) == false || container.Length != 1)
                    return false;

                var entry = container.Values[0];
                var table = state.GetTable(rawK);
                var all = table.All();
                using var rows = table.Scan(all.AsRef());
                all.Dispose();
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows.IsStale(i))
                        continue;

                    var row = rows.GetRow(i);
                    if (row[1] == entry)
                    {
                        result = row[0];
                        return true;
                    }
                }

                return false;
            }));

            EGraphRuleId wRewrite;
            {
                var rb = egraph.NewRule("w_rewrite", semiNaive);
                var x = rb.NewVar(ColumnTy.Id);
                var wId = rb.NewVar(ColumnTy.Id);
                rb.QueryTable(w, [x, wId], false);
                rb.Union(wId, x);
                wRewrite = rb.Build();
            }

            EGraphRuleId lRewrite;
            {
                var rb = egraph.NewRule("l_rewrite", semiNaive);
                var v = rb.NewVar(ColumnTy.Id);
                var lIdEntry = rb.NewVar(ColumnTy.Id);
                var x = rb.NewVar(ColumnTy.Id);
                rb.QueryTable(l, [v, lIdEntry], false);
                rb.QueryPrim(matchSingletonK, [v, x], ColumnTy.Id);
                rb.Union(lIdEntry, x);
                lRewrite = rb.Build();
            }

            var saturated = false;
            for (var i = 0; i < 8; i++)
            {
                saturated = egraph.RunRules(wRewrite, lRewrite).Changed == false;
                if (saturated)
                    break;
            }

            Assert.True(saturated, "failed to saturate after 8 iterations");
            return egraph.GetCanonInUf(lId) == egraph.GetCanonInUf(b);
        }

        /// <summary>
        /// egglog's <c>seminaive_query_prim_rechecks_after_rebuild</c> (tests.rs:806-810).
        /// </summary>
        [Fact]
        public void SemiNaiveQueryPrimRechecksAfterRebuild()
        {
            Assert.True(RunQueryPrimContainerMatchCase(true, false));
            Assert.True(RunQueryPrimContainerMatchCase(false, false));
        }

        /// <summary>
        /// egglog's <c>seminaive_query_prim_rechecks_after_preseeded_container_rebuild</c> (tests.rs:812-815).
        /// </summary>
        [Fact]
        public void SemiNaiveQueryPrimRechecksAfterPreseededContainerRebuild()
        {
            Assert.True(RunQueryPrimContainerMatchCase(true, true));
        }

        /// <summary>
        /// egglog's <c>rhs_only_rule</c> (tests.rs:817-853): a rule without atoms mints <c>num(0)</c> and
        /// <c>num(1)</c>.
        /// </summary>
        [Fact]
        public void RhsOnlyRule()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var zero = Int(egraph, 0);
            var one = Int(egraph, 1);
            var num = Constructor(egraph, "num", false, i64);
            EGraphRuleId addData;
            {
                var zeroEntry = egraph.BaseValueConstant<Int64Codec, long>(0);
                var oneEntry = egraph.BaseValueConstant<Int64Codec, long>(1);
                var rb = egraph.NewRule("", true);
                rb.Lookup(num, [zeroEntry], () => "");
                rb.Lookup(num, [oneEntry], () => "");
                addData = rb.Build();
            }

            Assert.True(egraph.RunRules(addData).Changed);
            var contents = Rows(egraph, num);
            Assert.All(contents, row => Assert.False(row.Subsumed));
            Value[][] expected = [[zero, new Value(0)], [one, new Value(1)]];
            Assert.Equal(expected, contents.Select(r => r.Values).ToArray());
        }

        /// <summary>
        /// egglog's <c>rhs_only_rule_only_runs_once</c> (tests.rs:855-875): a semi-naive rule without atoms fires on
        /// its first run only.
        /// </summary>
        [Fact]
        public void RhsOnlyRuleOnlyRunsOnce()
        {
            var egraph = NewEGraph();
            var counter = 0;
            var incCounter = Register(egraph, (_, _) =>
            {
                Interlocked.Increment(ref counter);
                return 0;
            });

            EGraphRuleId incCounterRule;
            {
                var rb = egraph.NewRule("", true);
                rb.CallExternalFunc(incCounter, [], ColumnTy.Id, () => "");
                incCounterRule = rb.Build();
            }

            Assert.False(egraph.RunRules(incCounterRule).Changed);
            Assert.Equal(1, Volatile.Read(ref counter));
            Assert.False(egraph.RunRules(incCounterRule).Changed);
            Assert.Equal(1, Volatile.Read(ref counter));
        }

        /// <summary>
        /// egglog's <c>mergefn_arithmetic</c> (tests.rs:877-999): rules write a function whose merge is
        /// <c>(+ 1 (* old new))</c>.
        /// </summary>
        [Fact]
        public void MergeFnArithmetic()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var multiply = Register(egraph, (state, args) => args.Length == 2
                ? state.BaseValues.Get<Int64Codec, long>(state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0])) * state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1]))).Rep
                : null);
            var addFunc = Register(egraph, (state, args) => args.Length == 2
                ? state.BaseValues.Get<Int64Codec, long>(state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0])) + state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1]))).Rep
                : null);

            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, i64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(addFunc, MergeFn.Const(Int(egraph, 1)), MergeFn.Primitive(multiply, MergeFn.Old, MergeFn.New)),
                Name = "f",
            });

            RuleEntry V(long n) => egraph.BaseValueConstant<Int64Codec, long>(n);

            List<(long, long)> Contents()
            {
                var contents = new List<(long, long)>();
                egraph.ForEach(f, row =>
                {
                    Assert.False(row.Subsumed);
                    contents.Add((Unint(egraph, row.Values[0]), Unint(egraph, row.Values[1])));
                });
                contents.Sort();
                return contents;
            }

            // (f 1 0) (f 2 1)
            EGraphRuleId rule1;
            {
                var rb = egraph.NewRule("rule1", true);
                rb.Set(f, [V(1), V(0)]);
                rb.Set(f, [V(2), V(1)]);
                rule1 = rb.Build();
            }

            Assert.True(egraph.RunRules(rule1).Changed);
            Assert.Equal([(1L, 0L), (2L, 1L)], Contents());

            // (f 1 5) (f 2 6): 1 + 0 * 5 = 1 and 1 + 1 * 6 = 7.
            EGraphRuleId rule2;
            {
                var rb = egraph.NewRule("rule2", true);
                rb.Set(f, [V(1), V(5)]);
                rb.Set(f, [V(2), V(6)]);
                rule2 = rb.Build();
            }

            Assert.True(egraph.RunRules(rule2).Changed);
            Assert.Equal([(1L, 1L), (2L, 7L)], Contents());

            // (f 1 3) (f 2 4): 1 + 1 * 3 = 4 and 1 + 7 * 4 = 29.
            EGraphRuleId rule3;
            {
                var rb = egraph.NewRule("rule3", true);
                rb.Set(f, [V(1), V(3)]);
                rb.Set(f, [V(2), V(4)]);
                rule3 = rb.Build();
            }

            Assert.True(egraph.RunRules(rule3).Changed);
            Assert.Equal([(1L, 4L), (2L, 29L)], Contents());
        }

        /// <summary>
        /// egglog's <c>mergefn_nested_function</c> (tests.rs:1001-1120): a function whose merge is
        /// <c>(g (g new new) (g old old))</c>, written by rules.
        /// </summary>
        [Fact]
        public void MergeFnNestedFunction()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var g = Constructor(egraph, "g", true, ColumnTy.Id, ColumnTy.Id);
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.Function(g, MergeFn.Function(g, MergeFn.New, MergeFn.New), MergeFn.Function(g, MergeFn.Old, MergeFn.Old)),
                Name = "f",
                CanSubsume = true,
            });

            var value1 = egraph.BaseValueConstant<Int64Codec, long>(1);
            var value2 = egraph.BaseValueConstant<Int64Codec, long>(2);

            EGraphRuleId writeRule;
            {
                var rb = egraph.NewRule("write_rule", true);
                rb.Lookup(f, [value1], () => "");
                rb.Lookup(f, [value2], () => "");
                writeRule = rb.Build();
            }

            List<(Value, Value, Value)> GEntries()
            {
                var entries = new List<(Value, Value, Value)>();
                egraph.ForEach(g, row =>
                {
                    Assert.False(row.Subsumed);
                    entries.Add((row.Values[0], row.Values[1], row.Values[2]));
                });
                entries.Sort((a, b) => (a.Item1.Rep, a.Item2.Rep, a.Item3.Rep).CompareTo((b.Item1.Rep, b.Item2.Rep, b.Item3.Rep)));
                return entries;
            }

            List<(long, Value)> FEntries()
            {
                var entries = new List<(long, Value)>();
                egraph.ForEach(f, row =>
                {
                    Assert.False(row.Subsumed);
                    entries.Add((Unint(egraph, row.Values[0]), row.Values[1]));
                });
                entries.Sort((a, b) => (a.Item1, a.Item2.Rep).CompareTo((b.Item1, b.Item2.Rep)));
                return entries;
            }

            // The first run mints both outputs; no merge happened, so g is empty.
            Assert.True(egraph.RunRules(writeRule).Changed);
            var f1 = FEntries();
            var g1 = GEntries();
            Assert.Equal(2, f1.Count);
            var base1 = f1[0].Item2;
            var base2 = f1[1].Item2;
            Assert.Empty(g1);

            EGraphRuleId setRule;
            {
                var rb = egraph.NewRule("iterate", true);
                rb.Set(f, [value1, RuleEntry.Const(base2, ColumnTy.Id)]);
                setRule = rb.Build();
            }

            // Merging (f 1) with base2 fills g through the merge function.
            Assert.True(egraph.RunRules(setRule).Changed);
            var f2 = FEntries();
            var g2 = GEntries();
            Assert.Equal(2, f2.Count);
            Assert.Equal(3, g2.Count);

            var newBase1 = f2[0].Item2;
            var (mid1, mid2, _) = g2.Single(e => e.Item3 == newBase1);
            var (baseL1, baseL2, _) = g2.Single(e => e.Item3 == mid1);
            var (baseR1, baseR2, _) = g2.Single(e => e.Item3 == mid2);

            // new is base2 and old is base1.
            Assert.Equal(baseL1, baseL2);
            Assert.Equal(base2, baseL1);
            Assert.Equal(baseR1, baseR2);
            Assert.Equal(base1, baseR1);
        }

        /// <summary>
        /// egglog's <c>constrain_prims_simple</c> (tests.rs:1122-1214): a left-hand-side primitive compared with a
        /// constant filters the matches, and a run without new rows does not call it again.
        /// </summary>
        [Fact]
        public void ConstrainPrimsSimple()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var boolType = ColumnTy.Base(egraph.BaseValues.RegisterType<BooleanCodec, bool>());
            var f = Constructor(egraph, "f", false, i64);
            var g = Constructor(egraph, "g", false, i64);

            var invocations = 0;
            var isEven = Register(egraph, (state, args) =>
            {
                if (args.Length != 1)
                    return null;

                Interlocked.Increment(ref invocations);
                var a = state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0]));
                return state.BaseValues.Get<BooleanCodec, bool>(a % 2 == 0).Rep;
            });

            var valueTrue = egraph.BaseValueConstant<BooleanCodec, bool>(true);
            EGraphRuleId writeF;
            {
                var rb = egraph.NewRule("write_f", true);
                rb.Lookup(f, [egraph.BaseValueConstant<Int64Codec, long>(1)], () => "");
                rb.Lookup(f, [egraph.BaseValueConstant<Int64Codec, long>(2)], () => "");
                rb.Lookup(f, [egraph.BaseValueConstant<Int64Codec, long>(3)], () => "");
                writeF = rb.Build();
            }

            EGraphRuleId copyToG;
            {
                var rb = egraph.NewRule("copy_to_g", true);
                var val = rb.NewVar(i64);
                var id = rb.NewVar(ColumnTy.Id);
                rb.QueryTable(f, [val, id], false);
                rb.QueryPrim(isEven, [val, valueTrue], boolType);
                rb.Set(g, [val, id]);
                copyToG = rb.Build();
            }

            Assert.Empty(Entries(egraph, f));
            Assert.Empty(Entries(egraph, g));
            egraph.RunRules(writeF);
            var fEntries = Entries(egraph, f);
            Assert.Equal(3, fEntries.Count);
            egraph.RunRules(copyToG);
            var afterFirst = Volatile.Read(ref invocations);
            Assert.True(afterFirst > 0);
            Assert.False(egraph.RunRules(copyToG).Changed);
            Assert.Equal(afterFirst, Volatile.Read(ref invocations));
            var gEntries = Entries(egraph, g);
            Assert.Single(gEntries);
            Assert.Equal(fEntries[1], gEntries[0]);
        }

        /// <summary>
        /// egglog's <c>constrain_prims_abstract</c> (tests.rs:1216-1311): the first primitive binds <c>negval</c>, the
        /// second is then compared with it, keeping the numbers with <c>-x = |x|</c>.
        /// </summary>
        [Fact]
        public void ConstrainPrimsAbstract()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var f = Constructor(egraph, "f", false, i64);
            var g = Constructor(egraph, "g", false, i64);
            var neg = Register(egraph, (state, args) => args.Length == 1 ? state.BaseValues.Get<Int64Codec, long>(-state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0]))).Rep : null);
            var abs = Register(egraph, (state, args) => args.Length == 1 ? state.BaseValues.Get<Int64Codec, long>(System.Math.Abs(state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0])))).Rep : null);

            EGraphRuleId writeF;
            {
                var rb = egraph.NewRule("write_f", true);
                rb.Lookup(f, [egraph.BaseValueConstant<Int64Codec, long>(-1)], () => "");
                rb.Lookup(f, [egraph.BaseValueConstant<Int64Codec, long>(0)], () => "");
                rb.Lookup(f, [egraph.BaseValueConstant<Int64Codec, long>(1)], () => "");
                writeF = rb.Build();
            }

            EGraphRuleId copyToG;
            {
                var rb = egraph.NewRule("copy_to_g", true);
                var val = rb.NewVar(i64);
                var id = rb.NewVar(ColumnTy.Id);
                var negVal = rb.NewVar(i64);
                rb.QueryTable(f, [val, id], false);
                rb.QueryPrim(neg, [val, negVal], i64);
                rb.QueryPrim(abs, [val, negVal], i64);
                rb.Set(g, [val, id]);
                copyToG = rb.Build();
            }

            Assert.Empty(Entries(egraph, f));
            Assert.Empty(Entries(egraph, g));
            egraph.RunRules(writeF);
            var fEntries = Entries(egraph, f);
            Assert.Equal(3, fEntries.Count);
            egraph.RunRules(copyToG);
            var gEntries = Entries(egraph, g);
            Assert.Equal(2, gEntries.Count);
            Assert.Equal(fEntries.Take(2), gEntries);
        }

        /// <summary>
        /// egglog's <c>basic_subsumption</c> (tests.rs:1313-1393): subsuming <c>(f 2)</c> and the missing <c>(f 3)</c>
        /// leaves only <c>(f 1)</c> for a rule that matches rows that are not subsumed.
        /// </summary>
        [Fact]
        public void BasicSubsumption()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var f = Constructor(egraph, "f", true, i64);
            var g = Constructor(egraph, "g", false, i64);
            var value1 = egraph.BaseValueConstant<Int64Codec, long>(1);
            var value2 = egraph.BaseValueConstant<Int64Codec, long>(2);
            var value3 = egraph.BaseValueConstant<Int64Codec, long>(3);

            EGraphRuleId writeF;
            {
                var rb = egraph.NewRule("write_f", true);
                rb.Lookup(f, [value1], () => "");
                rb.Lookup(f, [value2], () => "");
                writeF = rb.Build();
            }

            EGraphRuleId subsumeF;
            {
                var rb = egraph.NewRule("write_f", true);
                rb.Subsume(f, [value2]);
                rb.Subsume(f, [value3]);
                subsumeF = rb.Build();
            }

            EGraphRuleId copyToG;
            {
                var rb = egraph.NewRule("copy_to_g", true);
                var val = rb.NewVar(i64);
                var id = rb.NewVar(ColumnTy.Id);
                rb.QueryTable(f, [val, id], false);
                rb.Set(g, [val, id]);
                copyToG = rb.Build();
            }

            (List<(long, Value)> Entries, int Subsumed) Get(FunctionId table)
            {
                var entries = new List<(long, Value)>();
                var subsumed = 0;
                egraph.ForEach(table, row =>
                {
                    entries.Add((Unint(egraph, row.Values[0]), row.Values[1]));
                    if (row.Subsumed)
                        subsumed++;
                });
                entries.Sort((a, b) => (a.Item1, a.Item2.Rep).CompareTo((b.Item1, b.Item2.Rep)));
                return (entries, subsumed);
            }

            Assert.Empty(Get(f).Entries);
            Assert.Empty(Get(g).Entries);
            egraph.RunRules(writeF);
            var fState = Get(f);
            Assert.Equal((2, 0), (fState.Entries.Count, fState.Subsumed));
            Assert.Equal([1L, 2L], fState.Entries.Select(e => e.Item1));
            egraph.RunRules(subsumeF);
            fState = Get(f);
            Assert.Equal((3, 2), (fState.Entries.Count, fState.Subsumed));
            Assert.Equal([1L, 2L, 3L], fState.Entries.Select(e => e.Item1));
            egraph.RunRules(copyToG);
            var gState = Get(g);
            Assert.Equal((1, 0), (gState.Entries.Count, gState.Subsumed));
            Assert.Equal(fState.Entries[0], gState.Entries[0]);
        }

        /// <summary>
        /// egglog's <c>lookup_failure_panics</c> (tests.rs:1395-1435): looking up a missing key of a function without a
        /// default panics the run.
        /// </summary>
        [Fact]
        public void LookupFailurePanics()
        {
            var egraph = NewEGraph();
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.UnionId,
                Name = "test",
            });

            static RuleEntry ToEntry(uint v) => RuleEntry.Const(new Value(v), ColumnTy.Id);

            EGraphRuleId writeF;
            {
                var rb = egraph.NewRule("write_f", true);
                rb.Set(f, [ToEntry(1), ToEntry(1)]);
                rb.Set(f, [ToEntry(2), ToEntry(2)]);
                writeF = rb.Build();
            }

            egraph.RunRules(writeF);

            EGraphRuleId lookupSuccess;
            {
                var rb = egraph.NewRule("lookup_success", true);
                rb.Lookup(f, [ToEntry(1)], () => "");
                lookupSuccess = rb.Build();
            }

            egraph.RunRules(lookupSuccess);

            EGraphRuleId lookupFailure;
            {
                var rb = egraph.NewRule("lookup_fail", true);
                rb.Lookup(f, [ToEntry(3)], () => "");
                lookupFailure = rb.Build();
            }

            Assert.Throws<PanicException>(() => egraph.RunRules(lookupFailure));
        }

        /// <summary>
        /// egglog's <c>primitive_failure_panics</c> (tests.rs:1437-1478): a right-hand-side call that produces nothing
        /// panics the run.
        /// </summary>
        [Fact]
        public void PrimitiveFailurePanics()
        {
            var egraph = NewEGraph();
            IntType(egraph);
            var unitType = ColumnTy.Base(egraph.BaseValues.RegisterType<UnitCodec, ValueTuple>());
            var value1 = egraph.BaseValueConstant<Int64Codec, long>(1);
            var value2 = egraph.BaseValueConstant<Int64Codec, long>(2);
            var assertOdd = Register(egraph, (state, args) =>
            {
                if (args.Length != 1)
                    return null;

                var a = state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0]));
                return a % 2 == 1 ? state.BaseValues.Get<UnitCodec, ValueTuple>(default).Rep : null;
            });

            EGraphRuleId assertOddRule;
            {
                var rb = egraph.NewRule("assert_odd", true);
                rb.CallExternalFunc(assertOdd, [value1], unitType, () => "");
                rb.CallExternalFunc(assertOdd, [value2], unitType, () => "");
                assertOddRule = rb.Build();
            }

            Assert.Throws<PanicException>(() => egraph.RunRules(assertOddRule));
        }

        /// <summary>
        /// Lists the rows of a function of an integer key as sorted (key, output) pairs: the <c>get_entries</c>
        /// closures of egglog's tests.
        /// </summary>
        static List<(long, Value)> Entries(EGraph egraph, FunctionId function)
        {
            var entries = new List<(long, Value)>();
            egraph.ForEach(function, row =>
            {
                Assert.False(row.Subsumed);
                entries.Add((Unint(egraph, row.Values[0]), row.Values[1]));
            });
            entries.Sort((a, b) => (a.Item1, a.Item2.Rep).CompareTo((b.Item1, b.Item2.Rep)));
            return entries;
        }

    }

}
