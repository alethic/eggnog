using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What the <see cref="ContextPair"/>s of one differential program share: the engine store and its schema, and the
    /// program's trace, printed when a check fails. Thread-safe, for programs whose children run concurrently.
    /// </summary>
    sealed class ContextProgram
    {

        readonly object sync = new();
        readonly StringBuilder trace = new();

        /// <summary>
        /// Initializes a program over a fresh store.
        /// </summary>
        public ContextProgram()
        {
            Store = new Store(ParallelHeuristics.Serial);
            Schema = new ContextTestSchema(Store);
        }

        /// <summary>
        /// Gets the engine store.
        /// </summary>
        public Store Store { get; }

        /// <summary>
        /// Gets the schema.
        /// </summary>
        public ContextTestSchema Schema { get; }

        /// <summary>
        /// Gets or sets whether a get-or-add that finds a row is checked against the oracle's view, which recomputes it
        /// (off for long programs).
        /// </summary>
        public bool CheckFoundRows { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the engine's lookup paths are compared after every write (off where another suite
        /// covers them and the program checks something else).
        /// </summary>
        public bool CheckWrites { get; set; } = true;

        /// <summary>
        /// Gets or sets whether rows written in children are passed to the engine raw: id cells as given (stale ones
        /// included) and timestamp 0, so the child's merge must canonicalize and stamp them itself. Set before any
        /// child is created.
        /// </summary>
        public bool RawChildWrites { get; set; }

        /// <summary>
        /// Gets the trace so far.
        /// </summary>
        public string Trace
        {
            get
            {
                lock (sync)
                    return trace.ToString();
            }
        }

        /// <summary>
        /// Appends a line to the trace.
        /// </summary>
        public void Note(string line)
        {
            lock (sync)
                trace.AppendLine(line);
        }

    }

}
