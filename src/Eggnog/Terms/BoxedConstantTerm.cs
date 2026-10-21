using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A constant given as an object where a term of a known type is expected: an argument of the any-arity
    /// applications (<see cref="RelationN.Of"/>, <see cref="FunctionN{TOut}.Of"/>, <see cref="ConstructorN{TSort}.Of"/>,
    /// <see cref="PrimitiveN{TResult}.Of"/>), which take values and terms alike. Its column type is resolved where it is
    /// used, as a typed constant's is.
    /// </summary>
    internal sealed class BoxedConstantTerm : Term
    {

        readonly Type type;
        readonly object value;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="type">The value's type.</param>
        /// <param name="value">The value, of exactly that type.</param>
        BoxedConstantTerm(Type type, object value)
        {
            this.type = type;
            this.value = value;
        }

        /// <inheritdoc />
        public override Type ClrType => type;

        /// <inheritdoc />
        internal override TermShape Shape => new(TermShapeKind.Constant, type, null, null, null, value, null, null, Array.Empty<Term>());

        /// <summary>
        /// Converts the arguments of an application or a call: a term as it is, which must be of exactly its parameter's
        /// type, and any other value a constant of its parameter's type, which it must be an instance of (a boxed
        /// <c>int</c> is not a <c>long</c>).
        /// </summary>
        /// <param name="arguments">The arguments.</param>
        /// <param name="types">The parameters' types.</param>
        /// <param name="name">The table's or primitive's name, for messages.</param>
        /// <returns>The terms, a new array.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="arguments"/> or an argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The number of arguments is not the parameters', or an argument's type is
        /// not its parameter's.</exception>
        public static Term[] Convert(object[] arguments, IReadOnlyList<Type> types, string name)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            if (arguments.Length != types.Count)
                throw new ArgumentException($"'{name}' takes {types.Count} arguments, not {arguments.Length}.", nameof(arguments));

            var terms = new Term[arguments.Length];
            for (var i = 0; i < terms.Length; i++)
            {
                var argument = arguments[i] ?? throw new ArgumentNullException(nameof(arguments), $"Argument {i} of '{name}' is null.");
                var type = argument is Term term ? term.ClrType : argument.GetType();
                if (argument is Term ? type != types[i] : !types[i].IsInstanceOfType(argument))
                    throw new ArgumentException($"Argument {i} of '{name}' is a {ValueStore.DisplayName(type)}, not a {ValueStore.DisplayName(types[i])}.", nameof(arguments));

                terms[i] = argument as Term ?? new BoxedConstantTerm(types[i], argument);
            }

            return terms;
        }

    }

}
