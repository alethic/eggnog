using System;
using System.Threading;

using Xunit;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// Rejects a <see cref="Scope.Write"/> batch, after which it writes nothing more: a call of its writer from another
    /// thread, which the batch's thread guard refuses. A call that only fails writes nothing of its own, and the batch's
    /// other calls stand.
    /// </summary>
    public static class BatchRejection
    {

        /// <summary>
        /// Calls the writer from a new thread, which the guard refuses, rejecting the batch.
        /// </summary>
        /// <param name="writer">The batch's writer.</param>
        /// <param name="fact">A fact the refused call would write.</param>
        public static void FromAnotherThread(ScopeWriter writer, Fact fact)
        {
            Exception? refused = null;
            var thread = new Thread(() => refused = Record.Exception(() => writer.Assume(fact)));
            thread.Start();
            thread.Join();
            Assert.Contains("in use on another thread", Assert.IsType<InvalidOperationException>(refused).Message, StringComparison.Ordinal);
        }

    }

}
