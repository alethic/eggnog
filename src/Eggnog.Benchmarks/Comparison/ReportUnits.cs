using System;
using System.Globalization;

namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// Reads and writes the values of BenchmarkDotNet's CSV reports: times with a unit (<c>4.244 ms</c>) and sizes with
    /// a unit (<c>3360 B</c>, <c>9.61 GB</c>), written in the invariant culture, with commas grouping thousands.
    /// </summary>
    internal static class ReportUnits
    {

        /// <summary>
        /// Parses a time cell into nanoseconds. A cell without a unit is read as nanoseconds.
        /// </summary>
        /// <param name="cell">The cell, for example <c>4.244 ms</c>, <c>908.1 μs</c> or <c>NA</c>.</param>
        /// <returns>The time in nanoseconds; <see cref="double.NaN"/> for an empty cell, <c>NA</c> or <c>-</c>.</returns>
        /// <exception cref="FormatException">The cell is neither a number with a known time unit nor a marker of no
        /// value.</exception>
        public static double ParseNanoseconds(string cell)
        {
            ArgumentNullException.ThrowIfNull(cell);

            if (TrySplit(cell, out var number, out var unit) == false)
                return double.NaN;

            var scale = unit switch
            {
                "" or "ns" => 1.0,
                "us" or "μs" or "µs" => 1e3,
                "ms" => 1e6,
                "s" => 1e9,
                "m" => 60e9,
                "h" => 3600e9,
                _ => throw new FormatException($"'{cell}' has an unknown time unit '{unit}'."),
            };

            return number * scale;
        }

        /// <summary>
        /// Parses a size cell into bytes, with BenchmarkDotNet's binary multiples (1 KB = 1024 B). A cell without a unit
        /// is read as bytes.
        /// </summary>
        /// <param name="cell">The cell, for example <c>3360 B</c>, <c>1.5 KB</c> or <c>-</c>.</param>
        /// <returns>The size in bytes; <see cref="double.NaN"/> for an empty cell, <c>NA</c> or <c>-</c>.</returns>
        /// <exception cref="FormatException">The cell is neither a number with a known size unit nor a marker of no
        /// value.</exception>
        public static double ParseBytes(string cell)
        {
            ArgumentNullException.ThrowIfNull(cell);

            if (TrySplit(cell, out var number, out var unit) == false)
                return double.NaN;

            var scale = unit switch
            {
                "" or "B" => 1.0,
                "KB" => 1024.0,
                "MB" => 1024.0 * 1024,
                "GB" => 1024.0 * 1024 * 1024,
                "TB" => 1024.0 * 1024 * 1024 * 1024,
                _ => throw new FormatException($"'{cell}' has an unknown size unit '{unit}'."),
            };

            return number * scale;
        }

        /// <summary>
        /// Formats nanoseconds with the largest unit that keeps the number at least 1, to four significant places.
        /// </summary>
        /// <param name="nanoseconds">The time.</param>
        /// <returns>The text, for example <c>4.244 ms</c>; <c>-</c> for <see cref="double.NaN"/>.</returns>
        public static string FormatNanoseconds(double nanoseconds)
        {
            if (double.IsNaN(nanoseconds))
                return "-";

            var magnitude = Math.Abs(nanoseconds);
            var (scale, unit) = magnitude >= 1e9 ? (1e9, "s") : magnitude >= 1e6 ? (1e6, "ms") : magnitude >= 1e3 ? (1e3, "us") : (1.0, "ns");
            return (nanoseconds / scale).ToString("0.000", CultureInfo.InvariantCulture) + " " + unit;
        }

        /// <summary>
        /// Splits a cell into its number and its unit.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="number">The number, with thousands separators removed.</param>
        /// <param name="unit">The unit: the text after the number, trimmed; empty when there is none.</param>
        /// <returns><c>false</c> for an empty cell, <c>NA</c> or <c>-</c>.</returns>
        /// <exception cref="FormatException">The cell does not start with a number.</exception>
        static bool TrySplit(string cell, out double number, out string unit)
        {
            var text = cell.Trim();
            number = double.NaN;
            unit = "";
            if (text.Length == 0 || text == "NA" || text == "-" || text == "?")
                return false;

            var end = 0;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or ',' or '-' or '+' or 'e' or 'E'))
                end++;

            // An exponent marker must be followed by a digit or a sign to belong to the number.
            while (end > 0 && text[end - 1] is 'e' or 'E')
                end--;

            var digits = text.Substring(0, end).Replace(",", "", StringComparison.Ordinal);
            if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out number) == false)
                throw new FormatException($"'{cell}' does not start with a number.");

            unit = text.Substring(end).Trim();
            return true;
        }

    }

}
