using System;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerLayers
    {

        /// <summary>
        /// Freezes the layers, as part of the context's freeze, after its database and tables: the context's container
        /// log is drained and its counts stamped, never to grow again (<see cref="ContainerLog.Freeze"/>), so the
        /// descendants that existed before the freeze read its tail and then settle the context; every layer's shadowed
        /// ids stop changing and its own environment's size is stamped (<see cref="ContainerLayerBase.Freeze"/>); and the
        /// frozen flag is published last with a volatile write, so a reader on any thread that sees it reads the layers
        /// without locks. Domain thread only, while none of the context's rule tasks runs.
        /// </summary>
        public void Freeze()
        {
            Log.Freeze();
            foreach (var slot in Volatile.Read(ref slots))
                slot?.Freeze();

            frozen = true;
        }

        /// <summary>
        /// Drops the layers, as part of the context's dispose, next to its tables' release: no ancestor ever saw what
        /// they hold, so nothing else changes. The context's container log, inactive since its last descendant went, is
        /// cleared of anything left and lets go of the layers' own environments (<see cref="ContainerLog.Release"/>), so
        /// they can be collected while the disposed context is still referenced. Domain thread only, once the context
        /// has no live children.
        /// </summary>
        public void Release()
        {
            Log.Release();
            Volatile.Write(ref slots, Array.Empty<ContainerLayerBase?>());
        }

    }

}
