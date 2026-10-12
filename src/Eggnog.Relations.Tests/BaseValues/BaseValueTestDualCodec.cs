using System.Globalization;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A codec that (wrongly) describes two value types, used to show that a codec can be registered for only one.
    /// </summary>
    internal readonly struct BaseValueTestDualCodec : IBaseValueCodec<int>, IBaseValueCodec<long>
    {

        public static bool MayUnbox => false;

        public static bool TryBox(int value, out Value boxed)
        {
            boxed = default;
            return false;
        }

        public static bool TryUnbox(Value boxed, out int value)
        {
            value = 0;
            return false;
        }

        public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

        static bool IBaseValueCodec<long>.MayUnbox => false;

        static bool IBaseValueCodec<long>.TryBox(long value, out Value boxed)
        {
            boxed = default;
            return false;
        }

        static bool IBaseValueCodec<long>.TryUnbox(Value boxed, out long value)
        {
            value = 0;
            return false;
        }

        static string IBaseValueCodec<long>.Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    }

}
