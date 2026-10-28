using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// The generator's model of one scope of a random container program: its place in the tree, whether it is frozen
    /// or disposed, the next fresh key of its own range, and the keys its writes gave a container.
    /// </summary>
    sealed class ContainerProgramScopeModel
    {

        /// <summary>
        /// Initializes a scope's model.
        /// </summary>
        /// <param name="id">The scope's number.</param>
        /// <param name="parent">The parent's model; <c>null</c> for the root.</param>
        public ContainerProgramScopeModel(int id, ContainerProgramScopeModel? parent)
        {
            Id = id;
            Parent = parent;
            parent?.Children.Add(this);
        }

        /// <summary>Gets the scope's number.</summary>
        public int Id { get; }

        /// <summary>Gets the parent's model; <c>null</c> for the root.</summary>
        public ContainerProgramScopeModel? Parent { get; }

        /// <summary>Gets the children's models.</summary>
        public List<ContainerProgramScopeModel> Children { get; } = new();

        /// <summary>Gets or sets whether the scope is frozen.</summary>
        public bool Frozen { get; set; }

        /// <summary>Gets or sets whether the scope is disposed.</summary>
        public bool Disposed { get; set; }

        /// <summary>
        /// Gets the first key of the scope's range: a write takes a key no other scope's range holds, so no write
        /// conflicts with another in any view, at the root or in the replay, but for an echo
        /// (<see cref="ContainerProgramGenerator"/>), which writes a descendant's key with a value equal to the
        /// descendant's in every view that sees both.
        /// </summary>
        public long Base => 100L * Id;

        /// <summary>Gets the keys the scope's writes gave a container in any function, an echo's included.</summary>
        public List<long> Keys { get; } = new();

        /// <summary>Gets the keys the scope's writes gave a vector in <c>fv</c>.</summary>
        public List<long> VectorKeys { get; } = new();

        /// <summary>Gets or sets the next fresh key, past the base; key 0 is the setup's.</summary>
        public int NextKey { get; set; } = 1;

        /// <summary>
        /// Gets whether a descendant of the scope is live.
        /// </summary>
        public bool HasLiveDescendants => Children.Any(child => child.Disposed == false || child.HasLiveDescendants);

        /// <summary>
        /// Gets the scope's chain, from the root to the scope.
        /// </summary>
        public List<ContainerProgramScopeModel> Chain()
        {
            var chain = new List<ContainerProgramScopeModel>();
            for (var walk = this; walk is not null; walk = walk.Parent)
                chain.Add(walk);

            chain.Reverse();
            return chain;
        }

    }

}
