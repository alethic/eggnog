using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerLayers
    {

        /// <summary>
        /// Adds to <paramref name="violations"/> every way the layers break their invariants: each layer's own environment
        /// is consistent, holds entries only under ids local to the context or shadowed by the layer, shadows no local id,
        /// and, once the context is frozen, holds what it held at the freeze
        /// (<see cref="ContainerLayerBase.CheckInvariants"/>); and each layer sits in the slot of its type, frozen with
        /// the context. Requires that nothing changes the layers meanwhile.
        /// </summary>
        /// <param name="violations">Receives one line per violation.</param>
        public void CheckInvariants(List<string> violations)
        {
            ArgumentNullException.ThrowIfNull(violations);

            var current = Volatile.Read(ref slots);
            for (var i = 0; i < current.Length; i++)
            {
                if (current[i] is not { } layer)
                    continue;

                if (layer.TypeId.Index != i)
                    Context.Report(violations, $"container layers: the layer of type {layer.TypeId} sits in slot {i}");

                if (frozen && layer.Shadowed.IsFrozen == false)
                    Context.Report(violations, $"container layers: the context is frozen but its layer of type {layer.TypeId} is not");

                layer.CheckInvariants(this, violations);
            }
        }

    }

}
