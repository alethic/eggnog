using System;
using System.Collections.Generic;
using System.Text;

namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// Reads separated text as BenchmarkDotNet's CSV exporter writes it: one record per line, fields separated by the
    /// separator, a field that holds the separator, a quote or a line break enclosed in double quotes, and a quote inside
    /// such a field doubled. The exporter separates fields with the current culture's list separator, a comma in the
    /// invariant and English cultures and a semicolon in many others (<see cref="DetectSeparator"/>).
    /// </summary>
    internal static class CsvParser
    {

        /// <summary>
        /// Gets the separator of a report's text: the first comma or semicolon of its first line, which is the header,
        /// whose column names hold neither.
        /// </summary>
        /// <param name="text">The CSV text.</param>
        /// <returns>The separator; a comma when the first line has neither.</returns>
        public static char DetectSeparator(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            foreach (var c in text)
            {
                if (c is ',' or ';')
                    return c;

                if (c is '\r' or '\n')
                    break;
            }

            return ',';
        }

        /// <summary>
        /// Splits <paramref name="text"/> into records of fields. Blank lines are skipped; <c>\r\n</c>, <c>\n</c> and
        /// <c>\r</c> all end a record outside quotes. O(length of the text).
        /// </summary>
        /// <param name="text">The CSV text.</param>
        /// <param name="separator">The character between fields.</param>
        /// <returns>The records, each an array of its fields.</returns>
        /// <exception cref="FormatException">A quoted field is not closed, or a closing quote is followed by
        /// something other than the separator or the end of the record.</exception>
        public static IReadOnlyList<string[]> Parse(string text, char separator = ',')
        {
            ArgumentNullException.ThrowIfNull(text);

            var records = new List<string[]>();
            var fields = new List<string>();
            var field = new StringBuilder();
            var i = 0;
            var line = 1;
            while (i <= text.Length)
            {
                // The start of a field.
                if (i < text.Length && text[i] == '"')
                {
                    i++;
                    while (true)
                    {
                        if (i >= text.Length)
                            throw new FormatException($"A quoted field that starts on line {line} is not closed.");

                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                field.Append('"');
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        if (text[i] == '\n')
                            line++;

                        field.Append(text[i++]);
                    }

                    if (i < text.Length && text[i] != separator && text[i] != '\r' && text[i] != '\n')
                        throw new FormatException($"A quoted field on line {line} is followed by '{text[i]}' instead of '{separator}'.");
                }
                else
                {
                    while (i < text.Length && text[i] != separator && text[i] != '\r' && text[i] != '\n')
                        field.Append(text[i++]);
                }

                fields.Add(field.ToString());
                field.Clear();

                if (i < text.Length && text[i] == separator)
                {
                    i++;
                    continue;
                }

                // The end of a record: a line break or the end of the text.
                if (fields.Count > 1 || fields[0].Length > 0)
                    records.Add(fields.ToArray());

                fields.Clear();
                if (i < text.Length && text[i] == '\r')
                    i++;

                if (i < text.Length && text[i] == '\n')
                    i++;

                line++;
                if (i >= text.Length)
                    break;
            }

            return records;
        }

    }

}
