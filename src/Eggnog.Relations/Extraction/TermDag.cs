using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// A hash-consing arena of terms: egglog's <c>TermDag</c> (src/termdag.rs:19-24, 115-170, 471-524). Each distinct
    /// term (a literal, a variable, or a head applied to child terms already in the DAG) is stored once and named by a
    /// <see cref="TermId"/>, so equal subterms are shared and a term is a DAG over its subterms. Extraction writes its
    /// results here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ids are dense and assigned in insertion order, like egglog's <c>IndexSet</c>: adding a term that is already
    /// present returns its existing id. Children are stored in one flat buffer and the index is a
    /// <see cref="RawHashTable{TEntry}"/> keyed by a hash of the term's contents, so adding a term allocates nothing once
    /// the buffers have grown. Expected O(arity) per add.
    /// </para>
    /// <para>
    /// Not thread-safe; any number of threads may read while nobody adds.
    /// </para>
    /// </remarks>
    public sealed class TermDag
    {

        readonly RawHashTable<TermDagEntry> index;
        TermKind[] kinds;
        string?[] names;
        TermLiteral[] literals;
        int[] childStarts;
        int[] childCounts;
        TermId[] children;
        int childLength;
        int count;

        /// <summary>
        /// Initializes a new, empty instance.
        /// </summary>
        public TermDag()
        {
            index = new RawHashTable<TermDagEntry>();
            kinds = new TermKind[16];
            names = new string?[16];
            literals = new TermLiteral[16];
            childStarts = new int[16];
            childCounts = new int[16];
            children = new TermId[32];
        }

        /// <summary>
        /// Gets the number of distinct terms. egglog's <c>size</c>.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets the term <paramref name="id"/>. egglog's <c>get</c>.
        /// </summary>
        /// <param name="id">The term's id.</param>
        /// <returns>A view of the term.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The id is not in this DAG.</exception>
        public Term this[TermId id]
        {
            get
            {
                CheckId(id, nameof(id));
                return new Term(this, id);
            }
        }

        /// <summary>
        /// Adds the application of <paramref name="head"/> to <paramref name="children"/>, or finds it. egglog's
        /// <c>app</c>.
        /// </summary>
        /// <param name="head">The head symbol.</param>
        /// <param name="children">The child terms, already in this DAG.</param>
        /// <returns>The term's id.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A child is not in this DAG.</exception>
        public TermId App(string head, params ReadOnlySpan<TermId> children)
        {
            ArgumentNullException.ThrowIfNull(head);
            foreach (var child in children)
                CheckId(child, nameof(children));

            // Stage the children past the committed end of the buffer: the probe compares against them there, and adding
            // the term commits them without a copy.
            EnsureChildCapacity(childLength + children.Length);
            children.CopyTo(this.children.AsSpan(childLength));
            return Add(TermKind.App, head, default, children.Length);
        }

        /// <summary>
        /// Adds the literal <paramref name="literal"/>, or finds it. egglog's <c>lit</c>.
        /// </summary>
        /// <param name="literal">The literal.</param>
        /// <returns>The term's id.</returns>
        /// <exception cref="ArgumentException">The literal has no text.</exception>
        public TermId Lit(TermLiteral literal)
        {
            if (literal.Text is null)
                throw new ArgumentException("A literal needs its text.", nameof(literal));

            return Add(TermKind.Lit, null, literal, 0);
        }

        /// <summary>
        /// Adds the variable <paramref name="name"/>, or finds it. egglog's <c>var</c>.
        /// </summary>
        /// <param name="name">The variable's name.</param>
        /// <returns>The term's id.</returns>
        public TermId Var(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            return Add(TermKind.Var, name, default, 0);
        }

        /// <summary>
        /// Finds the application of <paramref name="head"/> to <paramref name="children"/> without adding it. egglog's
        /// <c>lookup</c>, which panics where this returns <c>false</c>.
        /// </summary>
        /// <param name="head">The head symbol.</param>
        /// <param name="children">The child terms.</param>
        /// <param name="id">The term's id, when found.</param>
        /// <returns>Whether the DAG holds the term.</returns>
        public bool TryLookupApp(string head, ReadOnlySpan<TermId> children, out TermId id)
        {
            ArgumentNullException.ThrowIfNull(head);
            EnsureChildCapacity(childLength + children.Length);
            children.CopyTo(this.children.AsSpan(childLength));
            return TryFind(TermKind.App, head, default, children.Length, out id);
        }

        /// <summary>
        /// Finds the literal <paramref name="literal"/> without adding it.
        /// </summary>
        /// <param name="literal">The literal.</param>
        /// <param name="id">The term's id, when found.</param>
        /// <returns>Whether the DAG holds the term.</returns>
        public bool TryLookupLit(TermLiteral literal, out TermId id)
        {
            if (literal.Text is null)
            {
                id = default;
                return false;
            }

            return TryFind(TermKind.Lit, null, literal, 0, out id);
        }

        /// <summary>
        /// Finds the variable <paramref name="name"/> without adding it.
        /// </summary>
        /// <param name="name">The variable's name.</param>
        /// <param name="id">The term's id, when found.</param>
        /// <returns>Whether the DAG holds the term.</returns>
        public bool TryLookupVar(string name, out TermId id)
        {
            ArgumentNullException.ThrowIfNull(name);
            return TryFind(TermKind.Var, name, default, 0, out id);
        }

        /// <summary>
        /// Renders the term <paramref name="term"/> as an s-expression, <c>(head child ...)</c>, with literals and
        /// variables as their text. egglog's <c>to_string</c> (src/termdag.rs:471-523): iterative, so deep terms do not
        /// overflow the stack, and each shared subterm is rendered once and copied where it repeats.
        /// </summary>
        /// <param name="term">The term.</param>
        /// <returns>The text; as long as the term's tree, which can be exponential in the size of the DAG.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The id is not in this DAG.</exception>
        public string ToString(TermId term)
        {
            CheckId(term, nameof(term));

            var result = new StringBuilder();
            var ranges = new Dictionary<int, (int Start, int End)>();

            // Frames: (term, whether a space precedes it, where its opening parenthesis is once it is open).
            var stack = new Stack<(int Id, bool SpaceBefore, int Start)>();
            stack.Push((term.Rep, false, -1));
            while (stack.Count > 0)
            {
                var (id, spaceBefore, start) = stack.Pop();
                if (spaceBefore)
                    result.Append(' ');

                if (ranges.TryGetValue(id, out var range))
                {
                    result.Append(result.ToString(range.Start, range.End - range.Start));
                    continue;
                }

                switch (kinds[id])
                {
                    case TermKind.App:
                        if (start >= 0)
                        {
                            result.Append(')');
                        }
                        else
                        {
                            stack.Push((id, false, result.Length));
                            result.Append('(').Append(names[id]);
                            for (var i = childCounts[id] - 1; i >= 0; i--)
                                stack.Push((children[childStarts[id] + i].Rep, true, -1));
                        }

                        break;
                    case TermKind.Lit:
                        start = result.Length;
                        result.Append(literals[id].Text);
                        break;
                    default:
                        start = result.Length;
                        result.Append(names[id]);
                        break;
                }

                if (start >= 0)
                    ranges[id] = (start, result.Length);
            }

            return result.ToString();
        }

        /// <summary>
        /// Gets the kind of a term known to be in the DAG.
        /// </summary>
        internal TermKind KindOf(TermId id) => kinds[id.Rep];

        /// <summary>
        /// Gets the head or variable name of a term known to be in the DAG; <c>null</c> for a literal.
        /// </summary>
        internal string? NameOf(TermId id) => names[id.Rep];

        /// <summary>
        /// Gets the literal of a term known to be in the DAG; <c>default</c> unless it is a literal.
        /// </summary>
        internal TermLiteral LiteralOf(TermId id) => literals[id.Rep];

        /// <summary>
        /// Gets the children of a term known to be in the DAG. Valid until the next add.
        /// </summary>
        internal ReadOnlySpan<TermId> ChildrenOf(TermId id) => new(children, childStarts[id.Rep], childCounts[id.Rep]);

        /// <summary>
        /// Gets whether the stored term <paramref name="term"/> has the given contents; the children are those at
        /// <paramref name="childStart"/> in the child buffer. Called by <see cref="TermDagMatch"/>.
        /// </summary>
        internal bool Holds(int term, TermKind kind, string? name, TermLiteral literal, int childStart, int childCount)
        {
            if (kinds[term] != kind || childCounts[term] != childCount)
                return false;

            switch (kind)
            {
                case TermKind.Lit:
                    return literals[term] == literal;
                case TermKind.Var:
                    return string.Equals(names[term], name, StringComparison.Ordinal);
                default:
                    return string.Equals(names[term], name, StringComparison.Ordinal)
                        && children.AsSpan(childStarts[term], childCount).SequenceEqual(children.AsSpan(childStart, childCount));
            }
        }

        /// <summary>
        /// Finds or adds a term whose children (for an application) are staged at <see cref="childLength"/>.
        /// </summary>
        TermId Add(TermKind kind, string? name, TermLiteral literal, int childCount)
        {
            var hash = Hash(kind, name, literal, childLength, childCount);
            var match = new TermDagMatch(this, hash, kind, name, literal, childLength, childCount);
            var slot = index.FindOrFindInsertSlot(hash, ref match, default(TermDagEntryHasher));
            if (slot >= 0)
                return new TermId(index.EntryAt(slot).Term);

            if (count == kinds.Length)
                GrowTerms();

            var id = count++;
            kinds[id] = kind;
            names[id] = name;
            literals[id] = literal;
            childStarts[id] = childLength;
            childCounts[id] = childCount;
            childLength += childCount;
            index.InsertInSlot(hash, ~slot, new TermDagEntry(hash, id));
            return new TermId(id);
        }

        /// <summary>
        /// Finds a term whose children (for an application) are staged at <see cref="childLength"/>, without adding it.
        /// </summary>
        bool TryFind(TermKind kind, string? name, TermLiteral literal, int childCount, out TermId id)
        {
            var hash = Hash(kind, name, literal, childLength, childCount);
            var match = new TermDagMatch(this, hash, kind, name, literal, childLength, childCount);
            var slot = index.Find(hash, ref match);
            id = slot >= 0 ? new TermId(index.EntryAt(slot).Term) : default;
            return slot >= 0;
        }

        /// <summary>
        /// Hashes a term's contents: its kind, its name or literal, and its children.
        /// </summary>
        ulong Hash(TermKind kind, string? name, TermLiteral literal, int childStart, int childCount)
        {
            var hasher = new FxHasher();
            hasher.Write((ulong)kind);
            if (kind == TermKind.Lit)
            {
                hasher.Write((ulong)(uint)literal.Type.Rep);
                hasher.Write(literal.Value.Rep);
                hasher.Write((ulong)(uint)literal.Text.GetHashCode());
            }
            else
            {
                Debug.Assert(name is not null, "applications and variables are named");
                hasher.Write((ulong)(uint)name.GetHashCode());
                hasher.Write((ulong)(uint)childCount);
                for (var i = 0; i < childCount; i++)
                    hasher.Write((ulong)(uint)children[childStart + i].Rep);
            }

            return hasher.Finish();
        }

        void CheckId(TermId id, string paramName)
        {
            if ((uint)id.Rep >= (uint)count)
                throw new ArgumentOutOfRangeException(paramName, id, $"The term is not in this DAG of {count} terms.");
        }

        void EnsureChildCapacity(int needed)
        {
            if (needed > children.Length)
                Array.Resize(ref children, Math.Max(needed, children.Length * 2));
        }

        void GrowTerms()
        {
            var size = kinds.Length * 2;
            Array.Resize(ref kinds, size);
            Array.Resize(ref names, size);
            Array.Resize(ref literals, size);
            Array.Resize(ref childStarts, size);
            Array.Resize(ref childCounts, size);
        }

    }

}
