using System.Collections.Generic;
using System.Text;

using Spike = Eggnog.Storage;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What the <see cref="SpikePair"/>s of one differential program share (colored-contexts spec §17 M9.4): the engine
    /// store and its schema, the spike's store when the spike runs, the map from every engine id to the spike id minted
    /// with it, and the program's trace, printed when a check fails. Thread-safe, for programs whose children run
    /// concurrently.
    /// </summary>
    sealed class SpikeProgram
    {

        readonly object sync = new();
        readonly StringBuilder trace = new();
        readonly Dictionary<uint, uint> spikeIds = new();

        /// <summary>
        /// Initializes a program over a fresh store, with the spike as a second oracle when <paramref name="withSpike"/>.
        /// </summary>
        public SpikeProgram(bool withSpike)
        {
            Store = new Store(ParallelHeuristics.Serial);
            SpikeStore = withSpike ? new Spike.Store() : null;
            Schema = new SpikeTestSchema(Store, SpikeStore);
        }

        /// <summary>
        /// Gets the engine store.
        /// </summary>
        public Store Store { get; }

        /// <summary>
        /// Gets the spike's store, or <c>null</c>.
        /// </summary>
        public Spike.Store? SpikeStore { get; }

        /// <summary>
        /// Gets the schema.
        /// </summary>
        public SpikeTestSchema Schema { get; }

        /// <summary>
        /// Gets or sets whether a get-or-add that finds a row is checked against the oracle's view, which recomputes it
        /// (off for long programs).
        /// </summary>
        public bool CheckFoundRows { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the engine's lookup paths are compared after every write (off where another suite covers
        /// them and the program checks something else).
        /// </summary>
        public bool CheckWrites { get; set; } = true;

        /// <summary>
        /// Gets or sets whether rows written in children are passed to the engine raw: id cells as given (stale ones
        /// included) and timestamp 0, so the child's merge must canonicalize and stamp them itself (X38). Set before any
        /// child is created; only for programs without the spike, whose own writes canonicalize first.
        /// </summary>
        public bool RawChildWrites { get; set; }

        /// <summary>
        /// Gets or sets whether checks compare with the reference, which recomputes each view from every assertion (off
        /// for long lockstep programs, which compare with the spike at every check and with the reference at the end).
        /// </summary>
        public bool CompareWithReference { get; set; } = true;

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

        /// <summary>
        /// Records that the spike minted <paramref name="spike"/> with the engine's <paramref name="engine"/>.
        /// </summary>
        public void MapId(uint engine, uint spike)
        {
            lock (sync)
                spikeIds[engine] = spike;
        }

        /// <summary>
        /// Gets the spike id minted with the engine's <paramref name="engine"/>.
        /// </summary>
        public uint SpikeId(uint engine)
        {
            lock (sync)
                return spikeIds[engine];
        }

    }

}
