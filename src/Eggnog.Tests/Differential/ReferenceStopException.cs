using System;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// Unwinds a reference run that reached its iteration limit, as the rule layer's executor does.
    /// </summary>
    internal sealed class ReferenceStopException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public ReferenceStopException()
            : base("The reference run reached its iteration limit.")
        {

        }

    }

}
