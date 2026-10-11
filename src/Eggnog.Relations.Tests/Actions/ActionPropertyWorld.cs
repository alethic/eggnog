using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The database a run of <see cref="ActionPropertyTests"/> executes against: its tables, counters and external
    /// functions, and the log the external functions append their calls to.
    /// </summary>
    sealed class ActionPropertyWorld
    {

        /// <summary>
        /// The database.
        /// </summary>
        public Database Db = null!;

        /// <summary>
        /// The sorted-key tables, of key widths 1, 2, 4, 1 and 0.
        /// </summary>
        public TableId T0, T1, T2, T3, T4;

        /// <summary>
        /// The displaced (union-find) table.
        /// </summary>
        public TableId U;

        /// <summary>
        /// The id counter the instructions mint from, and a counter only an external function advances.
        /// </summary>
        public CounterId Ids, Aux;

        /// <summary>
        /// The external functions.
        /// </summary>
        public ExternalFunctionId F0, F1, F2, F3;

        /// <summary>
        /// The calls the external functions received, in order: the function's name and its arguments.
        /// </summary>
        public List<(string Name, uint[] Args)> Log = new();

    }

}
