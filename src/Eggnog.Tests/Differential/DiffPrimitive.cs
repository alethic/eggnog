using System;
using System.Globalization;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A primitive a random program may call: a named built-in of <see cref="Primitives"/> or a lambda, with its
    /// signature, its meaning for the reference interpreter, the engine primitive for Eggnog, and its C# spelling for a
    /// printed program. Every primitive is pure, total on the values a program reaches, and bounded, so runs stay small.
    /// </summary>
    internal sealed class DiffPrimitive
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The name: the egglog name of a named primitive, a description of a lambda.</param>
        /// <param name="isLambda">Whether it is a lambda (rendered as <c>&lt;lambda&gt;</c>).</param>
        /// <param name="parameters">The parameter types.</param>
        /// <param name="result">The result type.</param>
        /// <param name="csharp">Spells a call in C#, given the arguments' spellings.</param>
        /// <param name="evaluate">The meaning: decoded arguments to the decoded result.</param>
        /// <param name="create">Makes the engine primitive.</param>
        DiffPrimitive(string name, bool isLambda, DiffType[] parameters, DiffType result, Func<string[], string> csharp, Func<object[], object> evaluate,
            Func<Primitive> create)
        {
            Name = name;
            IsLambda = isLambda;
            Parameters = parameters;
            Result = result;
            CSharp = csharp;
            Evaluate = evaluate;
            Create = create;
        }

        /// <summary>Gets <c>+</c> over <c>i64</c>.</summary>
        public static DiffPrimitive Add { get; } = Named("+", "Add", new[] { DiffType.I64, DiffType.I64 }, DiffType.I64, a => (long)a[0] + (long)a[1], () => Primitives.Add);

        /// <summary>Gets <c>max</c> over <c>i64</c>.</summary>
        public static DiffPrimitive Max { get; } = Named("max", "Max", new[] { DiffType.I64, DiffType.I64 }, DiffType.I64, a => Math.Max((long)a[0], (long)a[1]), () => Primitives.Max);

        /// <summary>Gets <c>&lt;</c>.</summary>
        public static DiffPrimitive Less { get; } = Named("<", "Less", new[] { DiffType.I64, DiffType.I64 }, DiffType.Bool, a => (long)a[0] < (long)a[1], () => Primitives.Less);

        /// <summary>Gets <c>&gt;</c>.</summary>
        public static DiffPrimitive Greater { get; } = Named(">", "Greater", new[] { DiffType.I64, DiffType.I64 }, DiffType.Bool, a => (long)a[0] > (long)a[1], () => Primitives.Greater);

        /// <summary>Gets <c>&lt;=</c>.</summary>
        public static DiffPrimitive LessOrEqual { get; } = Named("<=", "LessOrEqual", new[] { DiffType.I64, DiffType.I64 }, DiffType.Bool, a => (long)a[0] <= (long)a[1], () => Primitives.LessOrEqual);

        /// <summary>Gets <c>&gt;=</c>.</summary>
        public static DiffPrimitive GreaterOrEqual { get; } = Named(">=", "GreaterOrEqual", new[] { DiffType.I64, DiffType.I64 }, DiffType.Bool, a => (long)a[0] >= (long)a[1], () => Primitives.GreaterOrEqual);

        /// <summary>Gets <c>set-length</c>.</summary>
        public static DiffPrimitive SetLength { get; } = Named("set-length", "Count<int>()", new[] { DiffType.IntSet }, DiffType.I64, a => (long)((ValueSet<int>)a[0]).Count, () => Primitives.Count<int>());

        /// <summary>Gets <c>set-empty</c>.</summary>
        public static DiffPrimitive SetEmpty { get; } = Named("set-empty", "IsEmpty<int>()", new[] { DiffType.IntSet }, DiffType.Bool, a => ((ValueSet<int>)a[0]).IsEmpty, () => Primitives.IsEmpty<int>());

        /// <summary>Gets <c>set-union</c>.</summary>
        public static DiffPrimitive SetUnion { get; } = Named("set-union", "Union<int>()", new[] { DiffType.IntSet, DiffType.IntSet }, DiffType.IntSet,
            a => ((ValueSet<int>)a[0]).Union((ValueSet<int>)a[1]), () => Primitives.Union<int>());

        /// <summary>Gets the lambda <c>x =&gt; (x * 3 + 1) % 7</c>.</summary>
        public static DiffPrimitive Affine { get; } = Lambda("affine", new[] { DiffType.I64 }, DiffType.I64, "x => (x * 3 + 1) % 7",
            a => ((long)a[0] * 3 + 1) % 7, () => new Primitive<long, long>(Primitive.LambdaName, x => (x * 3 + 1) % 7, true));

        /// <summary>Gets the lambda <c>s =&gt; string.CompareOrdinal(s, "b") &lt; 0</c>.</summary>
        public static DiffPrimitive StringLow { get; } = Lambda("string low", new[] { DiffType.Str }, DiffType.Bool, "s => string.CompareOrdinal(s, \"b\") < 0",
            a => string.CompareOrdinal((string)a[0], "b") < 0, () => new Primitive<string, bool>(Primitive.LambdaName, s => string.CompareOrdinal(s, "b") < 0, true));

        /// <summary>Gets the lambda <c>s =&gt; s.Length &lt; 3 ? s + "x" : s</c>.</summary>
        public static DiffPrimitive StringGrow { get; } = Lambda("string grow", new[] { DiffType.Str }, DiffType.Str, "s => s.Length < 3 ? s + \"x\" : s",
            a => ((string)a[0]).Length < 3 ? (string)a[0] + "x" : (string)a[0], () => new Primitive<string, string>(Primitive.LambdaName, s => s.Length < 3 ? s + "x" : s, true));

        /// <summary>Gets the lambda <c>d =&gt; d.Where(e =&gt; e % 2 == 0)</c>.</summary>
        public static DiffPrimitive SetEven { get; } = Lambda("set even", new[] { DiffType.IntSet }, DiffType.IntSet, "d => d.Where(e => e % 2 == 0)",
            a => ((ValueSet<int>)a[0]).Where(e => e % 2 == 0), () => new Primitive<ValueSet<int>, ValueSet<int>>(Primitive.LambdaName, d => d.Where(e => e % 2 == 0), true));

        /// <summary>Gets the lambda <c>d =&gt; d.Count &gt; 1</c>.</summary>
        public static DiffPrimitive SetBig { get; } = Lambda("set big", new[] { DiffType.IntSet }, DiffType.Bool, "d => d.Count > 1",
            a => ((ValueSet<int>)a[0]).Count > 1, () => new Primitive<ValueSet<int>, bool>(Primitive.LambdaName, d => d.Count > 1, true));

        /// <summary>Gets the lambda <c>x =&gt; ValueSet.Of((int)(x % 5), (int)((x + 2) % 5))</c>.</summary>
        public static DiffPrimitive SetAround { get; } = Lambda("set around", new[] { DiffType.I64 }, DiffType.IntSet, "x => ValueSet.Of((int)(x % 5), (int)((x + 2) % 5))",
            a => ValueSet.Of((int)((long)a[0] % 5), (int)(((long)a[0] + 2) % 5)),
            () => new Primitive<long, ValueSet<int>>(Primitive.LambdaName, x => ValueSet.Of((int)(x % 5), (int)((x + 2) % 5)), true));

        /// <summary>Gets the lambda <c>(x, y) =&gt; (x + y) % 6</c>.</summary>
        public static DiffPrimitive SumMod { get; } = Lambda("sum mod", new[] { DiffType.I64, DiffType.I64 }, DiffType.I64, "(x, y) => (x + y) % 6",
            a => ((long)a[0] + (long)a[1]) % 6, () => new Primitive<long, long, long>(Primitive.LambdaName, (x, y) => (x + y) % 6, true));

        /// <summary>Gets the name: the egglog name of a named primitive, a description of a lambda.</summary>
        public string Name { get; }

        /// <summary>Gets whether it is a lambda.</summary>
        public bool IsLambda { get; }

        /// <summary>Gets the parameter types.</summary>
        public DiffType[] Parameters { get; }

        /// <summary>Gets the result type.</summary>
        public DiffType Result { get; }

        /// <summary>Gets the C# spelling of a call, given the arguments' spellings.</summary>
        public Func<string[], string> CSharp { get; }

        /// <summary>Gets the meaning: decoded arguments to the decoded result.</summary>
        public Func<object[], object> Evaluate { get; }

        /// <summary>Gets a maker of the engine primitive.</summary>
        public Func<Primitive> Create { get; }

        /// <summary>
        /// The lambda <c>x =&gt; x % k == r</c>: a filter over <c>i64</c>.
        /// </summary>
        /// <param name="k">The modulus, at least 2.</param>
        /// <param name="r">The remainder, below <paramref name="k"/>.</param>
        /// <returns>The primitive.</returns>
        public static DiffPrimitive Modulo(long k, long r)
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"x => x % {k} == {r}");
            return Lambda("mod " + k.ToString(CultureInfo.InvariantCulture) + " = " + r.ToString(CultureInfo.InvariantCulture), new[] { DiffType.I64 }, DiffType.Bool, text,
                a => (long)a[0] % k == r, () => new Primitive<long, bool>(Primitive.LambdaName, x => x % k == r, true));
        }

        /// <summary>
        /// Makes a named primitive.
        /// </summary>
        static DiffPrimitive Named(string name, string member, DiffType[] parameters, DiffType result, Func<object[], object> evaluate, Func<Primitive> create) =>
            new(name, false, parameters, result, args => $"Primitives.{member}.Of({string.Join(", ", args)})", evaluate, create);

        /// <summary>
        /// Makes a lambda.
        /// </summary>
        static DiffPrimitive Lambda(string name, DiffType[] parameters, DiffType result, string lambda, Func<object[], object> evaluate, Func<Primitive> create) =>
            new(name, true, parameters, result, args => $"Compute.Of({string.Join(", ", args)}, {lambda})", evaluate, create);

        /// <inheritdoc />
        public override string ToString() => Name;

    }

}
