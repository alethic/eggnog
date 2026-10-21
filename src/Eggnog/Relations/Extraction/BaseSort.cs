using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A base value sort: its values are base values of the registered type <typeparamref name="TCodec"/>. egglog's
    /// <c>BaseSort</c> (src/prelude.rs:799-851). A base value extracts as a literal (<see cref="TermLiteral"/>) whose
    /// text is the formatter given here or, by default, what egglog prints for the literal its built-in sorts
    /// reconstruct (<c>Literal</c>'s <c>Display</c>): a <see cref="string"/> raw between double quotes and a
    /// <see cref="double"/> in positional notation (<see cref="LiteralText"/>); any other type prints with the codec's
    /// <see cref="IBaseValueCodec{T}.Format"/>, which for integers, booleans and unit is egglog's text too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Register the type in the e-graph's <see cref="EGraph.BaseValues"/> before declaring functions over this sort.
    /// </para>
    /// <para>
    /// Immutable and thread-safe.
    /// </para>
    /// </remarks>
    /// <typeparam name="TCodec">The codec, which identifies the base value type.</typeparam>
    /// <typeparam name="T">The .NET type of the values.</typeparam>
    internal sealed class BaseSort<TCodec, T> : Sort
        where TCodec : struct, IBaseValueCodec<T>
        where T : notnull, IEquatable<T>
    {

        readonly Func<T, string>? format;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <param name="format">Renders a value as literal text; <c>null</c> for egglog's literal text (see the class
        /// remarks).</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        public BaseSort(string name, Func<T, string>? format = null)
            : base(name, SortKind.Base)
        {
            this.format = format;
        }

        /// <summary>
        /// Renders a value of this sort as literal text: the formatter given to the constructor, or egglog's literal text
        /// (see the class remarks).
        /// </summary>
        /// <param name="baseValues">The registry the value was boxed in.</param>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        public string Format(BaseValues baseValues, Value value)
        {
            ArgumentNullException.ThrowIfNull(baseValues);

            var unwrapped = baseValues.Unwrap<TCodec, T>(value);
            if (format is not null)
                return format(unwrapped);

            // The type tests are JIT-time constants: TCodec is a struct, so every instantiation is specialized.
            if (typeof(T) == typeof(string))
                return LiteralText.String((string)(object)unwrapped);

            if (typeof(T) == typeof(double))
                return LiteralText.Float((double)(object)unwrapped);

            return TCodec.Format(unwrapped);
        }

        /// <inheritdoc />
        internal override bool FitsColumn(EGraph egraph, ColumnTy type)
        {
            if (type.IsBase == false)
                return false;

            // A column of an unregistered codec cannot exist, so an unregistered codec fits no column. The registry only
            // throws for that case; this runs once per declared column.
            try
            {
                return egraph.BaseValues.GetTypeId<TCodec, T>() == type.BaseType;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <inheritdoc />
        internal override string FormatValue(BaseValues baseValues, Value value) => Format(baseValues, value);

        /// <inheritdoc />
        internal override TermId ReconstructBase(BaseValues baseValues, Value value, TermDag dag)
        {
            return dag.Lit(new TermLiteral(baseValues.GetTypeId<TCodec, T>(), value, Format(baseValues, value)));
        }

    }

}
