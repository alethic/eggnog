using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog
{

    /// <summary>
    /// A host write that would give a function without a lattice a second value for one key, against the stored value or
    /// another write of the same batch. The write that conflicts is not made.
    /// </summary>
    /// <remarks>
    /// A union of the batch (a host <c>Union</c>, a sort equality, a constructor write that gives an existing key another
    /// class, or a primitive's union) can also make two keys of such a function equal, directly or through the
    /// congruences it causes; when their values differ, that is this exception too, as egglog's rebuild after the command
    /// panics: the union stands, the rebuild keeps one of the rows, whose value is <see cref="StoredValue"/>, and the
    /// fact fails after it, with the rebuild's failure as the inner exception.
    /// </remarks>
    public sealed class FactConflictException : InvalidOperationException
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The function.</param>
        /// <param name="key">The decoded keys.</param>
        /// <param name="storedValue">The stored value, or the batch's earlier one.</param>
        /// <param name="newValue">The conflicting value.</param>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        public FactConflictException(TableHandle table, IReadOnlyList<object> key, object storedValue, object newValue)
            : base(Describe(table, key, storedValue, newValue, false))
        {
            Table = table;
            Key = key is object[] array ? Array.AsReadOnly(array) : key;
            StoredValue = storedValue;
            NewValue = newValue;
        }

        /// <summary>
        /// Initializes a new instance for two rows that a merge of classes made meet, after the rebuild kept one.
        /// </summary>
        /// <param name="table">The function.</param>
        /// <param name="key">The decoded keys, canonical after the rebuild.</param>
        /// <param name="storedValue">The value of the row the rebuild kept.</param>
        /// <param name="newValue">The value of the other row.</param>
        /// <param name="innerException">The rebuild's failure.</param>
        internal FactConflictException(TableHandle table, IReadOnlyList<object> key, object storedValue, object newValue, Exception innerException)
            : base(Describe(table, key, storedValue, newValue, true), innerException)
        {
            Table = table;
            Key = key is object[] array ? Array.AsReadOnly(array) : key;
            StoredValue = storedValue;
            NewValue = newValue;
        }

        /// <summary>
        /// Gets the function.
        /// </summary>
        public TableHandle Table { get; }

        /// <summary>
        /// Gets the decoded keys.
        /// </summary>
        public IReadOnlyList<object> Key { get; }

        /// <summary>
        /// Gets the stored value, or the batch's earlier one; for rows a merge of classes made meet, the value of the row
        /// the rebuild kept.
        /// </summary>
        public object StoredValue { get; }

        /// <summary>
        /// Gets the conflicting value; for rows a merge of classes made meet, the value of the other row.
        /// </summary>
        public object NewValue { get; }

        /// <summary>
        /// Checks the arguments, then builds the message.
        /// </summary>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        static string Describe(TableHandle table, IReadOnlyList<object> key, object storedValue, object newValue, bool merged)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(storedValue);
            ArgumentNullException.ThrowIfNull(newValue);
            var keys = string.Join(" ", key.Select((k, i) => Format(table.Keys, i, k)));
            var stored = Format(table.Output, storedValue);
            var written = Format(table.Output, newValue);
            var at = $"({table.Name}{(keys.Length > 0 ? " " + keys : "")})";
            return merged
                ? $"Function '{table.Name}' has no lattice, and a merge of classes made its rows of {stored} and {written} meet at {at}; the merge stands, and the rebuild kept {stored}."
                : $"Function '{table.Name}' has no lattice and already holds {stored} at {at}; {written} conflicts with it, and was not written.";
        }

        /// <summary>
        /// Formats one key with its column type when possible.
        /// </summary>
        static string Format(IReadOnlyList<ColumnType> keys, int index, object? value) => Format(index < keys.Count ? keys[index] : null, value);

        /// <summary>
        /// Formats a value with a column type when it is one of the type's values, and with <c>ToString</c> otherwise.
        /// </summary>
        static string Format(ColumnType? type, object? value)
        {
            if (value is null)
                return "null";

            try
            {
                return type is not null ? type.Format(value) : value.ToString() ?? "";
            }
            catch (ArgumentException)
            {
                return value.ToString() ?? "";
            }
        }

    }

}
