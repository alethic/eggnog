using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Records its value into a shared list when disposed: the <c>DropRecorder</c> of egglog's
    /// concurrency/src/tests.rs:691-719 and 758-787. A struct, so that disposal of the arena's boxed copy is observable
    /// through <see cref="Drops"/> while copies held elsewhere stay untouched.
    /// </summary>
    readonly struct SharedArenaDisposeRecorder : IDisposable
    {

        public SharedArenaDisposeRecorder(int value, List<int> drops)
        {
            Value = value;
            Drops = drops;
        }

        public int Value { get; }

        public List<int> Drops { get; }

        public void Dispose()
        {
            lock (Drops)
                Drops.Add(Value);
        }

    }

}
