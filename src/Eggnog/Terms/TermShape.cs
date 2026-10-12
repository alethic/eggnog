using System;

namespace Eggnog
{

    /// <summary>
    /// The structure of a term, as the compiler, the host batch and the renderer read it: one shape for typed and
    /// untyped terms alike.
    /// </summary>
    internal readonly struct TermShape
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kind">What the term is.</param>
        /// <param name="clrType">The CLR type of its values.</param>
        /// <param name="name">A variable's name.</param>
        /// <param name="owner">A variable's builder.</param>
        /// <param name="constantType">The column type of an untyped constant or variable; <c>null</c> when the graph
        /// resolves it from <paramref name="clrType"/>.</param>
        /// <param name="constant">A constant's value.</param>
        /// <param name="table">An application's table.</param>
        /// <param name="primitive">A computation's primitive.</param>
        /// <param name="arguments">The arguments of an application or computation.</param>
        public TermShape(TermShapeKind kind, Type clrType, string? name, object? owner, ColumnType? constantType, object? constant,
            TableHandle? table, Primitive? primitive, Term[] arguments)
        {
            Kind = kind;
            ClrType = clrType;
            Name = name;
            Owner = owner;
            ConstantType = constantType;
            Constant = constant;
            Table = table;
            Primitive = primitive;
            Arguments = arguments;
        }

        /// <summary>
        /// Gets what the term is.
        /// </summary>
        public TermShapeKind Kind { get; }

        /// <summary>
        /// Gets the CLR type of the term's values.
        /// </summary>
        public Type ClrType { get; }

        /// <summary>
        /// Gets a variable's name.
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// Gets a variable's builder.
        /// </summary>
        public object? Owner { get; }

        /// <summary>
        /// Gets the column type of an untyped constant or variable; <c>null</c> for typed terms.
        /// </summary>
        public ColumnType? ConstantType { get; }

        /// <summary>
        /// Gets a constant's value.
        /// </summary>
        public object? Constant { get; }

        /// <summary>
        /// Gets an application's table.
        /// </summary>
        public TableHandle? Table { get; }

        /// <summary>
        /// Gets a computation's primitive.
        /// </summary>
        public Primitive? Primitive { get; }

        /// <summary>
        /// Gets the arguments of an application or computation; empty otherwise.
        /// </summary>
        public Term[] Arguments { get; }

    }

}
