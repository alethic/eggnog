using System;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerLayers
    {

        /// <summary>
        /// Freezes the layers, as part of the context's freeze, after its database and tables: every layer's shadowed
        /// ids stop changing and its own environment's size is stamped (<see cref="ContainerLayerBase.Freeze"/>), and the
        /// frozen flag is published last with a volatile write, so a reader on any thread that sees it reads the layers
        /// without locks. Domain thread only, while none of the context's rule tasks runs.
        /// </summary>
        public void Freeze()
        {
            foreach (var slot in Volatile.Read(ref slots))
                slot?.Freeze();

            frozen = true;
        }

        /// <summary>
        /// Drops the layers, as part of the context's dispose, next to its tables' release: no ancestor ever saw what
        /// they hold, so nothing else changes. Domain thread only, once the context has no live children.
        /// </summary>
        public void Release() => Volatile.Write(ref slots, Array.Empty<ContainerLayerBase?>());

    }

}
