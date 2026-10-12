using System;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A term of a random program: a variable, a constant (for the sort, the class of a host-made term, by handle), an
    /// application of a function or constructor, or a computation. Immutable.
    /// </summary>
    internal sealed class DiffTerm
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        DiffTerm(DiffTermKind kind, DiffType type, string? name, object? value, DiffTable? table, DiffPrimitive? primitive, DiffTerm[] arguments)
        {
            Kind = kind;
            Type = type;
            Name = name;
            Value = value;
            Table = table;
            Primitive = primitive;
            Arguments = arguments;
        }

        /// <summary>Gets what the term is.</summary>
        public DiffTermKind Kind { get; }

        /// <summary>Gets the term's type.</summary>
        public DiffType Type { get; }

        /// <summary>Gets a variable's name.</summary>
        public string? Name { get; }

        /// <summary>Gets a constant's value: a <c>long</c>, <c>string</c> or <c>ValueSet&lt;int&gt;</c>, or for the sort the
        /// handle (an <c>int</c>) of a host-made term.</summary>
        public object? Value { get; }

        /// <summary>Gets an application's table.</summary>
        public DiffTable? Table { get; }

        /// <summary>Gets a computation's primitive.</summary>
        public DiffPrimitive? Primitive { get; }

        /// <summary>Gets an application's or computation's arguments.</summary>
        public DiffTerm[] Arguments { get; }

        /// <summary>
        /// A variable.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="type">The type.</param>
        /// <returns>The term.</returns>
        public static DiffTerm Var(string name, DiffType type) => new(DiffTermKind.Var, type, name, null, null, null, Array.Empty<DiffTerm>());

        /// <summary>
        /// A value constant.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="value">The value.</param>
        /// <returns>The term.</returns>
        public static DiffTerm Const(DiffType type, object value) => new(DiffTermKind.Const, type, null, value, null, null, Array.Empty<DiffTerm>());

        /// <summary>
        /// A class constant: the class of the host-made term <paramref name="handle"/>.
        /// </summary>
        /// <param name="handle">The handle, by order of the program's <c>Make</c> commands.</param>
        /// <returns>The term.</returns>
        public static DiffTerm Handle(int handle) => new(DiffTermKind.Const, DiffType.Node, null, handle, null, null, Array.Empty<DiffTerm>());

        /// <summary>
        /// An application.
        /// </summary>
        /// <param name="table">A function or constructor.</param>
        /// <param name="arguments">One term per key.</param>
        /// <returns>The term.</returns>
        public static DiffTerm App(DiffTable table, params DiffTerm[] arguments) =>
            new(DiffTermKind.App, table.Output!.Value, null, null, table, null, arguments);

        /// <summary>
        /// A computation.
        /// </summary>
        /// <param name="primitive">The primitive.</param>
        /// <param name="arguments">One term per parameter.</param>
        /// <returns>The term.</returns>
        public static DiffTerm Prim(DiffPrimitive primitive, params DiffTerm[] arguments) =>
            new(DiffTermKind.Prim, primitive.Result, null, null, null, primitive, arguments);

        /// <summary>Gets whether the term is an application of a constructor.</summary>
        public bool IsConstructor => Kind == DiffTermKind.App && Table!.Kind == TableKind.Constructor;

        /// <summary>Gets whether the term is an application of a function.</summary>
        public bool IsFunction => Kind == DiffTermKind.App && Table!.Kind == TableKind.Function;

        /// <inheritdoc />
        public override string ToString() => ProgramPrinter.Term(this);

    }

}
