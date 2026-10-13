using System;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class ContextTables
    {

        /// <summary>
        /// Freezes the context's layering: seals every hidden set, drops the views built so far, and builds the view of
        /// every table registered now, eagerly, folding every level's hides into each layer's frozen part (a frozen
        /// view has no live part). From then on the arrays never change: a table added later is answered through the
        /// parent, and the views may be read from any thread. Called by <see cref="Context.Freeze"/> on the domain
        /// thread, after the database froze its tables.
        /// </summary>
        /// <remarks>Cost: O(tables × depth) plus the folds, once.</remarks>
        internal void Freeze()
        {
            if (frozen)
                return;

            SealHidden();
            InvalidateViews(true);
            TableView?[] cache;
            lock (layerLock)
            {
                var count = schema.TableCount;
                if (slots.Length < count)
                    GrowLocked(count - 1);

                cache = views;
                frozen = true;
            }

            for (var i = 0; i < cache.Length; i++)
            {
                var id = new TableId(i);
                if (id == schema.UnionFind || schema[id] is not { } s)
                    continue;

                Volatile.Write(ref cache[i], BuildView(s));
            }

            Interlocked.Increment(ref viewGeneration);
        }

    }

}
