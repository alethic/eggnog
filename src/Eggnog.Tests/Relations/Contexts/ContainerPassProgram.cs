using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A random program of <see cref="ContainerPassDifferentialTests"/>: how the root is built
    /// (<see cref="ContainerPassWorld"/>), and the batches run in the child and the grandchild. A batch is a few writes,
    /// then a few unions of classes, then a rebuild. Deterministic in its seed, so two worlds built from it hold the
    /// same ids.
    /// </summary>
    /// <remarks>
    /// A step is <c>('u', a, b)</c>, the union of classes <c>a</c> and <c>b</c>; <c>('f', k, n)</c>, the write
    /// <c>f(k) = n</c> of the max function, where <c>k</c> indexes the world's classes, then its vectors, then its sets
    /// (<see cref="ContainerPassWorld.Resolve"/>); or <c>('c', a, b)</c>, the write <c>c(a) = b</c> of the
    /// constructor over classes, which unions <c>b</c> with the value <c>c(a)</c> has, if any. Writes name canonical
    /// ids, as the frontend's do; every third seed has them, drawn from a second generator so that the unions of every
    /// seed stay what they were without them.
    /// </remarks>
    sealed class ContainerPassProgram
    {

        /// <summary>
        /// Initializes the program of <paramref name="seed"/>. Every fifth seed is large: 1300 vectors, nearly all of
        /// them holding the last class, which the first union of the child displaces, so the child claims more than
        /// 1000 of them and its later rebuilds take egglog's incremental path. A small program is never large: 3 to 6
        /// classes, 3 to 8 vectors and up to 4 sets, each of one or two elements, so that unions make containers equal
        /// often.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <param name="small">Whether the world is small.</param>
        public ContainerPassProgram(int seed, bool small = false)
        {
            Seed = seed;
            Small = small;
            var random = new Random(seed);
            var writes = new Random(seed ^ 0x77e5);
            Large = small == false && seed % 5 == 0;
            Classes = small ? random.Next(3, 7) : Large ? random.Next(20, 40) : random.Next(5, 16);
            VectorCount = small ? random.Next(3, 9) : Large ? 1300 : random.Next(3, 30);
            SetCount = small ? random.Next(0, 5) : Large ? 40 : random.Next(0, 12);
            FreezeRoot = random.Next(2) == 0;
            FreezeChild = random.Next(2) == 0;
            Writes = writes.Next(3) == 0;

            var batches = random.Next(1, 4) + (Large ? 1 : 0);
            for (var i = 0; i < batches; i++)
                ChildBatches.Add(Batch(random, writes, Large && i == 0));

            if (random.Next(3) == 0)
            {
                var more = random.Next(1, 3);
                for (var i = 0; i < more; i++)
                    GrandchildBatches.Add(Batch(random, writes, false));
            }
        }

        /// <summary>
        /// Gets the seed.
        /// </summary>
        public int Seed { get; }

        /// <summary>
        /// Gets whether the program is large: 1300 vectors, nearly all holding the last class.
        /// </summary>
        public bool Large { get; }

        /// <summary>
        /// Gets whether the program is small: a few classes, and a few containers of one or two elements.
        /// </summary>
        public bool Small { get; }

        /// <summary>
        /// Gets the number of classes, the root's first ids.
        /// </summary>
        public int Classes { get; }

        /// <summary>
        /// Gets the number of vectors interned (some may be equal).
        /// </summary>
        public int VectorCount { get; }

        /// <summary>
        /// Gets the number of sets interned (some may be equal).
        /// </summary>
        public int SetCount { get; }

        /// <summary>
        /// Gets whether the root is frozen before the child is created.
        /// </summary>
        public bool FreezeRoot { get; }

        /// <summary>
        /// Gets whether the child is frozen before the grandchild is created.
        /// </summary>
        public bool FreezeChild { get; }

        /// <summary>
        /// Gets whether the batches write rows as well as union classes.
        /// </summary>
        public bool Writes { get; }

        /// <summary>
        /// Gets the child's batches of steps.
        /// </summary>
        public List<List<(char Kind, uint A, uint B)>> ChildBatches { get; } = new();

        /// <summary>
        /// Gets the grandchild's batches of steps; empty for no grandchild.
        /// </summary>
        public List<List<(char Kind, uint A, uint B)>> GrandchildBatches { get; } = new();

        /// <summary>
        /// Describes the program for a failure message.
        /// </summary>
        /// <returns>The description.</returns>
        public string Describe()
        {
            var text = new StringBuilder();
            text.AppendLine($"seed {Seed}{(Small ? " (small)" : "")}: {Classes} classes, {VectorCount} vectors, {SetCount} sets; root {(FreezeRoot ? "frozen" : "writable")}; child {(FreezeChild ? "frozen" : "writable")} under the grandchild");
            foreach (var batch in ChildBatches)
                text.AppendLine($"  child: {Describe(batch)}");

            foreach (var batch in GrandchildBatches)
                text.AppendLine($"  grandchild: {Describe(batch)}");

            return text.ToString();
        }

        /// <summary>
        /// Describes one batch: <c>a=b</c> for a union, <c>f(k)=n</c> and <c>c(a)=b</c> for writes.
        /// </summary>
        static string Describe(List<(char Kind, uint A, uint B)> batch) =>
            string.Join(" ", batch.Select(s => s.Kind == 'u' ? $"{s.A}={s.B}" : $"{s.Kind}({s.A})={s.B}"));

        /// <summary>
        /// Draws a batch: its writes from <paramref name="writes"/> when the program has them, then its unions of
        /// classes from <paramref name="random"/>, after the union of the first class with the hub, the last, when
        /// <paramref name="hub"/>.
        /// </summary>
        List<(char Kind, uint A, uint B)> Batch(Random random, Random writes, bool hub)
        {
            var steps = new List<(char, uint, uint)>();
            if (Writes)
            {
                var count = writes.Next(0, 3);
                for (var i = 0; i < count; i++)
                {
                    if (writes.Next(2) == 0)
                        steps.Add(('f', (uint)writes.Next(Classes + VectorCount + SetCount), (uint)writes.Next(10)));
                    else
                        steps.Add(('c', (uint)writes.Next(Classes), (uint)writes.Next(Classes)));
                }
            }

            if (hub)
                steps.Add(('u', 0u, (uint)(Classes - 1)));

            var unions = Large ? random.Next(1, 6) : random.Next(1, 4);
            for (var i = 0; i < unions; i++)
                steps.Add(('u', (uint)random.Next(Classes), (uint)random.Next(Classes)));

            return steps;
        }

    }

}
