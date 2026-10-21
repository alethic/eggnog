using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace Eggnog
{

    public sealed partial class EGraph
    {

        /// <summary>
        /// The root tag's name, reserved.
        /// </summary>
        const string RootTagName = "root";

        /// <summary>
        /// The name of <see cref="Contradiction"/>, under the reserved <c>$</c> prefix.
        /// </summary>
        const string ContradictionName = "$contradiction";

        readonly Dictionary<string, ScopeTag> scopeTagsByName = new(StringComparer.Ordinal);
        ScopeTag[] scopeTags;
        ReadOnlyCollection<ScopeTag> scopeTagsView;

        /// <summary>
        /// Gets the root's tag, named <c>root</c>. Every graph has its own.
        /// </summary>
        public ScopeTag RootTag { get; }

        /// <summary>
        /// Gets every tag of this graph, the root's first, in definition order (a snapshot).
        /// </summary>
        public IReadOnlyList<ScopeTag> ScopeTags => Volatile.Read(ref scopeTagsView);

        /// <summary>
        /// Defines a scope tag of this graph: a label a scope carries, which scoped atoms resolve to the nearest
        /// ancestor-or-self carrying it. Any thread, any time.
        /// </summary>
        /// <remarks>
        /// A tag is a definition of the graph, kept for its life, and so is the mirror table that the first scoped read
        /// of a table through the tag creates. Define tags with the theory, one per role a scope plays (a schema, a
        /// query, a case), never one per request: each tag grows the graph's tags and mirrors for good.
        /// </remarks>
        /// <param name="name">The tag's name: non-empty, unique among the graph's tags; <c>root</c> is the root's.</param>
        /// <returns>The tag.</returns>
        /// <exception cref="ArgumentException">The name is empty or taken.</exception>
        /// <exception cref="InvalidOperationException">Called from a primitive, lattice, filter or cost
        /// function.</exception>
        public ScopeTag DefineScopeTag(string name)
        {
            EngineCallMarker.ThrowIfActive();
            NameRules.ValidateName(name, nameof(name));
            lock (DefinitionLock)
            {
                if (scopeTagsByName.ContainsKey(name))
                {
                    throw new ArgumentException(name == RootTagName
                        ? "The tag name 'root' is the root's."
                        : $"A scope tag named '{name}' is already defined.", nameof(name));
                }

                var tag = new ScopeTag(this, name, scopeTags.Length);
                scopeTagsByName.Add(name, tag);
                var next = new ScopeTag[scopeTags.Length + 1];
                scopeTags.CopyTo(next, 0);
                next[^1] = tag;
                scopeTags = next;
                Volatile.Write(ref scopeTagsView, Array.AsReadOnly(next));
                return tag;
            }
        }

        /// <summary>
        /// Defines a named ruleset whose rules are erasing under <see cref="RulesetOptions.Licence"/> unless they call
        /// <see cref="RuleBuilder.ReadsOwnFacts"/>. Any thread, any time.
        /// </summary>
        /// <param name="name">The name, unique among the graph's rulesets.</param>
        /// <param name="options">The ruleset's options.</param>
        /// <returns>The ruleset.</returns>
        /// <exception cref="ArgumentException">The name is empty or taken, or the licence is another graph's
        /// tag.</exception>
        public Ruleset DefineRuleset(string name, RulesetOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.Licence is { } licence)
                CheckTag(licence, nameof(options));

            return DefineRulesetCore(name, options.Licence);
        }

        /// <summary>
        /// Checks that a tag belongs to this graph.
        /// </summary>
        /// <param name="tag">The tag.</param>
        /// <param name="parameter">The parameter name for the exception.</param>
        /// <exception cref="ArgumentNullException"><paramref name="tag"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The tag belongs to another graph.</exception>
        internal void CheckTag(ScopeTag tag, string parameter)
        {
            ArgumentNullException.ThrowIfNull(tag, parameter);
            if (ReferenceEquals(tag.Graph, this) == false)
                throw new ArgumentException($"Scope tag '{tag.Name}' belongs to another graph.", parameter);
        }

        /// <summary>
        /// Checks that every <see cref="Fact.In"/> tag in a fact given to a rule or query builder belongs to this
        /// graph: <see cref="Fact.In"/> itself can compare its tag only with the tables inside it, so a foreign tag
        /// over facts without an application is caught here, at the builder call. O(nesting) for a scoped fact; nothing
        /// for any other.
        /// </summary>
        /// <param name="fact">The fact.</param>
        /// <param name="parameter">The parameter name for the exception.</param>
        /// <exception cref="ArgumentException">A tag belongs to another graph.</exception>
        internal void CheckFactTags(Fact fact, string parameter)
        {
            if (fact is not ScopedFact scoped)
                return;

            CheckTag(scoped.Scope, parameter);
            foreach (var inner in scoped.Facts)
                CheckFactTags(inner, parameter);
        }

        /// <summary>
        /// Defines the mirror table of <paramref name="source"/> under <paramref name="tag"/>: an engine function
        /// named <c>{source}@{tag}</c> with the source's layout (a lattice function gets its own lattice merge, and a
        /// subsumable relation's mirror is subsumable, so that it holds the source's subsumptions too), and an
        /// internal handle of the source's kind over it, which is listed neither in <see cref="Tables"/> nor in extraction.
        /// A table definition: while the root is writable it enters the root's guard. Called by the mirror registry
        /// under the definition lock.
        /// </summary>
        /// <param name="source">The value-only table read scoped.</param>
        /// <param name="tag">The tag it is read under.</param>
        /// <param name="index">The handle's index: negative and unique among mirrors, so it never equals a table's.</param>
        /// <returns>The mirror's handle.</returns>
        /// <exception cref="InvalidOperationException">Another thread is using the writable root.</exception>
        internal TableHandle DefineMirrorTable(TableHandle source, ScopeTag tag, int index)
        {
            TableHandle handle = source.Kind == TableKind.Relation ? new UntypedRelation() : new UntypedFunction();
            var name = source.Name + "@" + tag.Name;
            var lattice = (source as FunctionHandle)?.Lattice;
            var function = AddEngineTable(name, source.Kind, source.Columns, lattice, source.Output, source.Subsumable);
            handle.Graph = this;
            handle.Name = name;
            handle.Index = index;
            handle.Kind = source.Kind;
            handle.Role = source.Role;
            handle.KeyArray = source.KeyArray;
            handle.Output = source.Output;
            handle.Lift = false;
            handle.Subsumable = source.Subsumable;
            handle.Function = function;
            handle.Columns = source.Columns;
            handle.Source = source;
            handle.MirrorTag = tag;
            if (handle is FunctionHandle mirrorFunction)
                mirrorFunction.Lattice = (source as FunctionHandle)?.Lattice;

            PublishFunction(handle);
            return handle;
        }

        /// <summary>
        /// Defines a scheduler's worklist (<see cref="ScheduledRule"/>): a context-private relation over the columns of the
        /// matches it holds, which descendants never read, so a child's is its own, and whose rows are not subsumable.
        /// egglog's <c>decided</c> table (<c>src/scheduler.rs</c>). A table definition: while the root is writable it enters
        /// the root's guard. Called under the definition lock.
        /// </summary>
        /// <param name="name">The table's engine name, unique.</param>
        /// <param name="keys">The column types of the matches.</param>
        /// <param name="index">The handle's index: negative and unique among internal handles, so it never equals a table's
        /// or a mirror's.</param>
        /// <returns>The worklist's handle.</returns>
        /// <exception cref="InvalidOperationException">Another thread is using the writable root.</exception>
        internal TableHandle DefineWorklistTable(string name, ColumnType[] keys, int index)
        {
            var columns = new ColumnType[keys.Length + 1];
            keys.CopyTo(columns, 0);
            columns[^1] = Unit;
            var function = AddEngineTable(name, TableKind.Relation, columns, null, null, false, contextPrivate: true);
            var handle = new UntypedRelation
            {
                Graph = this,
                Name = name,
                Index = index,
                Kind = TableKind.Relation,
                Role = TableRole.Fact,
                KeyArray = keys,
                Lift = false,
                Subsumable = false,
                Function = function,
                Columns = columns,
                IsWorklist = true,
            };
            PublishFunction(handle);
            return handle;
        }

    }

}
