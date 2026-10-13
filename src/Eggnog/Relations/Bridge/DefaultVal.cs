namespace Eggnog.Relations
{

    /// <summary>
    /// How a function computes the output of a key it does not hold when a rule or merge function looks the key up:
    /// egglog-bridge's <c>DefaultVal</c> (egglog-bridge/src/lib.rs:1000-1008). Constructors mint a fresh id, custom
    /// functions fail, and the frontend's scheduler bookkeeping tables insert a constant.
    /// </summary>
    internal readonly record struct DefaultVal
    {

        DefaultVal(DefaultValKind kind, Value constant)
        {
            Kind = kind;
            Constant = constant;
        }

        /// <summary>
        /// Gets the default that mints a fresh e-class id: egglog's <c>DefaultVal::FreshId</c>.
        /// </summary>
        public static DefaultVal FreshId => new(DefaultValKind.FreshId, default);

        /// <summary>
        /// Gets the default that causes an egglog-level panic: egglog's <c>DefaultVal::Fail</c>.
        /// </summary>
        public static DefaultVal Fail => new(DefaultValKind.Fail, default);

        /// <summary>
        /// Creates the default that inserts <paramref name="constant"/>: egglog's <c>DefaultVal::Const</c>.
        /// </summary>
        /// <param name="constant">The output of a missing key.</param>
        /// <returns>The default.</returns>
        public static DefaultVal Const(Value constant) => new(DefaultValKind.Const, constant);

        /// <summary>
        /// Gets which default this is.
        /// </summary>
        public DefaultValKind Kind { get; }

        /// <summary>
        /// Gets the constant of a <see cref="DefaultValKind.Const"/> default; zero otherwise.
        /// </summary>
        public Value Constant { get; }

        /// <inheritdoc />
        public override string ToString() => Kind == DefaultValKind.Const ? "Const(" + Constant + ")" : Kind.ToString();

    }

}
