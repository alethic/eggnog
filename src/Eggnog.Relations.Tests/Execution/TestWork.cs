using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A <see cref="ScopedWork"/> that runs a delegate, for the <see cref="WorkScope"/> tests.
    /// </summary>
    sealed class TestWork : ScopedWork
    {

        readonly Action action;

        public TestWork(Action action)
        {
            this.action = action;
        }

        public override void Run() => action();

    }

}
