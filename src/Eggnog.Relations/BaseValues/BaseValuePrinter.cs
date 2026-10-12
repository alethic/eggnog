namespace Eggnog.Relations
{

    /// <summary>
    /// A base value paired with its registry and type, whose <see cref="ToString"/> formats it: the port of egglog's
    /// <c>BaseValuePrinter</c> (core-relations/src/base_values/mod.rs:48-64), which implements <c>Debug</c> the same
    /// way. Formatting is a cold path: one virtual call to the type's table and the codec's
    /// <see cref="IBaseValueCodec{T}.Format"/>.
    /// </summary>
    internal readonly struct BaseValuePrinter
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="base">The registry the type is registered in.</param>
        /// <param name="type">The type of the value.</param>
        /// <param name="value">The value.</param>
        public BaseValuePrinter(BaseValues @base, BaseValueTypeId type, Value value)
        {
            Base = @base;
            Type = type;
            Value = value;
        }

        /// <summary>
        /// Gets the registry the type is registered in.
        /// </summary>
        public BaseValues Base { get; }

        /// <summary>
        /// Gets the type of the value.
        /// </summary>
        public BaseValueTypeId Type { get; }

        /// <summary>
        /// Gets the value.
        /// </summary>
        public Value Value { get; }

        /// <summary>
        /// Formats the base value. Throws if <see cref="Type"/> is not registered in <see cref="Base"/>, as egglog's
        /// printer panics.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString() => Base.Format(Type, Value);

    }

}
