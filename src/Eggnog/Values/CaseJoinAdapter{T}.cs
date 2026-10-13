using System;

namespace Eggnog
{

    /// <summary>
    /// Adapts a lattice's case join (<see cref="ValueLattice{T}.CaseJoin"/>) to the cell form a lift calls
    /// (<c>LiftTable</c>'s case join): <c>(a, b) → Encode(CaseJoin(Decode(a), Decode(b)))</c>.
    /// </summary>
    /// <remarks>
    /// The delegate runs on the thread that lifts, once per pair of live cases per lifted key, under
    /// <see cref="EngineCallMarker"/>, so a call into a graph from the case join throws; it allocates what the case
    /// join and the interning of a new value allocate. An exception from the case join becomes a
    /// <see cref="RuleEvaluationException"/> (no rule owns a lift, so its rule name is <c>null</c>), which ends the
    /// lift before the round's writes are staged: nothing of that round is written.
    /// </remarks>
    /// <typeparam name="T">The value type.</typeparam>
    internal static class CaseJoinAdapter<T>
    {

        /// <summary>
        /// Creates the cell form of <paramref name="lattice"/>'s case join over <paramref name="type"/>, or <c>null</c>
        /// when it has none (a lift then requires every live case to agree).
        /// </summary>
        /// <param name="lattice">The lattice.</param>
        /// <param name="type">The function's output type.</param>
        /// <param name="functionName">The function's name, for messages.</param>
        /// <returns>The case join over cells, or <c>null</c>.</returns>
        public static Func<uint, uint, uint>? Create(ValueLattice<T> lattice, ColumnType<T> type, string functionName)
        {
            ArgumentNullException.ThrowIfNull(lattice);
            ArgumentNullException.ThrowIfNull(type);
            if (lattice.CaseJoin is not { } join)
                return null;

            return (a, b) =>
            {
                EngineCallMarker.Enter();
                try
                {
                    return type.Encode(join(type.Decode(a, null), type.Decode(b, null)));
                }
                catch (Exception exception) when (exception is not RuleEvaluationException)
                {
                    throw new RuleEvaluationException(null, null,
                        $"the case join of lattice '{lattice.Name}' of function '{functionName}' threw {exception.GetType().Name}: {exception.Message}", exception);
                }
                finally
                {
                    EngineCallMarker.Exit();
                }
            };
        }

    }

}
