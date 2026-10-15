using System;

namespace Eggnog
{

    /// <summary>
    /// The checks every class value passes before its cell reaches the engine.
    /// </summary>
    internal static class ClassChecks
    {

        /// <summary>
        /// The most origin links a check walks: deeper than any scope tree, whose depth the engine limits to 31 (a
        /// context's layer mask has 32 bits).
        /// </summary>
        const int MaxLinks = 32;

        /// <summary>
        /// Gets the cell of <paramref name="value"/>, checking that it may be used in <paramref name="scope"/>: it names a
        /// class, it belongs to the scope's graph, a batch origin is the open batch or committed, its scope is not
        /// disposed unless it is <paramref name="scope"/> or <paramref name="held"/>, and its scope is
        /// <paramref name="scope"/> or an ancestor of it.
        /// </summary>
        /// <param name="scope">The scope the value is used in. Its own classes pass after its disposal: a call that
        /// starts after the disposal is refused at entry, so only a call that entered it before, and holds it until it
        /// returns, checks them here.</param>
        /// <param name="value">The value.</param>
        /// <param name="parameter">The parameter name for exceptions.</param>
        /// <param name="openBatch">The origin of the host batch the call belongs to, whose values are valid before it
        /// commits; <c>null</c> outside a batch.</param>
        /// <param name="held">The origin of <paramref name="scope"/> or an ancestor of it that the call holds until it
        /// returns, whose own classes also pass after its disposal: a run's scope, for a check in the run's cases, which
        /// the run does not enter, or the scope a split step of the run makes a case under (the run's scope or one of its
        /// cases, which the run keeps until it ends); <c>null</c> for none.</param>
        /// <returns>The value's cell.</returns>
        /// <exception cref="ArgumentException">The value fails a check.</exception>
        public static uint Require(Scope scope, ClassId value, string parameter, ScopeOrigin? openBatch = null, ScopeOrigin? held = null)
        {
            var error = Check(scope.Origin, value, openBatch, held);
            if (error is not null)
                throw new ArgumentException(error, parameter);

            return value.Cell;
        }

        /// <summary>
        /// Gets the message of the first check <paramref name="value"/> fails when used under <paramref name="target"/>,
        /// or <c>null</c> when it passes. A class of a disposed scope fails, except when that scope is
        /// <paramref name="target"/> or <paramref name="held"/>.
        /// </summary>
        /// <param name="target">The origin of the scope the value is used in (see <see cref="Require"/>).</param>
        /// <param name="value">The value.</param>
        /// <param name="openBatch">The open batch's origin, or <c>null</c>.</param>
        /// <param name="held">The origin of <paramref name="target"/> or an ancestor of it that the call holds, or
        /// <c>null</c> (see <see cref="Require"/>).</param>
        /// <returns>The message, or <c>null</c>.</returns>
        public static string? Check(ScopeOrigin target, ClassId value, ScopeOrigin? openBatch, ScopeOrigin? held = null)
        {
            var origin = value.Origin;
            if (origin is null)
                return "default(ClassId) names no class";

            if (ReferenceEquals(origin.Graph, target.Graph) == false)
                return $"class {value} belongs to another graph";

            if (origin.IsBatch)
            {
                if (ReferenceEquals(origin, openBatch) == false && origin.Committed == false)
                    return $"class {value} was made by a Write batch that did not commit";

                origin = origin.Parent!;
            }

            // A call holds the scope it entered until it returns, so a disposal that comes while it runs leaves that
            // scope's own classes usable in it; a call made after the disposal was refused at entry.
            if (origin.Disposed && ReferenceEquals(origin, target) == false && ReferenceEquals(origin, held) == false)
                return $"class {value} came from scope '{origin.Path}', which is disposed";

            var walk = target;
            for (var links = 0; walk is not null && links <= MaxLinks; links++)
            {
                if (ReferenceEquals(walk, origin))
                    return null;

                walk = walk.Parent;
            }

            return $"class {value} came from scope '{origin.Path}', which is not '{target.Path}' or an ancestor of it";
        }

        /// <summary>
        /// Gets whether a rule owned by <paramref name="owner"/> (<c>null</c>: a global rule) may hold a class constant of
        /// <paramref name="origin"/>: global rules hold only classes from the root, local rules classes from
        /// their owner or its ancestors. A batch origin stands for its scope; it must have committed. A disposed scope's
        /// classes are refused, except the owner's own: its definition is a call that has entered the owner.
        /// </summary>
        /// <param name="owner">The rule's owner scope, which the definition has entered; <c>null</c> for a global
        /// rule.</param>
        /// <param name="origin">The constant's origin.</param>
        /// <returns>Whether the constant is allowed.</returns>
        public static bool AllowedInRule(Scope? owner, ScopeOrigin origin)
        {
            if (origin.IsBatch)
            {
                if (origin.Committed == false)
                    return false;

                origin = origin.Parent!;
            }

            if (origin.Disposed && ReferenceEquals(origin, owner?.Origin) == false)
                return false;

            if (owner is null)
                return origin.Parent is null;

            for (ScopeOrigin? walk = owner.Origin; walk is not null; walk = walk.Parent)
                if (ReferenceEquals(walk, origin))
                    return true;

            return false;
        }

    }

}
