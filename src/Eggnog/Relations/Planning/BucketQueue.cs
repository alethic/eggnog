using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Greedy set cover over the atoms of a bag, for the <see cref="PlanStrategy.MinCover"/> strategy: egglog's
    /// <c>BucketQueue</c> (core-relations/src/free_join/plan.rs:1302-1366).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each atom keeps the set of its variables not yet covered, and atoms are bucketed by that count. Each
    /// <see cref="TryPop"/> takes an atom from the <em>largest</em> bucket (egglog's comment says smallest, but
    /// <c>next_back</c> is the largest key), adds its variables to the cover, and moves every atom sharing them to a
    /// smaller bucket. Buckets are indexed by count (at most the largest atom's variable count) instead of a
    /// <c>BTreeMap</c>; with empty buckets dropped, "highest non-empty bucket" and "last key" agree.
    /// </para>
    /// <para>
    /// Fix (map §8.1 row 7, core-relations/src/free_join/plan.rs:1337-1338): an atom without variables sits in bucket
    /// 0; popping it never changes any count, so egglog never removes the emptied bucket and the next pop unwraps an
    /// empty set and panics. Here a bucket is dropped as soon as a pop empties it.
    /// </para>
    /// <para>
    /// Fix (map §8.1 row 7, plan.rs:1344): in a bag, atoms mention variables outside the bag, whose
    /// <c>var_info</c> lookups panic in egglog. Only the bag's variables enter the per-atom sets.
    /// </para>
    /// </remarks>
    internal sealed class BucketQueue
    {

        readonly PlanningContext ctx;
        readonly FixedBitSet cover;
        readonly DenseIdMap<AtomId, FixedBitSet> atomInfo;
        readonly AtomIndexSet?[] buckets;

        /// <summary>
        /// Initializes a new instance over the atoms of <paramref name="ctx"/>.
        /// </summary>
        /// <param name="ctx">The bag.</param>
        public BucketQueue(PlanningContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            this.ctx = ctx;
            var capacity = ctx.VariableCapacity;
            cover = new FixedBitSet(capacity);
            atomInfo = new DenseIdMap<AtomId, FixedBitSet>(ctx.Atoms.IdCount);
            var maxCount = 0;
            foreach (var (atom, info) in ctx.Atoms)
            {
                var set = new FixedBitSet(capacity);
                foreach (var (_, variable) in info.VarColumns)
                    if (ctx.Variables.ContainsKey(variable))
                        set.Insert(variable.Index);

                atomInfo.Insert(atom, set);
                maxCount = Math.Max(maxCount, set.Count);
            }

            buckets = new AtomIndexSet?[maxCount + 1];
            foreach (var (atom, set) in atomInfo)
                (buckets[set.Count] ??= new AtomIndexSet()).Insert(atom);
        }

        /// <summary>
        /// Takes the atom with the most uncovered variables and covers them. egglog's <c>pop_min</c>.
        /// </summary>
        /// <param name="atom">The atom taken.</param>
        /// <returns><c>false</c> once every bucket is empty.</returns>
        public bool TryPop(out AtomId atom)
        {
            var top = buckets.Length - 1;
            while (top >= 0 && buckets[top] is null)
                top--;

            if (top < 0)
            {
                atom = default;
                return false;
            }

            var bucket = buckets[top]!;
            bucket.Pop(out atom);
            if (bucket.Count == 0)
                buckets[top] = null;

            var newly = atomInfo[atom];
            var vars = new FixedBitSet(newly.Length);
            vars.UnionWith(newly);
            for (var v = 0; v < vars.Length; v++)
            {
                if (vars.Contains(v) == false || cover.Contains(v))
                    continue;

                foreach (var subatom in ctx.Variables[new Variable(v)].Occurrences)
                {
                    var set = atomInfo[subatom.Atom];
                    var oldSize = set.Count;
                    set.ExceptWith(vars);
                    var newSize = set.Count;
                    if (oldSize == newSize)
                        continue;

                    var old = buckets[oldSize];
                    if (old is not null)
                    {
                        old.SwapRemove(subatom.Atom);
                        if (old.Count == 0)
                            buckets[oldSize] = null;
                    }

                    if (newSize > 0)
                        (buckets[newSize] ??= new AtomIndexSet()).Insert(subatom.Atom);
                }
            }

            cover.UnionWith(vars);
            return true;
        }

    }

}
