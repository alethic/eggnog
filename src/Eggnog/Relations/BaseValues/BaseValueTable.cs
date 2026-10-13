using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The type-erased face of one registered base value type's intern table: egglog's <c>DynamicInternTable</c> trait
    /// object (core-relations/src/base_values/mod.rs:126-131). <see cref="BaseValues"/> keeps one per registered type,
    /// indexed by <see cref="BaseValueTypeId"/>.
    /// </summary>
    /// <remarks>
    /// The only virtual members are <see cref="Format"/>, a diagnostics path, and <see cref="InternedCount"/>, which
    /// <see cref="BaseValues.InternedCount"/> sums on a cold path. Boxing and unboxing go through the sealed
    /// <see cref="BaseInternTable{TCodec, T}"/>, never through this class. Instances are immutable apart from the intern
    /// table they own, and are shared between a <see cref="BaseValues"/> and its clones, as egglog's
    /// <c>Arc</c>-backed <c>InternTable</c> is.
    /// </remarks>
    internal abstract class BaseValueTable
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id">The id of the type in the registry that created this table.</param>
        /// <param name="codecType">The codec type, which identifies the base value type.</param>
        /// <param name="valueType">The .NET type of the base values.</param>
        protected BaseValueTable(BaseValueTypeId id, Type codecType, Type valueType)
        {
            Id = id;
            CodecType = codecType;
            ValueType = valueType;
        }

        /// <summary>
        /// Gets the id of the type. Ids are assigned in registration order and are the same in every clone of the
        /// registry that created the table, because clones copy the id assignment.
        /// </summary>
        public BaseValueTypeId Id { get; }

        /// <summary>
        /// Gets the codec type, which identifies the base value type.
        /// </summary>
        public Type CodecType { get; }

        /// <summary>
        /// Gets the .NET type of the base values.
        /// </summary>
        public Type ValueType { get; }

        /// <summary>
        /// Gets the number of base values interned in this table. Values stored inline are not counted: they never
        /// touch the table. Lock-free; never decreases, because interned values are never freed.
        /// </summary>
        public abstract int InternedCount { get; }

        /// <summary>
        /// Formats the base value stored as <paramref name="value"/>: egglog's <c>print_value</c>. Cold path.
        /// </summary>
        /// <param name="value">A value of this type.</param>
        /// <returns>The text.</returns>
        public abstract string Format(Value value);

    }

}
