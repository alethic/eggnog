using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Xunit;

using Spike = Eggnog.Storage;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One context of a union-find differential program (colored-contexts spec §17 M9.2), run three ways at once: the
    /// engine's <see cref="Context"/>, the naive <see cref="ReferenceUnionFindContext"/> (the oracle for exact canonical
    /// ids, journals and merges), and the spike's context (a second oracle, compared modulo renaming of ids).
    /// </summary>
    /// <remarks>
    /// Every operation is applied to all three and traced, so a failure prints the program that led to it. Ids are the
    /// engine's; the spike mints its own, and <see cref="Ids"/> maps each engine id to the spike id minted with it.
    /// </remarks>
    sealed class UnionFindContextPair
    {

        readonly List<UnionFindContextPair> children = new();

        UnionFindContextPair(Context engine, Spike.Context spike, ReferenceUnionFindContext reference, UnionFindContextPair? parent, Dictionary<uint, uint> ids, StringBuilder trace, string name)
        {
            Engine = engine;
            SpikeContext = spike;
            Reference = reference;
            Parent = parent;
            Ids = ids;
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
            var spike = new Spike.Store();
            return new UnionFindContextPair(store.Root, spike.Root, new ReferenceUnionFindContext(), null, new Dictionary<uint, uint>(), new StringBuilder(), "r");
        }

        /// <summary>
        /// Gets the engine's context.
        /// </summary>
        public Context Engine { get; }

        /// <summary>
        /// Gets the spike's context.
        /// </summary>
        public Spike.Context SpikeContext { get; }

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
        /// Gets the map from every engine id minted in the program to the spike id minted with it.
        /// </summary>
        public Dictionary<uint, uint> Ids { get; }

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
        /// Mints an id in all three.
        /// </summary>
        /// <returns>The engine's id.</returns>
        public uint NewId()
        {
            var id = Engine.NewId();
            var spikeId = SpikeContext.NewId().Value;
            Ids.Add(id, spikeId);
            Reference.Mint(id);
            Trace.AppendLine($"{Name}.NewId() = {id}");
            return id;
        }

        /// <summary>
        /// Unions two visible ids in all three, which must agree on whether the classes were distinct.
        /// </summary>
        public void Union(uint a, uint b)
        {
            Trace.AppendLine($"{Name}.Union({a}, {b})");
            var expected = Reference.Union(a, b);
            Assert.Equal(expected, Engine.Union(a, b));
            Assert.Equal(expected, SpikeContext.Union(new Spike.Id(Ids[a]), new Spike.Id(Ids[b])));
        }

        /// <summary>
        /// Rebuilds the engine's and the spike's context.
        /// </summary>
        public void Rebuild()
        {
            Trace.AppendLine($"{Name}.Rebuild()");
            Engine.Rebuild();
            SpikeContext.Rebuild();
        }

        /// <summary>
        /// Creates a child in all three.
        /// </summary>
        /// <returns>The child.</returns>
        public UnionFindContextPair CreateChild()
        {
            var name = $"{Name}{children.Count}";
            Trace.AppendLine($"{name} = {Name}.CreateChild()");
            var child = new UnionFindContextPair(Engine.CreateChild(), SpikeContext.CreateChild(), Reference.CreateChild(), this, Ids, Trace, name);
            children.Add(child);
            return child;
        }

        /// <summary>
        /// Freezes the engine's and the spike's context.
        /// </summary>
        public void Freeze()
        {
            Trace.AppendLine($"{Name}.Freeze()");
            Engine.Freeze();
            SpikeContext.Freeze();
            Reference.IsFrozen = true;
        }

        /// <summary>
        /// Disposes the engine's and the spike's context; it must have no children.
        /// </summary>
        public void Dispose()
        {
            Trace.AppendLine($"{Name}.Dispose()");
            Engine.Dispose();
            SpikeContext.Dispose();
            Parent!.children.Remove(this);
        }

        /// <summary>
        /// Rebuilds the pair and compares the engine with both oracles: the engine's invariants hold; canonical ids are
        /// exactly the reference's; the partition equals the spike's modulo renaming; and in a child, local ids, the
        /// journal (complete, and minus the ids displaced in the parent's view at creation), own displacements and the
        /// merges <see cref="Context.ScanMerges{TVisitor}"/> reports are the reference's.
        /// </summary>
        /// <param name="allMinted">Every id minted anywhere in the program, by the reference context that minted it.</param>
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

            // The spike's partition, modulo renaming: a bijection between the classes.
            var spikeToEngine = new Dictionary<uint, uint>();
            var engineToSpike = new Dictionary<uint, uint>();
            foreach (var (id, canonical) in canonicals)
            {
                var spikeCanonical = SpikeContext.Find(new Spike.Id(Ids[id])).Value;
                if (spikeToEngine.TryGetValue(spikeCanonical, out var seen))
                    Assert.True(seen == canonical, $"{Name}: the spike puts {id} with the engine's class {seen}, the engine in {canonical}");
                else
                    spikeToEngine.Add(spikeCanonical, canonical);

                if (engineToSpike.TryGetValue(canonical, out var seenSpike))
                    Assert.True(seenSpike == spikeCanonical, $"{Name}: the engine's class {canonical} is split in the spike");
                else
                    engineToSpike.Add(canonical, spikeCanonical);
            }

            if (Parent is null)
            {
                Assert.Throws<InvalidOperationException>(() => Engine.IsLocalId(0));
                Assert.Equal(canonicals.Count(pair => pair.Key != pair.Value), Engine.DisplacedCount);
                return;
            }

            // IsLocalId is exact over every id the program minted.
            foreach (var (id, owner) in allMinted)
                Assert.True(Engine.IsLocalId(id) == ReferenceEquals(owner, Reference), $"{Name}: IsLocalId({id}) is wrong");

            // The journal holds exactly the non-canonical ids not displaced in the parent's view at creation (I8).
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
