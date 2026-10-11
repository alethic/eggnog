using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The type of a column of a function: egglog-bridge's <c>ColumnTy</c> (egglog-bridge/src/lib.rs:98-102). Either an
    /// e-class id (<see cref="Id"/>), which the union-find canonicalizes and a rebuild rewrites (this covers containers
    /// and every sort the frontend treats as an id), or a base value of a registered type (<see cref="Base"/>), which is
    /// never canonicalized.
    /// </summary>
    /// <remarks>
    /// A four-byte value: zero for <see cref="Id"/> (so <c>default</c> is the id type), otherwise the base type's index
    /// plus one. Equality is structural.
    /// </remarks>
    public readonly record struct ColumnTy
    {

        readonly int code;

        ColumnTy(int code)
        {
            this.code = code;
        }

        /// <summary>
        /// Gets the type of an e-class id column: egglog's <c>ColumnTy::Id</c>. Also <c>default(ColumnTy)</c>.
        /// </summary>
        public static ColumnTy Id => default;

        /// <summary>
        /// Creates the type of a column of base values of type <paramref name="type"/>: egglog's <c>ColumnTy::Base</c>.
        /// </summary>
        /// <param name="type">The base value type.</param>
        /// <returns>The column type.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is negative or <see cref="int.MaxValue"/>.</exception>
        public static ColumnTy Base(BaseValueTypeId type)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(type.Rep, nameof(type));
            ArgumentOutOfRangeException.ThrowIfEqual(type.Rep, int.MaxValue, nameof(type));
            return new(type.Rep + 1);
        }

        /// <summary>
        /// Gets whether this is the type of an e-class id column.
        /// </summary>
        public bool IsId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => code == 0;
        }

        /// <summary>
        /// Gets whether this is the type of a base value column.
        /// </summary>
        public bool IsBase
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => code != 0;
        }

        /// <summary>
        /// Gets the base value type of a base value column.
        /// </summary>
        /// <exception cref="InvalidOperationException">This is the type of an id column.</exception>
        public BaseValueTypeId BaseType => code != 0 ? new BaseValueTypeId(code - 1) : throw new InvalidOperationException("An id column has no base value type.");

        /// <inheritdoc />
        public override string ToString() => code == 0 ? "Id" : "Base(" + (code - 1) + ")";

    }

}
