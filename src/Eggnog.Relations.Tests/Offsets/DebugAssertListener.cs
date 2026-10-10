using System.Diagnostics;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A trace listener that turns a failed assertion into a <see cref="DebugAssertFailedException"/> instead of
    /// letting the default listener end the process.
    /// </summary>
    sealed class DebugAssertListener : TraceListener
    {

        /// <inheritdoc />
        public override void Fail(string? message, string? detailMessage) => throw new DebugAssertFailedException(message);

        /// <inheritdoc />
        public override void Fail(string? message) => throw new DebugAssertFailedException(message);

        /// <inheritdoc />
        public override void Write(string? message)
        {

        }

        /// <inheritdoc />
        public override void WriteLine(string? message)
        {

        }

    }

}
