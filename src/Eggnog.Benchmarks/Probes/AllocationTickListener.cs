using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Eggnog.Benchmarks.Probes
{

    /// <summary>
    /// Listens in process to the runtime's <c>GCAllocationTick</c> events, which the runtime raises about once per
    /// 100 KB allocated on a thread, naming the type of the object that crossed the threshold and the bytes since the
    /// last tick; and totals the sampled bytes by type over the windows it is told to record.
    /// </summary>
    /// <remarks>
    /// The runtime delivers events to the listener on its own thread, after a delay, so a window is defined by event
    /// timestamps, not by when an event arrives: <see cref="Open"/> and <see cref="Close"/> record the window's bounds,
    /// and <see cref="Fence"/> waits until an event stamped after the last bound has arrived, by allocating until the
    /// runtime raises one. The totals are a sample: their shares estimate where the bytes go, and their sum
    /// approximates the bytes allocated in the windows.
    /// </remarks>
    internal sealed class AllocationTickListener : EventListener
    {

        /// <summary>
        /// The runtime's event source.
        /// </summary>
        const string RuntimeSource = "Microsoft-Windows-DotNETRuntime";

        /// <summary>
        /// The runtime's GC keyword, which enables the allocation tick at the verbose level.
        /// </summary>
        const EventKeywords GcKeyword = (EventKeywords)0x1;

        /// <summary>
        /// The event id of <c>GCAllocationTick</c>, in every version.
        /// </summary>
        const int AllocationTickId = 10;

        readonly object gate = new();
        readonly List<(DateTime Start, DateTime End)> windows = [];
        readonly Dictionary<string, long> bytesByType = new(StringComparer.Ordinal);
        DateTime? openedAt;
        DateTime lastEvent = DateTime.MinValue;
        long ticks;

        /// <summary>
        /// Gets the number of ticks counted in the windows.
        /// </summary>
        public long Ticks
        {
            get
            {
                lock (gate)
                    return ticks;
            }
        }

        /// <summary>
        /// Gets the sampled bytes of each type over the windows, largest first.
        /// </summary>
        /// <returns>The types and their sampled bytes.</returns>
        public List<KeyValuePair<string, long>> BytesByType()
        {
            lock (gate)
            {
                var result = new List<KeyValuePair<string, long>>(bytesByType);
                result.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
                return result;
            }
        }

        /// <summary>
        /// Starts a window: ticks stamped from now until <see cref="Close"/> are counted.
        /// </summary>
        /// <exception cref="InvalidOperationException">A window is already open.</exception>
        public void Open()
        {
            lock (gate)
            {
                if (openedAt != null)
                    throw new InvalidOperationException("A window is already open.");

                openedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Ends the open window.
        /// </summary>
        /// <exception cref="InvalidOperationException">No window is open.</exception>
        public void Close()
        {
            lock (gate)
            {
                if (openedAt is not { } start)
                    throw new InvalidOperationException("No window is open.");

                windows.Add((start, DateTime.UtcNow));
                openedAt = null;
            }
        }

        /// <summary>
        /// Waits until the runtime has delivered every tick stamped before now, by allocating until a tick stamped
        /// after now arrives.
        /// </summary>
        /// <param name="timeout">How long to wait.</param>
        /// <returns><c>true</c> when such a tick arrived in time.</returns>
        public bool Fence(TimeSpan timeout)
        {
            var now = DateTime.UtcNow;
            var deadline = now + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (gate)
                {
                    if (lastEvent > now)
                        return true;
                }

                // About one tick's worth of garbage, so the runtime raises an event soon.
                for (var i = 0; i < 16; i++)
                    GC.KeepAlive(new byte[8 * 1024]);

                Thread.Yield();
            }

            return false;
        }

        /// <inheritdoc />
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == RuntimeSource)
                EnableEvents(eventSource, EventLevel.Verbose, GcKeyword);
        }

        /// <inheritdoc />
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId != AllocationTickId || eventData.Payload == null || eventData.PayloadNames == null)
                return;

            var stamp = eventData.TimeStamp.ToUniversalTime();
            var amount = 0L;
            var type = "?";
            for (var i = 0; i < eventData.PayloadNames.Count; i++)
            {
                switch (eventData.PayloadNames[i])
                {
                    case "AllocationAmount64":
                        amount = Convert.ToInt64(eventData.Payload[i]);
                        break;
                    case "AllocationAmount" when amount == 0:
                        amount = Convert.ToInt64(eventData.Payload[i]);
                        break;
                    case "TypeName":
                        type = eventData.Payload[i] as string ?? "?";
                        break;
                }
            }

            lock (gate)
            {
                if (stamp > lastEvent)
                    lastEvent = stamp;

                if (InWindow(stamp) == false)
                    return;

                ticks++;
                bytesByType[type] = bytesByType.GetValueOrDefault(type) + amount;
            }
        }

        /// <summary>
        /// Gets whether <paramref name="stamp"/> falls in a closed window or after the open one's start.
        /// </summary>
        /// <param name="stamp">The event's timestamp, in UTC.</param>
        /// <returns><c>true</c> to count the event.</returns>
        bool InWindow(DateTime stamp)
        {
            if (openedAt is { } start && stamp >= start)
                return true;

            foreach (var (from, to) in windows)
                if (stamp >= from && stamp <= to)
                    return true;

            return false;
        }

    }

}
