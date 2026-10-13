using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An in-case schedule for the split tests: records every call (case, depth, nested splits allowed, phase order)
    /// and runs an optional body, whose result it returns.
    /// </summary>
    sealed class RecordingSchedule : ICaseSchedule
    {

        readonly Func<EGraphContext, int, bool, bool>? body;

        /// <summary>
        /// Initializes a schedule that runs <paramref name="body"/>, or does nothing and reports no change.
        /// </summary>
        public RecordingSchedule(Func<EGraphContext, int, bool, bool>? body = null)
        {
            this.body = body;
        }

        /// <summary>
        /// Gets the calls made, in order.
        /// </summary>
        public List<(EGraphContext Case, int Depth, bool AllowSplits)> Calls { get; } = new();

        /// <summary>
        /// Gets or sets a log shared with other recorders, to which each call appends a line.
        /// </summary>
        public List<string>? Log { get; set; }

        /// <inheritdoc />
        public bool Run(EGraphContext @case, int depth, bool allowSplits)
        {
            Calls.Add((@case, depth, allowSplits));
            Log?.Add($"run d{depth} {(allowSplits ? "B" : "A")}");
            return body?.Invoke(@case, depth, allowSplits) ?? false;
        }

    }

}
