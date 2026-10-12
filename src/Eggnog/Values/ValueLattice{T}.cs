using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog
{

    /// <summary>
    /// A merge for a function's values, with the case join a lift uses (D25). Contract: <see cref="Merge"/> is
    /// associative, commutative and idempotent; <c>Merge(a, CaseJoin(a, b)) == a</c> (absorption: the join claims no
    /// more than either case). Both must be pure and thread-safe, and must not call into any graph (D47).
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    public sealed class ValueLattice<T> : ValueLattice
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The lattice's name, for rendering and messages.</param>
        /// <param name="merge">The merge: associative, commutative and idempotent.</param>
        /// <param name="caseJoin">The case join; <c>null</c> when a lift requires every live case to agree.</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="merge"/> is
        /// <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        public ValueLattice(string name, Func<T, T, T> merge, Func<T, T, T>? caseJoin = null)
            : base(Validate(name), typeof(T))
        {
            ArgumentNullException.ThrowIfNull(merge);
            Merge = merge;
            CaseJoin = caseJoin;
        }

        /// <summary>
        /// Gets the merge.
        /// </summary>
        public Func<T, T, T> Merge { get; }

        /// <summary>
        /// Gets the case join; <c>null</c> when a lift requires every live case to agree.
        /// </summary>
        public Func<T, T, T>? CaseJoin { get; }

        /// <summary>
        /// Checks the laws on every value, pair and triple of <paramref name="samples"/>: idempotence, commutativity,
        /// associativity, and, with a case join, absorption. For consumers' tests.
        /// </summary>
        /// <param name="samples">The values to check.</param>
        /// <exception cref="ArgumentNullException"><paramref name="samples"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">A law fails; the message names the law and the values.</exception>
        public void Verify(IEnumerable<T> samples)
        {
            ArgumentNullException.ThrowIfNull(samples);
            var values = samples.ToArray();
            var equal = EqualityComparer<T>.Default;
            foreach (var a in values)
            {
                if (equal.Equals(Merge(a, a), a) == false)
                    throw Law("idempotence", $"Merge({a}, {a}) is {Merge(a, a)}, not {a}");

                foreach (var b in values)
                {
                    var ab = Merge(a, b);
                    var ba = Merge(b, a);
                    if (equal.Equals(ab, ba) == false)
                        throw Law("commutativity", $"Merge({a}, {b}) is {ab} but Merge({b}, {a}) is {ba}");

                    if (CaseJoin is not null)
                    {
                        var joined = CaseJoin(a, b);
                        var absorbed = Merge(a, joined);
                        if (equal.Equals(absorbed, a) == false)
                            throw Law("absorption", $"Merge({a}, CaseJoin({a}, {b})) is {absorbed}, not {a}");
                    }

                    foreach (var c in values)
                    {
                        var left = Merge(Merge(a, b), c);
                        var right = Merge(a, Merge(b, c));
                        if (equal.Equals(left, right) == false)
                            throw Law("associativity", $"Merge(Merge({a}, {b}), {c}) is {left} but Merge({a}, Merge({b}, {c})) is {right}");
                    }
                }
            }
        }

        /// <inheritdoc />
        internal override bool Absorbs(ColumnType type, uint stored, uint incoming)
        {
            var typed = (ColumnType<T>)type;
            var old = typed.Decode(stored, null);
            var merged = Merge(old, typed.Decode(incoming, null));
            return EqualityComparer<T>.Default.Equals(merged, old);
        }

        /// <inheritdoc />
        internal override Relations.ExternalFunction CreateMerge(EGraph graph, string functionName, ColumnType type) =>
            new LatticeMerge<T>(graph, functionName, this, (ColumnType<T>)type);

        /// <summary>
        /// Makes the exception of a failed law.
        /// </summary>
        InvalidOperationException Law(string law, string detail) =>
            new($"Lattice '{Name}' violates {law}: {detail}.");

        /// <summary>
        /// Checks the name before the base constructor stores it.
        /// </summary>
        static string Validate(string name)
        {
            NameRules.ValidateName(name, nameof(name));
            return name;
        }

    }

}
