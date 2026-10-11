using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Appends the values of a container to a list. For cold paths: invariant checks and diagnostics.
    /// </summary>
    internal readonly struct ContainerElementCollector : IContainerElementVisitor
    {

        readonly List<uint> output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="output">Receives the values.</param>
        public ContainerElementCollector(List<uint> output)
        {
            this.output = output;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit(uint value) => output.Add(value);

    }

}
