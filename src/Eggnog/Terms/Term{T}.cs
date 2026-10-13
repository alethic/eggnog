using System;

namespace Eggnog
{

    /// <summary>
    /// An expression whose values are <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The CLR type of the values.</typeparam>
    public abstract class Term<T> : Term
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        private protected Term()
        {

        }

        /// <inheritdoc />
        public override Type ClrType => typeof(T);

        /// <summary>
        /// Equality: a join or a check in a body, a write or a union in a head, a ground fact in a host write or check.
        /// </summary>
        /// <param name="other">The other side.</param>
        /// <returns>The fact.</returns>
        public Fact Is(Term<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            return new EqualityFact(this, other);
        }

        /// <summary>
        /// Disequality: a filter, in a body only (a definition error in a head; <see cref="ArgumentException"/> in a
        /// host write or check).
        /// </summary>
        /// <param name="other">The other side.</param>
        /// <returns>The fact.</returns>
        public Fact IsNot(Term<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            return new NotEqualFact(this, other);
        }

        /// <summary>
        /// A constant; for a sort, a class constant, checked where it is used: a class is usable only in the scope that
        /// returned it and that scope's descendants, until that scope is disposed.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is <c>null</c>.</exception>
        public static implicit operator Term<T>(T value)
        {
            if (value is null)
                throw new ArgumentNullException(nameof(value));

            return new ConstantTerm<T>(value);
        }

    }

}
