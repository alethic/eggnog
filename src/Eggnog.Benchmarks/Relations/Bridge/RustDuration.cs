using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Measures and formats elapsed time the way the egglog-bridge examples print it: Rust's <c>{:?}</c> of a
    /// <c>Duration</c> taken from <c>Instant::now()</c>, so that the example mode (<see cref="BridgeExampleRunner"/>)
    /// prints the same lines as <c>math.exe</c> and <c>ac.exe</c>.
    /// </summary>
    /// <remarks>
    /// Rust's <c>Debug for Duration</c> (library/core/src/time.rs) picks the largest unit the duration reaches (whole
    /// seconds, else milliseconds, else microseconds, else nanoseconds) and prints the integer part, then the exact
    /// fraction with trailing zeros removed (no rounding without a precision), then the unit: <c>9.0456298s</c>,
    /// <c>279.7217ms</c>, <c>908.1µs</c>, <c>6.4µs</c>, <c>700ns</c>. The microsecond unit is <c>µs</c> with U+00B5 MICRO
    /// SIGN. On Windows both <c>Instant</c> and <see cref="Stopwatch"/> read QueryPerformanceCounter, so both resolve
    /// 100 ns.
    /// </remarks>
    internal static class RustDuration
    {

        const long NanosPerSecond = 1_000_000_000;
        const int NanosPerMilli = 1_000_000;
        const int NanosPerMicro = 1_000;

        /// <summary>
        /// Gets the nanoseconds elapsed since <paramref name="startTimestamp"/>, a <see cref="Stopwatch.GetTimestamp"/>
        /// reading: Rust's <c>start.elapsed()</c>.
        /// </summary>
        /// <param name="startTimestamp">The start, from <see cref="Stopwatch.GetTimestamp"/>.</param>
        /// <returns>The elapsed nanoseconds, truncated.</returns>
        public static long ElapsedNanoseconds(long startTimestamp)
        {
            var ticks = Stopwatch.GetTimestamp() - startTimestamp;
            return (long)((Int128)ticks * NanosPerSecond / Stopwatch.Frequency);
        }

        /// <summary>
        /// Formats <paramref name="nanoseconds"/> as Rust's <c>{:?}</c> formats a <c>Duration</c> of that length.
        /// </summary>
        /// <param name="nanoseconds">The duration in nanoseconds; not negative.</param>
        /// <returns>The text, such as <c>9.0456298s</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="nanoseconds"/> is negative.</exception>
        public static string Format(long nanoseconds)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(nanoseconds);

            var seconds = nanoseconds / NanosPerSecond;
            var nanos = (int)(nanoseconds % NanosPerSecond);
            if (seconds > 0)
                return FormatDecimal(seconds, nanos, (int)(NanosPerSecond / 10), "s");

            if (nanos >= NanosPerMilli)
                return FormatDecimal(nanos / NanosPerMilli, nanos % NanosPerMilli, NanosPerMilli / 10, "ms");

            if (nanos >= NanosPerMicro)
                return FormatDecimal(nanos / NanosPerMicro, nanos % NanosPerMicro, NanosPerMicro / 10, "µs");

            return FormatDecimal(nanos, 0, 1, "ns");
        }

        /// <summary>
        /// Rust's <c>fmt_decimal</c> without a precision: the integer part, then the fraction's digits until it is
        /// exhausted (so no trailing zeros, and no dot for a whole number), then the unit.
        /// </summary>
        /// <param name="integerPart">The whole units.</param>
        /// <param name="fractionalPart">The remainder, in units of the next power of 10 below one unit.</param>
        /// <param name="divisor">The place value of the first fractional digit, in the same units as
        /// <paramref name="fractionalPart"/>.</param>
        /// <param name="unit">The unit suffix.</param>
        /// <returns>The text.</returns>
        static string FormatDecimal(long integerPart, int fractionalPart, int divisor, string unit)
        {
            var builder = new StringBuilder(24);
            builder.Append(integerPart.ToString(CultureInfo.InvariantCulture));
            if (fractionalPart > 0)
            {
                builder.Append('.');
                while (fractionalPart > 0 && divisor > 0)
                {
                    builder.Append((char)('0' + fractionalPart / divisor));
                    fractionalPart %= divisor;
                    divisor /= 10;
                }
            }

            builder.Append(unit);
            return builder.ToString();
        }

    }

}
