using System;
using System.Collections.Generic;

namespace Eggnog.Storage
{

    /// <summary>
    /// Compares a <see cref="RowSet"/>'s slots by their keys, so that its key index can be a set of slot numbers.
    /// Slot -1 stands for the thread's probe buffer, which is how a key that is not in the buffer is looked up
    /// without allocating.
    /// </summary>
    internal sealed class RowKeyComparer : IEqualityComparer<int>
    {

        readonly RowSet set;

        public RowKeyComparer(RowSet set) => this.set = set;

        public bool Equals(int x, int y) => set.KeyOf(x).SequenceEqual(set.KeyOf(y));

        public int GetHashCode(int slot) => RowSet.Hash(set.KeyOf(slot));

    }

}
