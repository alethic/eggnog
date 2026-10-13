using System;

namespace Eggnog
{

    /// <summary>
    /// Resolves a scope tag from a scope: the first scope in <c>from, from.Parent, …</c> whose tag is the tag, by
    /// reference. Tags are absolute labels, never relative to the scope; the root carries <see cref="EGraph.RootTag"/>.
    /// </summary>
    internal static class ScopeResolver
    {

        /// <summary>
        /// Gets the nearest ancestor-or-self of <paramref name="from"/> tagged <paramref name="tag"/>, or <c>null</c>.
        /// O(depth), at most 32 links.
        /// </summary>
        /// <param name="from">The scope.</param>
        /// <param name="tag">The tag.</param>
        /// <returns>The scope, or <c>null</c>.</returns>
        public static Scope? TryResolve(Scope from, ScopeTag tag)
        {
            for (var scope = from; scope is not null; scope = scope.Parent)
                if (ReferenceEquals(scope.Tag, tag))
                    return scope;

            return null;
        }

        /// <summary>
        /// Gets the nearest ancestor-or-self of <paramref name="from"/> tagged <paramref name="tag"/>.
        /// </summary>
        /// <param name="from">The scope.</param>
        /// <param name="tag">The tag.</param>
        /// <param name="reader">What reads the tag, for the message: <c>Rule 'declared tautology'</c>.</param>
        /// <returns>The scope.</returns>
        /// <exception cref="InvalidOperationException">No ancestor-or-self carries the tag: "Rule 'declared tautology'
        /// reads scope 'schema', but 'root/query#4' has no ancestor tagged 'schema'."</exception>
        public static Scope Resolve(Scope from, ScopeTag tag, string reader) =>
            TryResolve(from, tag) ?? throw new InvalidOperationException(
                $"{reader} reads scope '{tag.Name}', but '{from.Path}' has no ancestor tagged '{tag.Name}'.");

    }

}
