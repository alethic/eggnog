using System.Globalization;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A term of a <see cref="ScopedProgram"/>: a variable (its name's first letter gives its type: <c>x</c> an
    /// <c>i64</c>, <c>s</c> a <c>String</c>, <c>n</c> a <c>Node</c>), a value constant, or, in a head, a constructor
    /// application <c>C(term)</c>.
    /// </summary>
    /// <param name="Var">The variable's name; <c>null</c> otherwise.</param>
    /// <param name="Constant">The constant, a <c>long</c> or a <c>string</c>; <c>null</c> otherwise.</param>
    /// <param name="Construct">The table index of a constructor application; -1 otherwise.</param>
    /// <param name="Argument">The application's argument.</param>
    internal sealed record ScopedTermSpec(string? Var, object? Constant, int Construct = -1, ScopedTermSpec? Argument = null)
    {

        /// <summary>
        /// Makes a variable.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns>The term.</returns>
        public static ScopedTermSpec Variable(string name) => new(name, null);

        /// <summary>
        /// Makes a constant.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The term.</returns>
        public static ScopedTermSpec Value(object value) => new(null, value);

        /// <inheritdoc />
        public override string ToString() => Var ?? (Construct >= 0
            ? $"(C{Construct.ToString(CultureInfo.InvariantCulture)} {Argument})"
            : Constant is string text ? "\"" + text + "\"" : string.Format(CultureInfo.InvariantCulture, "{0}", Constant));

    }

}
