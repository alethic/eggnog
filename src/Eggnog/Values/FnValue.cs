using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// The content of a function value (<see cref="UnstableFn{T1, TResult}"/> and its arities): the function it applies
    /// and its partially applied arguments, egglog's <c>FunctionContainer</c> (src/sort/fn.rs:21-55), equal by both as
    /// egglog's are. Immutable.
    /// </summary>
    internal sealed class FnValue : IEquatable<FnValue>
    {

        /// <summary>
        /// The order of distinct targets of one name, by the order they were first compared in.
        /// </summary>
        static readonly ConditionalWeakTable<object, object> TargetOrder = new();
        static long targetCount;

        readonly object[] arguments;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="target">The table or the primitive.</param>
        /// <param name="arguments">The partially applied arguments, owned by the value from here.</param>
        FnValue(object target, object[] arguments)
        {
            Target = target;
            this.arguments = arguments;
        }

        /// <summary>
        /// Gets the table or the primitive.
        /// </summary>
        public object Target { get; }

        /// <summary>
        /// Gets the name, egglog's: the table's or the primitive's.
        /// </summary>
        public string Name => Target is TableHandle table ? table.Name : ((Primitive)Target).Name;

        /// <summary>
        /// Gets the partially applied arguments.
        /// </summary>
        public IReadOnlyList<object> Arguments => arguments;

        /// <summary>
        /// Gets the partially applied arguments as an array, not to be changed.
        /// </summary>
        internal object[] ArgumentArray => arguments;

        /// <summary>
        /// Makes a function value of a target whose parameters end with <paramref name="inputs"/> and whose result is
        /// <paramref name="result"/>, partially applied to <paramref name="partial"/>, its first parameters.
        /// </summary>
        /// <param name="target">A constructor or a function, or a primitive.</param>
        /// <param name="partial">The partially applied arguments.</param>
        /// <param name="inputs">The CLR types of the function sort's inputs.</param>
        /// <param name="result">The CLR type of its output.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target is not a table or a primitive, its signature is not the partial
        /// arguments' then the inputs and the output, or an argument is not of its parameter's type.</exception>
        internal static FnValue Create(object target, ReadOnlySpan<object> partial, Type[] inputs, Type result)
        {
            var parameters = PartialTypes(target, inputs, result);
            if (parameters.Length != partial.Length)
                throw new ArgumentException($"'{NameOf(target)}' takes {parameters.Length} partially applied arguments before the inputs, not {partial.Length}.", nameof(partial));

            var arguments = new object[partial.Length];
            for (var i = 0; i < partial.Length; i++)
            {
                var argument = partial[i] ?? throw new ArgumentNullException(nameof(partial), $"Argument {i} is null.");
                if (parameters[i].IsInstanceOfType(argument) == false)
                    throw new ArgumentException($"Argument {i} of '{NameOf(target)}' is a {ValueStore.DisplayName(argument.GetType())}, not a {ValueStore.DisplayName(parameters[i])}.", nameof(partial));

                arguments[i] = argument;
            }

            return new FnValue(target, arguments);
        }

        /// <summary>
        /// Gets the types of a target's partially applied arguments for a function sort: the parameters before the
        /// sort's inputs, which must be its last, its result the sort's output.
        /// </summary>
        /// <param name="target">A constructor or a function, or a primitive.</param>
        /// <param name="inputs">The CLR types of the function sort's inputs.</param>
        /// <param name="result">The CLR type of its output.</param>
        /// <returns>The types.</returns>
        /// <exception cref="ArgumentException">The target is not a table or a primitive, or its signature does not end with
        /// the inputs and the output.</exception>
        internal static Type[] PartialTypes(object target, Type[] inputs, Type result)
        {
            ArgumentNullException.ThrowIfNull(target);
            var (parameters, output, name) = target switch
            {
                TableHandle { IsRelation: true } table => throw RelationIsNoTarget(table),
                TableHandle table when table.ReadScope is null => (Array.ConvertAll(table.KeyArray, type => type.ClrType), table.Output!.ClrType, table.Name),
                TableHandle table => throw new ArgumentException($"'{table}' is a scoped view, which a function value does not apply.", nameof(target)),
                Primitive primitive => ([.. primitive.ParameterTypes], primitive.ResultType, primitive.Name),
                _ => throw new ArgumentException($"A function value applies a constructor, a function or a primitive, not a {target.GetType().Name}.", nameof(target)),
            };

            var partial = parameters.Length - inputs.Length;
            if (partial < 0 || output != result)
                throw Mismatch(name, parameters, output, inputs, result);

            for (var i = 0; i < inputs.Length; i++)
                if (parameters[partial + i] != inputs[i])
                    throw Mismatch(name, parameters, output, inputs, result);

            return parameters[..partial];
        }

        /// <summary>
        /// Makes the error for a relation as a function value's target: egglog desugars a relation to a constructor of
        /// the fresh sort <c>@{relation}Sort</c> (src/ast/desugar.rs:398-425), a name reserved there, so no function sort
        /// names its output and <c>unstable-fn</c> never resolves to one.
        /// </summary>
        /// <param name="relation">The relation.</param>
        /// <returns>The error.</returns>
        internal static ArgumentException RelationIsNoTarget(TableHandle relation) =>
            new($"'{relation.Name}' is a relation, which a function value does not apply: egglog's relation outputs a sort of its own that no function sort names.", "target");

        /// <summary>
        /// Gets a target's name.
        /// </summary>
        static string NameOf(object target) => target is TableHandle table ? table.Name : ((Primitive)target).Name;

        /// <summary>
        /// Makes a function value of a resolved target and its decoded arguments.
        /// </summary>
        internal static FnValue Of(object target, object[] arguments) => new(target, arguments);

        /// <inheritdoc />
        public bool Equals(FnValue? other)
        {
            if (other is null || ReferenceEquals(Target, other.Target) == false || arguments.Length != other.arguments.Length)
                return false;

            for (var i = 0; i < arguments.Length; i++)
                if (EqualityComparer<object>.Default.Equals(arguments[i], other.arguments[i]) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is FnValue other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(RuntimeHelpers.GetHashCode(Target));
            foreach (var argument in arguments)
                hash.Add(argument);

            return hash.ToHashCode();
        }

        /// <summary>
        /// Compares in content order: by name, by code point, distinct targets of one name in the order they were first
        /// compared, then argument by argument as <see cref="Set{T}"/> orders its elements (<see cref="ValueOrder"/>).
        /// Zero exactly when the values are equal; lets function values be the elements of sets and multisets and the keys
        /// of maps.
        /// </summary>
        /// <param name="other">The other value.</param>
        /// <returns>Negative, zero or positive.</returns>
        /// <exception cref="ArgumentException">An argument is of a type no column type orders, and not
        /// comparable.</exception>
        public int CompareTo(FnValue other)
        {
            var order = Relations.CodePointComparer.Instance.Compare(Name, other.Name);
            if (order == 0 && ReferenceEquals(Target, other.Target) == false)
                order = OrderOf(Target).CompareTo(OrderOf(other.Target));

            if (order == 0)
                order = arguments.Length.CompareTo(other.arguments.Length);

            for (var i = 0; order == 0 && i < arguments.Length; i++)
                order = ValueOrder.Compare(arguments[i], other.arguments[i]);

            return order;
        }

        /// <summary>
        /// Renders the value for display: <c>(unstable-fn "name" args)</c>, its arguments as .NET formats them, numbers in
        /// the invariant culture. A graph renders it as egglog writes the term (<see cref="ColumnType.Format(object)"/>).
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var text = "(unstable-fn " + Relations.LiteralText.String(Name);
            foreach (var argument in arguments)
                text += " " + (argument is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : argument.ToString());

            return text + ")";
        }

        /// <summary>
        /// Gets a target's place among the targets compared so far.
        /// </summary>
        static long OrderOf(object target) =>
            (long)TargetOrder.GetValue(target, static _ => Interlocked.Increment(ref targetCount));

        /// <summary>
        /// Gets the refusal of a target whose signature is not the function value's.
        /// </summary>
        static ArgumentException Mismatch(string name, Type[] parameters, Type output, Type[] inputs, Type result) =>
            new($"function container lookup for '{name}' expected ({(inputs.Length == 0 ? "..." : "..., " + string.Join(", ", Array.ConvertAll(inputs, ValueStore.DisplayName)))}) -> {ValueStore.DisplayName(result)}, found ({string.Join(", ", Array.ConvertAll(parameters, ValueStore.DisplayName))}) -> {ValueStore.DisplayName(output)}.", "target");

    }

}
