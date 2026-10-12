using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal sealed partial class Extractor
    {

        /// <summary>
        /// Reused stack of reconstruction frames.
        /// </summary>
        readonly Stack<ExtractionFrame> frames = new();

        /// <summary>
        /// Builds the term of a value whose cost is known, children first, memoizing every value it builds: egglog's
        /// <c>reconstruct_termdag_node_helper</c> (src/extract.rs:513-567), with an explicit stack so that deep terms do
        /// not overflow the call stack. An e-class is built from its chosen node, whose child columns are built in turn; a
        /// container from its elements; a base value directly.
        /// </summary>
        /// <remarks>
        /// Terminates because the chosen nodes are acyclic: every class a choice depends on became final before it.
        /// O(distinct values in the term).
        /// </remarks>
        /// <typeparam name="TBuilder">The builder; a struct, so the walk is specialized for it.</typeparam>
        /// <typeparam name="TResult">What a built term is.</typeparam>
        /// <param name="sort">The value's sort index.</param>
        /// <param name="value">The value, canonical; it must have a cost.</param>
        /// <param name="builder">Builds each term.</param>
        /// <param name="memo">The terms built so far, by (sort, value); shared across calls that build into the same
        /// place.</param>
        /// <returns>The term.</returns>
        TResult Build<TBuilder, TResult>(int sort, uint value, ref TBuilder builder, Dictionary<ulong, TResult> memo)
            where TBuilder : struct, IExtractionBuilder<TResult>
        {
            var rootKey = Key(sort, value);
            if (memo.TryGetValue(rootKey, out var built))
                return built;

            var arguments = new List<TResult>();
            frames.Clear();
            frames.Push(new ExtractionFrame(sort, value, false));
            while (frames.TryPop(out var frame))
            {
                var key = Key(frame.Sort, frame.Value);
                if (memo.ContainsKey(key))
                    continue;

                switch (sortKinds[frame.Sort])
                {
                    case SortKind.Base:
                        memo.Add(key, builder.Base(frame.Sort, frame.Value));
                        break;
                    case SortKind.Eq:
                    {
                        var owner = FindClass(frame.Sort, frame.Value);
                        Debug.Assert(owner >= 0 && classBest[owner] >= 0, "only classes with a cost are built");
                        var node = classBest[owner];
                        var slot = nodeSlot[node];
                        var columnSorts = slotColumnSorts[slot];
                        var row = nodeRow[node];
                        var children = slots[slot].ChildCount;
                        if (frame.Expanded == false)
                        {
                            frames.Push(frame with { Expanded = true });
                            for (var i = children - 1; i >= 0; i--)
                            {
                                Debug.Assert(
                                    sortKinds[columnSorts[i]] != SortKind.Eq
                                        || classRank[FindClass(columnSorts[i], rowCells[row + i])] < classRank[owner],
                                    "choices are acyclic");
                                if (memo.ContainsKey(Key(columnSorts[i], rowCells[row + i])) == false)
                                    frames.Push(new ExtractionFrame(columnSorts[i], rowCells[row + i], false));
                            }

                            break;
                        }

                        arguments.Clear();
                        for (var i = 0; i < children; i++)
                            arguments.Add(memo[Key(columnSorts[i], rowCells[row + i])]);

                        memo.Add(key, builder.Node(frame.Sort, frame.Value, node, CollectionsMarshal.AsSpan(arguments)));
                        break;
                    }
                    default:
                    {
                        var container = containerIndex[key];
                        var start = containerElemStart[container];
                        var end = start + containerElemCount[container];
                        if (frame.Expanded == false)
                        {
                            frames.Push(frame with { Expanded = true });
                            for (var i = end - 1; i >= start; i--)
                                if (memo.ContainsKey(Key(elemSort[i], elemValue[i])) == false)
                                    frames.Push(new ExtractionFrame(elemSort[i], elemValue[i], false));

                            break;
                        }

                        arguments.Clear();
                        for (var i = start; i < end; i++)
                            arguments.Add(memo[Key(elemSort[i], elemValue[i])]);

                        memo.Add(key, builder.Container(frame.Sort, frame.Value, container, CollectionsMarshal.AsSpan(arguments)));
                        break;
                    }
                }
            }

            return memo[rootKey];
        }

        /// <summary>
        /// Gets the node chosen for the class of <paramref name="value"/>, for tests: its function and its row.
        /// </summary>
        /// <param name="sort">An e-class sort.</param>
        /// <param name="value">The class.</param>
        /// <param name="function">The chosen node's function.</param>
        /// <param name="row">The chosen node's schema columns.</param>
        /// <param name="rank">The order in which the class became final.</param>
        /// <returns>Whether the class has a cost.</returns>
        internal bool TryGetChoice(Sort sort, Value value, out FunctionId function, out Value[] row, out int rank)
        {
            function = default;
            row = [];
            rank = -1;
            if (TryGetCost(sort, value, out _) == false)
                return false;

            var owner = FindClass(SortIndexOf(sort), egraph.GetCanonInUf(value).Rep);
            var node = classBest[owner];
            var slot = nodeSlot[node];
            function = slots[slot].Id;
            row = new Value[slotColumnSorts[slot].Length];
            for (var i = 0; i < row.Length; i++)
                row[i] = new Value(rowCells[nodeRow[node] + i]);

            rank = classRank[owner];
            return true;
        }

    }

}
