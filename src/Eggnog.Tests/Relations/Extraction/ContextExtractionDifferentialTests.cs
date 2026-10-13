using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The context extraction differential (colored-contexts spec §10.10, rule-layer E5): random small programs run on a
    /// chain of contexts, and the deepest context's extraction is compared with extraction at a fresh root that replays
    /// the chain's steps. Ids differ between the two, so classes are matched through the terms the program made, and the
    /// extracted terms are compared up to cost ties: equal costs, and each side's term, rebuilt in the other e-graph,
    /// is a term of the matching class there.
    /// </summary>
    public class ContextExtractionDifferentialTests
    {

        [Fact]
        public void ExtractionInAChildEqualsExtractionAtAFreshRootReplayingTheChain()
        {
            var compared = 0;
            for (var seed = 0; seed < 400; seed++)
            {
                try
                {
                    compared += Compare(seed);
                }
                catch (Exception e) when (e is not Xunit.Sdk.IAssertionException)
                {
                    throw new InvalidOperationException($"Seed {seed} failed: {e.Message}", e);
                }
            }

            Assert.True(compared > 2000, $"only {compared} classes were compared");
        }

        /// <summary>
        /// Runs seed <paramref name="seed"/> both ways and compares every class the program's terms reach.
        /// </summary>
        /// <returns>The number of classes compared.</returns>
        static int Compare(int seed)
        {
            var program = new ContextExtractionProgram(seed);
            var chained = new ContextExtractionWorld(program);
            var (chain, chainTerms) = chained.RunChain();
            var deepest = chain[^1];
            var replayed = new ContextExtractionWorld(program);
            var replayTerms = replayed.Replay();
            var root = replayed.Fx.EGraph.Root;
            var where = $"seed {seed} (depth {program.Depth}, frozen {string.Join("", program.Frozen.Select(f => f ? 'F' : 'w'))})";

            // The classes correspond one to one through the terms.
            var toReplay = new Dictionary<Value, Value>();
            var toChain = new Dictionary<Value, Value>();
            for (var t = 0; t < program.TermCount; t++)
            {
                var c = deepest.Find(chainTerms[t]);
                var r = root.Find(replayTerms[t]);
                Assert.True(toReplay.TryAdd(c, r) || toReplay[c] == r, $"{where}: term {t} splits a class of the chain");
                Assert.True(toChain.TryAdd(r, c) || toChain[r] == c, $"{where}: term {t} splits a class of the replay");
            }

            var inChain = Extractor.CreateOnDemand(chained.Fx.Schema, deepest);
            var atRoot = Extractor.CreateOnDemand(replayed.Fx.Schema, root);
            foreach (var (c, r) in toReplay)
            {
                Assert.True(inChain.TryExtractTerm(chained.E, c, out var mine), $"{where}: class {c} has no term in the chain");
                Assert.True(atRoot.TryExtractTerm(replayed.E, r, out var theirs), $"{where}: class {r} has no term in the replay");
                Assert.True(mine.Cost == theirs.Cost, $"{where}: {mine} costs {mine.Cost} in the chain, {theirs} costs {theirs.Cost} in the replay");
                Assert.True(r == Rebuild(mine, chained, replayed, (f, key) => root.LookupId(f, key), root.Find), $"{where}: {mine} is not a term of the replay's class");
                Assert.True(c == Rebuild(theirs, replayed, chained, (f, key) => deepest.LookupId(f, key), deepest.Find), $"{where}: {theirs} is not a term of the chain's class");
            }

            for (var level = chain.Count - 1; level > 0; level--)
                chain[level].Dispose();

            return toReplay.Count;
        }

        /// <summary>
        /// Rebuilds an extracted term of one e-graph in another, bottom up, by looking each node up there; base values are
        /// carried over by content.
        /// </summary>
        /// <returns>The canonical class of the rebuilt term, or <c>null</c> when a node does not exist there.</returns>
        static Value? Rebuild(ExtractedTerm term, ContextExtractionWorld from, ContextExtractionWorld to, Func<FunctionId, Value[], Value?> lookup, Func<Value, Value> find)
        {
            var key = new Value[term.Arity];
            for (var i = 0; i < key.Length; i++)
            {
                var child = term.Child(i);
                if (child.Kind == ExtractedTermKind.Base)
                {
                    key[i] = to.Fx.Int(from.Fx.EGraph.BaseValues.Unwrap<Int64Codec, long>(child.Value));
                    continue;
                }

                if (Rebuild(child, from, to, lookup, find) is not { } rebuilt)
                    return null;

                key[i] = rebuilt;
            }

            var function = to.Functions[Array.IndexOf(from.Functions, term.Function)];
            return lookup(function, key) is { } found ? find(found) : null;
        }

    }

}
