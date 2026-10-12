namespace Eggnog.Tests.Support
{

    /// <summary>
    /// An enum with a negative member, for the enum column tests.
    /// </summary>
    public enum Shade
    {

        /// <summary>A negative member.</summary>
        Dark = -1,

        /// <summary>Zero.</summary>
        Plain = 0,

        /// <summary>A large member, interned rather than inline as an i64.</summary>
        Bright = int.MaxValue,

    }

}
