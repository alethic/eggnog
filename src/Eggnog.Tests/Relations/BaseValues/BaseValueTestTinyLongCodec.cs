using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A user-defined codec over <see cref="long"/>, distinct from <see cref="Int64Codec"/>: it boxes only
    /// <c>[0, 16)</c> inline and interns the rest with <see cref="BaseValues.InternedTag"/>, following the contract of
    /// <see cref="IBaseValueCodec{T}"/>. Used to show that two codecs over one .NET type are two base value types.
    /// </summary>
    internal readonly struct BaseValueTestTinyLongCodec : IBaseValueCodec<long>
    {

        public static bool MayUnbox => true;

        public static bool TryBox(long value, out Value boxed)
        {
            if ((ulong)value < 16)
            {
                boxed = new Value((uint)value);
                return true;
            }

            boxed = default;
            return false;
        }

        public static bool TryUnbox(Value boxed, [MaybeNullWhen(false)] out long value)
        {
            if (boxed.Rep < BaseValues.InternedTag)
            {
                value = boxed.Rep;
                return true;
            }

            value = 0;
            return false;
        }

        public static string Format(long value) => "tiny:" + value.ToString(CultureInfo.InvariantCulture);

    }

}
