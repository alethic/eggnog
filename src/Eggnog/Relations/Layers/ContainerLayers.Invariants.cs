using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerLayers
    {

        /// <summary>
        /// Adds to <paramref name="violations"/> every way the layers break their invariants: each layer's own
        /// environment is consistent, holds entries only under ids local to the context or shadowed by the layer,
        /// shadows no local id, and, once the context is frozen, holds what it held at the freeze
        /// (<see cref="ContainerLayerBase.CheckInvariants"/>); each layer sits in the slot of its type, frozen with the
        /// context; the container mark lies within the journal, and nothing waits for the catch-up
        /// (<see cref="HasPending"/>); and the context's view of containers of classes is canonical, with each content
        /// once (<see cref="CheckView"/>). Requires that nothing changes the layers meanwhile, and a clean context.
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

            if (owner.Journal is { } journal && containerMark > journal.Count)
                Context.Report(violations, $"container layers: the container mark {containerMark} is beyond the journal's {journal.Count} rows");

            if (pending is { Count: > 0 } kept)
                Context.Report(violations, $"container layers: the context is clean but {kept.Count} entries of its ancestors' container logs wait for its catch-up");

            CheckView(violations);
        }

        /// <summary>
        /// Adds to <paramref name="violations"/> every way the context's view of containers of classes breaks what a
        /// clean context promises, each starting with <c>container view:</c>: every entry of its layers' own
        /// environments has a canonical id and canonical contents, and no level below shows its content under another
        /// id (contents are unique in the view; <see cref="ContainerLayerBase.CheckView"/>); and no container a level
        /// below holds under an id the context sees, the root included, holds an id the context's journal lists, or has
        /// one as its id: the context claimed and rebuilt every container that held a displacement of its own, and an
        /// ancestor rebuilt its own for the ids it displaced. O(journal × depth × types) probes, plus O(layers' size ×
        /// depth).
        /// </summary>
        void CheckView(List<string> violations)
        {
            if (owner.Journal is not { } journal || owner.Database.Containers is not { HasEqContainers: true } values)
                return;

            var rebuilder = owner.UnionFindTable.GetRebuilder(ReadOnlySpan<ColumnId>.Empty)!;
            var count = values.TypeCount;

            // Storage of the checker's own: a frozen context is checked from any thread, outside its domain.
            var seen = new OrderedValueSet();
            var holders = new OrderedValueSet();
            for (var i = 0; i < count; i++)
            {
                var type = ContainerTypeId.FromIndex(i);
                var root = values.GetEnvironment(type);
                if (root.HoldsIds == false)
                    continue;

                var layer = LayerAt(type);
                layer?.CheckView(this, root, rebuilder, owner.UnionFind, violations);
                foreach (var id in journal.Ids)
                {
                    if (idBlocks.Contains(id))
                        continue;

                    seen.Clear();
                    AddVisibleHoldersBelow(root, [id], holders, seen);
                    foreach (var holder in seen.AsSpan())
                        Context.Report(violations, $"container view: the container {holder} of type {type} that the context sees at a level below holds {id}, which is not canonical in the context");

                    if ((layer is null || Owns(layer, id) == false) && (parent is null ? root.ContainsId(id) : parent.ContainsId(root, id)))
                        Context.Report(violations, $"container view: the context sees a container of type {type} at a level below under {id}, which is not canonical in the context");
                }
            }
        }

    }

}
