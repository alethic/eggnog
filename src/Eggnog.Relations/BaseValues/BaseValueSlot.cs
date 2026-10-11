using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A process-wide slot number for one base value type (a codec and its value type), which every
    /// <see cref="BaseValues"/> uses to index its tables directly. It replaces egglog's per-call
    /// <c>HashMap&lt;TypeId, BaseValueId&gt;</c> lookup (core-relations/src/base_values/mod.rs:67, 82-84).
    /// </summary>
    /// <remarks>
    /// Slots are allocated once per type, on first use, in no particular order; they are not
    /// <see cref="BaseValueTypeId"/>s, which each registry assigns in registration order. The field is
    /// <c>static readonly</c>, so the JIT folds it to a constant in optimized code.
    /// </remarks>
    /// <typeparam name="TCodec">The codec.</typeparam>
    /// <typeparam name="T">The .NET type of the base values.</typeparam>
    internal static class BaseValueSlot<TCodec, T>
        where TCodec : struct, IBaseValueCodec<T>
        where T : notnull, IEquatable<T>
    {

        /// <summary>
        /// The slot of this base value type.
        /// </summary>
        public static readonly int Index = BaseValues.AllocateSlot();

    }

}
