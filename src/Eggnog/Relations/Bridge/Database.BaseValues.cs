using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Database
    {

        /// <summary>
        /// The base value registry, created on first use.
        /// </summary>
        BaseValues? baseValues;

        /// <summary>
        /// Gets the database's base value registry: egglog's <c>base_values</c> and <c>base_values_mut</c>
        /// (core-relations/src/free_join/mod.rs:306, 341-347), through which rules, merge functions and external
        /// functions box and unbox base values (<see cref="ExecutionState.BaseValues"/>). Registering types and boxing
        /// values are thread-safe (see <see cref="Relations.BaseValues"/>). A clone of the database gets a registry with
        /// the same types that shares the intern tables, as egglog's derived clone does. A child context's database
        /// shares its root's registry by reference, as it shares the counters, external functions and containers, so
        /// base values mean the same in every context of a store.
        /// </summary>
        public BaseValues BaseValues => baseValues ?? CreateBaseValues();

        /// <summary>
        /// Creates the registry on first use; racing first uses agree on one instance.
        /// </summary>
        BaseValues CreateBaseValues()
        {
            Interlocked.CompareExchange(ref baseValues, new BaseValues(), null);
            return baseValues!;
        }

    }

}
