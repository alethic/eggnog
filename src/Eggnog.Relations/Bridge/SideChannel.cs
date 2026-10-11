using System.Diagnostics.CodeAnalysis;

namespace Eggnog.Relations
{

    /// <summary>
    /// A slot through which external functions pass a value to code outside the run, such as a panic message:
    /// egglog-bridge's <c>SideChannel&lt;T&gt;</c>, an <c>Arc&lt;Mutex&lt;Option&lt;T&gt;&gt;&gt;</c>
    /// (egglog-bridge/src/lib.rs:1435-1437). The instance itself is the shared handle.
    /// </summary>
    /// <remarks>
    /// Thread-safe: parallel rule tasks and merges may write it at once. Every operation takes one lock briefly.
    /// </remarks>
    /// <typeparam name="T">The value type.</typeparam>
    internal sealed class SideChannel<T>
    {

        readonly object sync = new();
        T? value;
        bool hasValue;

        /// <summary>
        /// Gets whether the channel holds a value.
        /// </summary>
        public bool HasValue
        {
            get
            {
                lock (sync)
                    return hasValue;
            }
        }

        /// <summary>
        /// Stores <paramref name="newValue"/> unless the channel already holds a value: how panics keep the first
        /// message (<c>if guard.is_none()</c>, lib.rs:1460-1463, 1509-1512).
        /// </summary>
        /// <param name="newValue">The value.</param>
        /// <returns><c>true</c> if stored.</returns>
        public bool TrySet(T newValue)
        {
            lock (sync)
            {
                if (hasValue)
                    return false;

                value = newValue;
                hasValue = true;
                return true;
            }
        }

        /// <summary>
        /// Stores <paramref name="newValue"/>, replacing any value.
        /// </summary>
        /// <param name="newValue">The value.</param>
        public void Set(T newValue)
        {
            lock (sync)
            {
                value = newValue;
                hasValue = true;
            }
        }

        /// <summary>
        /// Takes the value out of the channel, leaving it empty: egglog's <c>lock().unwrap().take()</c>.
        /// </summary>
        /// <param name="taken">The value, if there was one.</param>
        /// <returns><c>true</c> if the channel held a value.</returns>
        public bool TryTake([MaybeNullWhen(false)] out T taken)
        {
            lock (sync)
            {
                if (hasValue == false)
                {
                    taken = default;
                    return false;
                }

                taken = value!;
                value = default;
                hasValue = false;
                return true;
            }
        }

        /// <summary>
        /// Reads the value without taking it.
        /// </summary>
        /// <param name="current">The value, if there is one.</param>
        /// <returns><c>true</c> if the channel holds a value.</returns>
        public bool TryPeek([MaybeNullWhen(false)] out T current)
        {
            lock (sync)
            {
                if (hasValue == false)
                {
                    current = default;
                    return false;
                }

                current = value!;
                return true;
            }
        }

    }

}
