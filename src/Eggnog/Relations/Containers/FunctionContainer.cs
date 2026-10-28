using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A function value: egglog's <c>FunctionContainer</c> (src/sort/fn.rs:21-70), its function and its partially applied
    /// arguments. The function is the id of its target (<see cref="Eggnog.FnTarget"/>), which names the constructor,
    /// function or primitive, as egglog's resolved id and name do. Each argument has a rebuild flag, set for one of an
    /// e-class or eq-container sort, as egglog's <c>rebuild_contents</c> rebuilds an argument by its sort; the flags are
    /// part of the container's identity, as the arguments' sorts are of egglog's. Holds any number of arguments, as
    /// egglog's vector does.
    /// </summary>
    internal readonly struct FunctionContainer : IContainerValue<FunctionContainer>
    {

        readonly uint[]? arguments;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="target">The target's id.</param>
        /// <param name="rebuild">The rebuild flags, one per argument.</param>
        /// <param name="arguments">The arguments' cells, owned by the container from here.</param>
        public FunctionContainer(int target, ArgumentFlags rebuild, uint[] arguments)
        {
            Target = target;
            Rebuild = rebuild;
            this.arguments = arguments;
        }

        /// <summary>
        /// Gets the target's id.
        /// </summary>
        public int Target { get; }

        /// <summary>
        /// Gets the rebuild flags, one per argument.
        /// </summary>
        public ArgumentFlags Rebuild { get; }

        /// <summary>
        /// Gets the arguments' cells.
        /// </summary>
        public ReadOnlySpan<uint> Arguments => arguments;

        /// <summary>
        /// Gets the arguments' cells, none for a default container.
        /// </summary>
        uint[] Cells => arguments ?? Array.Empty<uint>();

        /// <inheritdoc />
        public bool HoldsIds => Rebuild.IsEmpty == false;

        /// <inheritdoc />
        public ulong ComputeHash()
        {
            var hasher = new FxHasher();
            hasher.Write((ulong)(uint)Target);
            Rebuild.HashInto(ref hasher);
            foreach (var argument in Cells)
                hasher.Write(argument);

            return hasher.Finish();
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>rebuild_contents</c> (fn.rs:56-66): each flagged argument is canonicalized.</remarks>
        public bool TryRebuild(Rebuilder rebuilder, out FunctionContainer rebuilt)
        {
            var cells = Cells;
            uint[]? changed = null;
            for (var i = 0; i < cells.Length; i++)
            {
                if (Rebuild[i] == false)
                    continue;

                var value = rebuilder.RebuildValue(cells[i]);
                if (value == cells[i])
                    continue;

                changed ??= (uint[])cells.Clone();
                changed[i] = value;
            }

            if (changed is null)
            {
                rebuilt = default;
                return false;
            }

            rebuilt = new FunctionContainer(Target, Rebuild, changed);
            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Visits the flagged arguments only, where egglog's <c>iter</c> (fn.rs:67-69) yields every one, as
        /// <see cref="PairContainer.ForEachValue"/> does and for its reason.
        /// </remarks>
        public void ForEachValue<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IContainerElementVisitor
        {
            var cells = Cells;
            for (var i = 0; i < cells.Length; i++)
                if (Rebuild[i])
                    visitor.Visit(cells[i]);
        }

        /// <inheritdoc />
        public bool Equals(FunctionContainer other) =>
            Target == other.Target && Rebuild == other.Rebuild && Arguments.SequenceEqual(other.Arguments);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is FunctionContainer other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (int)ComputeHash();

        /// <inheritdoc />
        public override string ToString() => $"fn#{Target}[{string.Join(", ", Cells)}]";

    }

}
