using System;
using System.Collections.Generic;

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
    /// Extraction's tie-break (rule-layer decision D10) orders base values by content, which must not depend on the
    /// machine or the thread (decision 4): by the comparer given here; else ordinally for <see cref="string"/> (never by
    /// the current culture's collation); else by <see cref="Comparer{T}.Default"/> when <typeparamref name="T"/>
    /// implements <see cref="IComparable{T}"/>, which for the built-in numeric, boolean and character types is
    /// culture-invariant. A type whose own order depends on the culture should be given a comparer. The boxed
    /// representation breaks the remaining ties: values the order calls equal, and every value of an incomparable type.
    /// </para>
    /// <para>
    /// Immutable and thread-safe.
    /// </para>
    /// </remarks>
    /// <typeparam name="TCodec">The codec, which identifies the base value type.</typeparam>
    /// <typeparam name="T">The .NET type of the values.</typeparam>
    public sealed class BaseSort<TCodec, T> : Sort
        where TCodec : struct, IBaseValueCodec<T>
        where T : notnull, IEquatable<T>
    {

        /// <summary>
        /// Whether <typeparamref name="T"/> has a content order; a JIT-time constant per instantiation.
        /// </summary>
        static readonly bool Comparable = typeof(IComparable<T>).IsAssignableFrom(typeof(T));

        readonly Func<T, string>? format;
        readonly IComparer<T>? comparer;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <param name="format">Renders a value as literal text; <c>null</c> for egglog's literal text (see the class
        /// remarks).</param>
        /// <param name="comparer">The content order of values for extraction's tie-break; <c>null</c> for the default
        /// (see the class remarks). It must not depend on the current culture or on anything else that varies between
        /// runs.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        public BaseSort(string name, Func<T, string>? format = null, IComparer<T>? comparer = null)
            : base(name, SortKind.Base)
        {
            this.format = format;
            this.comparer = comparer;
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
        /// <remarks>
        /// Culture-invariant: strings compare ordinally (<see cref="string.CompareOrdinal(string, string)"/>), because
        /// <see cref="Comparer{T}.Default"/> would use the current culture's collation and make the extracted term depend
        /// on the machine and the thread.
        /// </remarks>
        internal override int CompareValues(BaseValues baseValues, Value left, Value right)
        {
            var order = 0;
            if (comparer is not null)
            {
                order = comparer.Compare(baseValues.Unwrap<TCodec, T>(left), baseValues.Unwrap<TCodec, T>(right));
            }
            else if (typeof(T) == typeof(string))
            {
                var leftText = (string)(object)baseValues.Unwrap<TCodec, T>(left);
                var rightText = (string)(object)baseValues.Unwrap<TCodec, T>(right);
                order = string.CompareOrdinal(leftText, rightText);
            }
            else if (Comparable)
            {
                order = Comparer<T>.Default.Compare(baseValues.Unwrap<TCodec, T>(left), baseValues.Unwrap<TCodec, T>(right));
            }

            return order != 0 ? order : left.Rep.CompareTo(right.Rep);
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
