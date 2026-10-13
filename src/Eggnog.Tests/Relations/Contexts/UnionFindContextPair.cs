using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One context of a union-find differential program, run two ways at once: the engine's <see cref="Context"/> and
    /// the naive <see cref="ReferenceUnionFindContext"/> (the oracle for exact canonical ids, journals and merges).
    /// </summary>
    /// <remarks>
    /// Every operation is applied to both and traced, so a failure prints the program that led to it. Ids are the
    /// engine's; the reference is told them.
    /// </remarks>
    sealed class UnionFindContextPair
    {

        readonly List<UnionFindContextPair> children = new();

        /// <summary>
        /// Initializes a pair over <paramref name="engine"/> and <paramref name="reference"/>, tracing into
        /// <paramref name="trace"/> as <paramref name="name"/>.
        /// </summary>
        UnionFindContextPair(Context engine, ReferenceUnionFindContext reference, UnionFindContextPair? parent, StringBuilder trace, string name)
        {
            Engine = engine;
            Reference = reference;
            Parent = parent;
            Trace = trace;
            Name = name;
        }

        /// <summary>
        /// Creates the root pair of a fresh program.
        /// </summary>
        /// <returns>The root.</returns>
        public static UnionFindContextPair Root()
        {
            var store = new Store(ParallelHeuristics.Serial);
            return new UnionFindContextPair(store.Root, new ReferenceUnionFindContext(), null, new StringBuilder(), "r");
        }

        /// <summary>
        /// Gets the engine's context.
        /// </summary>
        public Context Engine { get; }

        /// <summary>
        /// Gets the naive model.
        /// </summary>
        public ReferenceUnionFindContext Reference { get; }

        /// <summary>
        /// Gets the parent pair, or <c>null</c>.
        /// </summary>
        public UnionFindContextPair? Parent { get; }

        /// <summary>
        /// Gets the live children.
        /// </summary>
        public IReadOnlyList<UnionFindContextPair> Children => children;

        /// <summary>
        /// Gets the program's trace.
        /// </summary>
        public StringBuilder Trace { get; }

        /// <summary>
        /// Gets the pair's name in the trace.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the depth.
        /// </summary>
        public int Depth => Reference.Depth;

        /// <summary>
        /// Gets whether the pair is frozen.
        /// </summary>
        public bool IsFrozen => Reference.IsFrozen;

        /// <summary>
        /// Gets whether every ancestor is frozen, so the pair may be frozen.
        /// </summary>
        public bool CanFreeze => IsFrozen == false && (Parent is null || Parent.IsFrozen);

        /// <summary>
        /// Gets the ids visible in the pair: minted in it or an ancestor.
        /// </summary>
        public uint[] VisibleIds() => Reference.VisibleIds();

        /// <summary>
        /// Mints an id in both.
        /// </summary>
        /// <returns>The engine's id.</returns>
        public uint NewId()
        {
            var id = Engine.NewId();
            Reference.Mint(id);
            Trace.AppendLine($"{Name}.NewId() = {id}");
            return id;
        }

        /// <summary>
        /// Unions two visible ids in both, which must agree on whether the classes were distinct.
        /// </summary>
        public void Union(uint a, uint b)
        {
            Trace.AppendLine($"{Name}.Union({a}, {b})");
            var expected = Reference.Union(a, b);
            Assert.Equal(expected, Engine.Union(a, b));
        }

        /// <summary>
        /// Rebuilds the engine's context.
        /// </summary>
        public void Rebuild()
        {
            Trace.AppendLine($"{Name}.Rebuild()");
            Engine.Rebuild();
        }

        /// <summary>
        /// Creates a child in both.
        /// </summary>
        /// <returns>The child.</returns>
        public UnionFindContextPair CreateChild()
        {
            var name = $"{Name}{children.Count}";
            Trace.AppendLine($"{name} = {Name}.CreateChild()");
            var child = new UnionFindContextPair(Engine.CreateChild(), Reference.CreateChild(), this, Trace, name);
            children.Add(child);
            return child;
        }

        /// <summary>
        /// Freezes the engine's context and the reference's.
        /// </summary>
        public void Freeze()
        {
            Trace.AppendLine($"{Name}.Freeze()");
            Engine.Freeze();
            Reference.IsFrozen = true;
        }

        /// <summary>
        /// Disposes the engine's context; it must have no children.
        /// </summary>
        public void Dispose()
        {
            Trace.AppendLine($"{Name}.Dispose()");
            Engine.Dispose();
            Parent!.children.Remove(this);
        }

        /// <summary>
        /// Rebuilds the pair and compares the engine with the oracle: the engine's invariants hold; canonical ids are
        /// exactly the reference's; and in a child, local ids, the journal (complete, and minus the ids displaced in
        /// the parent's view at creation), own displacements and the merges <see cref="Context.ScanMerges{TVisitor}"/>
        /// reports are the reference's.
        /// </summary>
        /// <param name="allMinted">Every id minted anywhere in the program, by the reference context that minted
        /// it.</param>
        public void Check(IReadOnlyDictionary<uint, ReferenceUnionFindContext> allMinted)
        {
            Trace.AppendLine($"{Name}.Check()");
            Rebuild();
            Assert.True(Engine.IsClean);
            Engine.CheckInvariants();

            // Exact canonical ids.
            var canonicals = Reference.Canonicals();
            foreach (var (id, canonical) in canonicals)
                Assert.True(canonical == Engine.Find(id), $"{Name}: find({id}) is {Engine.Find(id)}, the reference says {canonical}");

            if (Parent is null)
            {
                Assert.Throws<InvalidOperationException>(() => Engine.IsLocalId(0));
                Assert.Equal(canonicals.Count(pair => pair.Key != pair.Value), Engine.DisplacedCount);
                return;
            }

            // IsLocalId is exact over every id the program minted.
            foreach (var (id, owner) in allMinted)
                Assert.True(Engine.IsLocalId(id) == ReferenceEquals(owner, Reference), $"{Name}: IsLocalId({id}) is wrong");

            // The journal holds exactly the non-canonical ids not displaced in the parent's view at creation.
            var journal = Engine.DisplacedIds.ToArray();
            Assert.Equal(journal.Length, journal.Distinct().Count());
            var expectedJournal = canonicals.Where(pair => pair.Key != pair.Value && Reference.DisplacedInParentAtCreation.Contains(pair.Key) == false).Select(pair => pair.Key).OrderBy(x => x).ToArray();
            Assert.Equal(expectedJournal, journal.OrderBy(x => x).ToArray());

            // Own displacements: the journal ids canonical in the parent.
            var parentCanonicals = Parent.Reference.Canonicals();
            foreach (var id in journal)
            {
                var own = parentCanonicals.TryGetValue(id, out var p) == false || p == id;
                Assert.True(own == Engine.IsOwnDisplacement(id), $"{Name}: IsOwnDisplacement({id}) is wrong");
            }

            // ScanMerges: every id canonical in the parent whose class here holds another such id, unless it is local,
            // paired with its canonical here.
            var parentCanonicalIds = canonicals.Keys.Where(id => parentCanonicals.TryGetValue(id, out var p) == false || p == id).ToArray();
            var merged = parentCanonicalIds.GroupBy(id => canonicals[id]).Where(group => group.Count() >= 2).SelectMany(group => group);
            var expectedPairs = merged.Where(id => Reference.Minted.Contains(id) == false).Select(id => (id, canonicals[id])).OrderBy(pair => pair.Item1).ToArray();
            var visitor = new MergeCollector();
            Engine.ScanMerges(ref visitor);
            Assert.Equal(expectedPairs, visitor.Pairs.Select(pair => (pair.Member, pair.Canonical)).OrderBy(pair => pair.Item1).ToArray());
        }

    }

}
