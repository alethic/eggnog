using System;
using System.Diagnostics;
using System.Threading;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// While alive, makes a failed <see cref="Debug.Assert(bool)"/> throw <see cref="DebugAssertFailedException"/>
    /// by putting a <see cref="DebugAssertListener"/> first in <see cref="Trace.Listeners"/>. Scopes nest and may
    /// overlap across threads; the listener is removed when the last one ends.
    /// </summary>
    sealed class DebugAssertScope : IDisposable
    {

        static readonly object gate = new();
        static readonly DebugAssertListener listener = new();
        static int active;

        int disposed;

        /// <summary>
        /// Begins a scope.
        /// </summary>
        public DebugAssertScope()
        {
            lock (gate)
                if (active++ == 0)
                    Trace.Listeners.Insert(0, listener);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            lock (gate)
                if (--active == 0)
                    Trace.Listeners.Remove(listener);
        }

    }

}
