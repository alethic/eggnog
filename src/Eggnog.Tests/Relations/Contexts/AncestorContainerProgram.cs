using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A random program of <see cref="AncestorContainerDifferentialTests"/>: the world its root is built from (a
    /// <see cref="ContainerPassProgram"/> of the same seed, whose batches it does not run), the shape of the chain, and
    /// the steps the chain's levels take in turn. Deterministic in its seed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chain is a writable root with a child and a grandchild, or a frozen root with a writable parent, its child
    /// and a grandchild; the writable root or parent is the ancestor. A step is
    /// <c>(actor, kind, a, b, c, check, defer)</c>: the actor is <c>a</c> (the ancestor), <c>c</c> (the child) or
    /// <c>g</c> (the grandchild); the kinds are <c>u</c>, the union of classes <c>a</c> and <c>b</c> (modulo the
    /// classes); <c>v</c>, the union of two vector (<c>a</c> even) or set ids, the <c>b</c>-th and <c>c</c>-th of those
    /// known, which renames one container onto the other's id; <c>i</c> and <c>s</c>, a vector or set interned at the
    /// writable root, of <c>1 + a % 3</c> elements drawn from a generator seeded with <c>b</c>, classes or earlier
    /// vectors, canonical at the root; <c>f</c>, the write <c>f(k) = b % 10</c> of the max function, <c>k</c> the
    /// <c>a</c>-th of the classes, vectors and sets; and <c>w</c>, the write <c>c(a) = b</c> of the constructor over
    /// classes, which unions <c>b</c> with the value the key has, if any; and, in a small world, <c>r</c>, the
    /// ancestor's repeat of the <c>a</c>-th of the unions of classes or container ids its descendants made so far, if
    /// any, as a case split's parent learns what a case knew. The actor rebuilds before and after its step,
    /// but a step of the child or the grandchild with <c>defer</c> set leaves out the rebuild after it, so its unions
    /// wait in its journal, ahead of its container pass, while the steps that follow run, the ancestor's writes among
    /// them, until the descendant's next rebuild, its own or a descendant's, catches up with both at once. A step with
    /// <c>check</c> set is followed by a comparison of the child and the grandchild with their oracles. The
    /// <c>defer</c> flags come from a generator of their own, so a seed's other steps are what they were without them.
    /// </para>
    /// <para>
    /// A large world's first step is the child's union of the first class with the hub, the last, which nearly every
    /// vector holds, so the child claims more than 1000 vectors and its later rebuilds take egglog's incremental path.
    /// A small world (<see cref="ContainerPassProgram.Small"/>) has 16 to 39 steps, where another has 8 to 24; its
    /// grandchild takes three steps in ten rather than two, its ancestor repeats its descendants' unions and unions
    /// container ids more often, and every step that is not deferred is checked, so that the levels' claims, the
    /// ancestor's unions that make a claimed copy equal to its own content and its renames onto a claimed id meet
    /// often, and are seen before later unions make every container equal: a child and a grandchild claiming two
    /// containers before the ancestor repeats the child's union and unions their ids, for one.
    /// </para>
    /// </remarks>
    sealed class AncestorContainerProgram
    {

        /// <summary>
        /// Initializes the program of <paramref name="seed"/>, over a small world when <paramref name="small"/>.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <param name="small">Whether the world is small (<see cref="ContainerPassProgram.Small"/>).</param>
        public AncestorContainerProgram(int seed, bool small = false)
        {
            Seed = seed;
            World = new ContainerPassProgram(seed, small);
            var random = new Random(seed ^ 0x4a5c);
            WritableRoot = random.Next(2) == 0;
            if (World.Large)
                Steps.Add(('c', 'u', 0, World.Classes - 1, 0, false, false));

            var deferrals = new Random(seed ^ 0x1d3f);

            var count = small ? random.Next(16, 40) : random.Next(8, 25);
            for (var i = 0; i < count; i++)
            {
                var draw = random.Next(10);
                var actor = draw < 4 ? 'a' : draw < (small ? 7 : 8) ? 'c' : 'g';

                var roll = random.Next(100);
                var kind = (actor, WritableRoot, small) switch
                {
                    ('a', true, false) => roll switch { < 35 => 'u', < 50 => 'v', < 65 => 'i', < 75 => 's', < 88 => 'f', _ => 'w' },
                    ('a', true, true) => roll switch { < 20 => 'u', < 45 => 'r', < 70 => 'v', < 80 => 'i', < 85 => 's', < 93 => 'f', _ => 'w' },
                    ('a', false, true) => roll switch { < 25 => 'u', < 50 => 'r', < 80 => 'v', < 90 => 'f', _ => 'w' },
                    _ => roll switch { < 50 => 'u', < 65 => 'v', < 85 => 'f', _ => 'w' },
                };

                var (a, b, c, check) = (random.Next(1 << 20), random.Next(1 << 20), random.Next(1 << 20), random.Next(3) == 0);
                var defer = actor != 'a' && deferrals.Next(3) == 0;
                Steps.Add((actor, kind, a, b, c, small ? defer == false : check, defer));
            }

            Interns = Steps.Count(step => step.Kind is 'i' or 's');
        }

        /// <summary>
        /// Gets the seed.
        /// </summary>
        public int Seed { get; }

        /// <summary>
        /// Gets the program the world is built from.
        /// </summary>
        public ContainerPassProgram World { get; }

        /// <summary>
        /// Gets whether the ancestor is a writable root; otherwise it is a writable parent of a frozen root.
        /// </summary>
        public bool WritableRoot { get; }

        /// <summary>
        /// Gets the number of steps that intern a container.
        /// </summary>
        public int Interns { get; }

        /// <summary>
        /// Gets the steps, in order.
        /// </summary>
        public List<(char Actor, char Kind, int A, int B, int C, bool Check, bool Defer)> Steps { get; } = new();

        /// <summary>
        /// Describes the program for a failure message.
        /// </summary>
        /// <returns>The description.</returns>
        public string Describe()
        {
            var text = new StringBuilder();
            text.AppendLine($"seed {Seed}{(World.Small ? " (small world)" : "")}: {World.Classes} classes, {World.VectorCount} vectors, {World.SetCount} sets; {(WritableRoot ? "writable root" : "frozen root, writable parent")}");
            foreach (var (actor, kind, a, b, c, check, defer) in Steps)
                text.AppendLine($"  {actor} {kind} {a} {b} {c}{(check ? " check" : "")}{(defer ? " defer" : "")}");

            return text.ToString();
        }

    }

}
