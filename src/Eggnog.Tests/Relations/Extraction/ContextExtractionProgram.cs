using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A random small program over a chain of contexts, for the context extraction differential: terms made and classes
    /// unioned at each level, rule saturations in between, children created under frozen or writable parents, and
    /// writes to a writable parent after its child was created. The same program can be run on a chain of contexts or
    /// replayed, in order, at a fresh root.
    /// </summary>
    /// <remarks>
    /// The schema is one e-class sort <c>E</c> with constructors <c>L0</c>, <c>L1</c>, <c>Num(i64)</c>, <c>U(E)</c>,
    /// <c>W(E)</c> and <c>B(E, E)</c> of random costs from 0 to 3 (so cost ties are common), and two rules that
    /// saturate: <c>B(x, y) = z =&gt; B(y, x) = z</c> and <c>U(x) = u, W(u) = w =&gt; union(w, x)</c>. Every program
    /// fact is a term or a union, and every rule is monotone, so the deepest context's final saturated view is
    /// determined by the facts of its chain, whatever order they came in: a fresh root replaying them reaches the same
    /// e-graph up to the renaming of ids.
    /// </remarks>
    sealed class ContextExtractionProgram
    {

        /// <summary>
        /// The bound on rule iterations per saturation; the rules always saturate well within it.
        /// </summary>
        public const int MaxIterations = 64;

        readonly List<ContextExtractionStep> steps = new();
        readonly List<int> termLevels = new();

        /// <summary>
        /// Generates the program of <paramref name="seed"/>.
        /// </summary>
        public ContextExtractionProgram(int seed)
        {
            Seed = seed;
            var random = new Random(seed);
            Costs = Enumerable.Range(0, 6).Select(_ => (long)random.Next(0, 4)).ToArray();
            var depth = random.Next(1, 4);
            var frozen = new bool[depth + 1];
            for (var level = 0; level <= depth; level++)
            {
                if (level > 0)
                {
                    // Frozen levels are a prefix of the chain: a context freezes only under a frozen parent.
                    frozen[level - 1] = (level == 1 || frozen[level - 2]) && random.Next(2) == 0;
                    steps.Add(new ContextExtractionStep(level - 1, ContextExtractionStepKind.Child, frozen[level - 1], 0, 0, 0));
                }

                var count = random.Next(level == 0 ? 3 : 1, 12);
                var parentWrites = level > 0 && frozen[level - 1] == false ? random.Next(0, 4) : 0;
                for (var i = 0; i < count + parentWrites; i++)
                {
                    // Some writes go to the writable parent, after its child exists.
                    var at = parentWrites > 0 && random.Next(count + parentWrites) < parentWrites ? level - 1 : level;
                    if (at != level)
                        parentWrites--;

                    AddRandomStep(random, at);
                }
            }

            Depth = depth;
            Frozen = frozen;
        }

        /// <summary>
        /// Gets the seed.
        /// </summary>
        public int Seed { get; }

        /// <summary>
        /// Gets the depth of the deepest context.
        /// </summary>
        public int Depth { get; }

        /// <summary>
        /// Gets, per level, whether it is frozen before its child is created; the deepest level never is.
        /// </summary>
        public bool[] Frozen { get; }

        /// <summary>
        /// Gets the head costs of <c>L0</c>, <c>L1</c>, <c>Num</c>, <c>U</c>, <c>W</c> and <c>B</c>.
        /// </summary>
        public long[] Costs { get; }

        /// <summary>
        /// Gets the steps, in order.
        /// </summary>
        public IReadOnlyList<ContextExtractionStep> Steps => steps;

        /// <summary>
        /// Gets the number of terms the program makes.
        /// </summary>
        public int TermCount => termLevels.Count;

        /// <summary>
        /// Adds a random make, union or saturation step at <paramref name="level"/>, whose operands are terms made at
        /// that level or above.
        /// </summary>
        void AddRandomStep(Random random, int level)
        {
            var visible = Enumerable.Range(0, termLevels.Count).Where(t => termLevels[t] <= level).ToArray();
            int Pick() => visible[random.Next(visible.Length)];
            var kind = visible.Length < 2 ? ContextExtractionStepKind.Make : random.Next(10) switch
            {
                < 7 => ContextExtractionStepKind.Make,
                < 9 => ContextExtractionStepKind.Union,
                _ => ContextExtractionStepKind.Saturate,
            };

            switch (kind)
            {
                case ContextExtractionStepKind.Make:
                    var function = visible.Length == 0 ? random.Next(3) : random.Next(6);
                    var first = function switch
                    {
                        2 => random.Next(-2, 3),
                        >= 3 => Pick(),
                        _ => 0,
                    };
                    var second = function == 5 ? Pick() : 0;
                    steps.Add(new ContextExtractionStep(level, ContextExtractionStepKind.Make, false, function, first, second));
                    termLevels.Add(level);
                    break;
                case ContextExtractionStepKind.Union:
                    steps.Add(new ContextExtractionStep(level, ContextExtractionStepKind.Union, false, 0, Pick(), Pick()));
                    break;
                default:
                    steps.Add(new ContextExtractionStep(level, ContextExtractionStepKind.Saturate, false, 0, 0, 0));
                    break;
            }
        }

    }

}
