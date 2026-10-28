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
        /// class, it belongs to the scope's graph, a batch origin is the open batch or committed, and its scope is
        /// <paramref name="scope"/> or an ancestor of it. A disposed scope's classes pass in the scope and its live
        /// descendants: a call that starts on the disposed scope is refused at entry, a live descendant or a call in
        /// progress keeps the scope from being released, and ids are never reused, so its cells stay valid wherever
        /// they pass.
        /// </summary>
        /// <param name="scope">The scope the value is used in.</param>
        /// <param name="value">The value.</param>
        /// <param name="parameter">The parameter name for exceptions.</param>
        /// <param name="openBatch">The origin of the host batch the call belongs to, whose values are valid before it
        /// commits; <c>null</c> outside a batch.</param>
        /// <returns>The value's cell.</returns>
        /// <exception cref="ArgumentException">The value fails a check.</exception>
        public static uint Require(Scope scope, ClassId value, string parameter, ScopeOrigin? openBatch = null)
        {
            var error = Check(scope.Origin, value, openBatch);
            if (error is not null)
                throw new ArgumentException(error, parameter);

            return value.Cell;
        }

        /// <summary>
        /// Gets the message of the first check <paramref name="value"/> fails when used under <paramref name="target"/>,
        /// or <c>null</c> when it passes (see <see cref="Require"/>). Whether the value's scope is disposed only chooses
        /// the message of a value whose scope is not <paramref name="target"/> or an ancestor of it.
        /// </summary>
        /// <param name="target">The origin of the scope the value is used in.</param>
        /// <param name="value">The value.</param>
        /// <param name="openBatch">The open batch's origin, or <c>null</c>.</param>
        /// <returns>The message, or <c>null</c>.</returns>
        public static string? Check(ScopeOrigin target, ClassId value, ScopeOrigin? openBatch)
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

            var walk = target;
            for (var links = 0; walk is not null && links <= MaxLinks; links++)
            {
                if (ReferenceEquals(walk, origin))
                    return null;

                walk = walk.Parent;
            }

            if (origin.Disposed)
                return $"class {value} came from scope '{origin.Path}', which is disposed";

            return $"class {value} came from scope '{origin.Path}', which is not '{target.Path}' or an ancestor of it";
        }

        /// <summary>
        /// Gets the message of the first check a class constant held in <paramref name="graph"/> fails before any scope is
        /// asked of it, or <c>null</c> when it passes: it names a class, and the class belongs to the graph.
        /// </summary>
        /// <param name="graph">The graph the constant is held in.</param>
        /// <param name="value">The constant.</param>
        /// <returns>The message, or <c>null</c>.</returns>
        public static string? CheckConstant(EGraph graph, ClassId value)
        {
            if (value.Origin is not { } origin)
                return "a class constant is default(ClassId), which names no class";

            if (ReferenceEquals(origin.Graph, graph) == false)
                return $"class {value} belongs to another graph";

            return null;
        }

        /// <summary>
        /// Gets the message of the first check a class constant held by a rule owned by <paramref name="owner"/> fails, or
        /// <c>null</c> when it passes: <see cref="CheckConstant"/>, then that a batch origin committed and that the rule
        /// may hold it (<see cref="AllowedInRule"/>). A rule of the root, such as a global rule, a split rule or a merge,
        /// has no owner.
        /// </summary>
        /// <param name="owner">The rule's owner scope; <c>null</c> for a rule of the root.</param>
        /// <param name="graph">The graph the constant is held in.</param>
        /// <param name="value">The constant.</param>
        /// <param name="rootOnly">What the message of a constant from a scope that is not the root says after the scope
        /// when <paramref name="owner"/> is <c>null</c>: that the rule may hold only classes from the root.</param>
        /// <returns>The message, or <c>null</c>.</returns>
        public static string? CheckRuleConstant(Scope? owner, EGraph graph, ClassId value, string rootOnly)
        {
            if (CheckConstant(graph, value) is { } error)
                return error;

            var origin = value.Origin!;
            if (origin.IsBatch && origin.Committed == false)
                return $"class {value} was made by a Write batch that did not commit";

            if (AllowedInRule(owner, origin))
                return null;

            var scope = origin.IsBatch ? origin.Parent! : origin;
            if (scope.Disposed)
                return $"class {value} came from scope '{scope.Path}', which is disposed";

            if (owner is null)
                return $"class {value} came from scope '{scope.Path}'; {rootOnly}";

            return $"class {value} came from scope '{scope.Path}', which is not '{owner.Path}' or an ancestor of it";
        }

        /// <summary>
        /// Gets whether a rule owned by <paramref name="owner"/> (<c>null</c>: a global rule) may hold a class constant of
        /// <paramref name="origin"/>: global rules hold only classes from the root, local rules classes from
        /// their owner or its ancestors. A batch origin stands for its scope; it must have committed. A disposed
        /// scope's classes are allowed where the scope is the owner or an ancestor of it, as in <see cref="Require"/>.
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

            if (owner is null)
                return origin.Parent is null;

            for (ScopeOrigin? walk = owner.Origin; walk is not null; walk = walk.Parent)
                if (ReferenceEquals(walk, origin))
                    return true;

            return false;
        }

    }

}
