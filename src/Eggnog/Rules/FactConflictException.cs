using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog
{

    /// <summary>
    /// A host write that would give a function without a lattice a second value for one key, against the stored value or
    /// another write of the same batch. Nothing is written.
    /// </summary>
    /// <remarks>
    /// A union of the batch (a host <c>Union</c>, a sort equality, or a constructor write that gives an existing key
    /// another class) can also make two keys of such a function equal, directly or through the congruences it causes;
    /// when their values differ, that is this exception too, found before anything is staged.
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
            : base(Describe(table, key, storedValue, newValue))
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
        /// Gets the stored value, or the batch's earlier one.
        /// </summary>
        public object StoredValue { get; }

        /// <summary>
        /// Gets the conflicting value.
        /// </summary>
        public object NewValue { get; }

        /// <summary>
        /// Checks the arguments, then builds the message.
        /// </summary>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        static string Describe(TableHandle table, IReadOnlyList<object> key, object storedValue, object newValue)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(storedValue);
            ArgumentNullException.ThrowIfNull(newValue);
            var keys = string.Join(" ", key.Select((k, i) => Format(table.Keys, i, k)));
            var stored = Format(table.Output, storedValue);
            var written = Format(table.Output, newValue);
            return $"Function '{table.Name}' has no lattice and already holds {stored} at ({table.Name}{(keys.Length > 0 ? " " + keys : "")}); {written} conflicts with it. The fact was not written.";
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
