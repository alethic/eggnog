using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// What a settled lift read (colored-contexts spec §11.2): the versions of the parent, of those of its ancestors that
    /// are not frozen, and of each case, with the identities of the parent and the cases, and the spec it lifted. A lift
    /// whose first round finds the signature it memoized last time has nothing new to read and returns at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Versions are change stamps (<see cref="EGraphContext.Version"/>, X30): they move with every merge that adds or
    /// removes rows (unions included), every hide and clear, and the stop flag; never for no-ops. Frozen ancestors cannot
    /// change, so they are left out.
    /// </para>
    /// <para>
    /// The spec is part of the signature because a lift over other tables reads other rows: it is compared by reference,
    /// then by content (<see cref="LiftSpec.HasSameTables"/>), so a host that builds an equal spec for every call still
    /// hits the memo. The limits are not: only a lift whose answer does not depend on them is memoized (see
    /// <see cref="Lifter"/>).
    /// </para>
    /// <para>
    /// Immutable; O(depth + cases) to take, and <see cref="Matches"/> compares without allocating.
    /// </para>
    /// </remarks>
    internal sealed class LiftSignature : IEquatable<LiftSignature>
    {

        readonly EGraphContext parent;
        readonly EGraphContext[] cases;
        readonly long[] versions;
        readonly LiftSpec spec;

        /// <summary>
        /// Initializes a signature.
        /// </summary>
        LiftSignature(EGraphContext parent, EGraphContext[] cases, long[] versions, LiftSpec spec)
        {
            this.parent = parent;
            this.cases = cases;
            this.versions = versions;
            this.spec = spec;
        }

        /// <summary>
        /// Takes the signature of <paramref name="parent"/>, <paramref name="cases"/> and <paramref name="spec"/> now.
        /// </summary>
        /// <param name="parent">The cases' parent.</param>
        /// <param name="cases">The cases.</param>
        /// <param name="spec">The tables lifted.</param>
        /// <returns>The signature.</returns>
        public static LiftSignature Of(EGraphContext parent, IReadOnlyList<EGraphContext> cases, LiftSpec spec)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(cases);
            ArgumentNullException.ThrowIfNull(spec);
            var versions = new List<long> { parent.Version };
            for (var up = parent.Parent; up is not null && up.IsFrozen == false; up = up.Parent)
                versions.Add(up.Version);

            var copy = new EGraphContext[cases.Count];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = cases[i];
                versions.Add(cases[i].Version);
            }

            return new LiftSignature(parent, copy, versions.ToArray(), spec);
        }

        /// <summary>
        /// Gets whether this signature is the one <see cref="Of"/> would take of <paramref name="parent"/>,
        /// <paramref name="cases"/> and <paramref name="spec"/> now. O(depth + cases + tables); allocates nothing.
        /// </summary>
        /// <param name="parent">The cases' parent.</param>
        /// <param name="cases">The cases.</param>
        /// <param name="spec">The tables to lift.</param>
        /// <returns><c>true</c> if nothing the memoized lift read has changed.</returns>
        public bool Matches(EGraphContext parent, IReadOnlyList<EGraphContext> cases, LiftSpec spec)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(cases);
            ArgumentNullException.ThrowIfNull(spec);
            if (ReferenceEquals(this.parent, parent) == false || this.cases.Length != cases.Count || SameSpec(this.spec, spec) == false)
                return false;

            var v = 0;
            if (versions[v++] != parent.Version)
                return false;

            for (var up = parent.Parent; up is not null && up.IsFrozen == false; up = up.Parent)
                if (v == versions.Length || versions[v++] != up.Version)
                    return false;

            if (versions.Length - v != cases.Count)
                return false;

            for (var i = 0; i < cases.Count; i++)
                if (ReferenceEquals(this.cases[i], cases[i]) == false || versions[v++] != cases[i].Version)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public bool Equals(LiftSignature? other)
        {
            if (other is null)
                return false;

            if (ReferenceEquals(parent, other.parent) == false || cases.Length != other.cases.Length || versions.AsSpan().SequenceEqual(other.versions) == false || SameSpec(spec, other.spec) == false)
                return false;

            for (var i = 0; i < cases.Length; i++)
                if (ReferenceEquals(cases[i], other.cases[i]) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is LiftSignature other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(parent);
            foreach (var version in versions)
                hash.Add(version);

            hash.Add(spec.Tables.Count);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public override string ToString() => $"LiftSignature [{string.Join(", ", versions)}] over {spec.Tables.Count} tables";

        /// <summary>
        /// Gets whether two specs lift the same tables the same way.
        /// </summary>
        static bool SameSpec(LiftSpec a, LiftSpec b) => ReferenceEquals(a, b) || a.HasSameTables(b);

    }

}
