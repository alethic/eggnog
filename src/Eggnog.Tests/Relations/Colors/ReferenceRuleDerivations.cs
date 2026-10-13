using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What the rules of one reference iteration derived (colored-contexts spec §17 M10.3): rows and unions, in the
    /// order their matches ran, and the ids minted for missing constructor terms, one per term and iteration, as one
    /// engine execution state mints one id per missing key (the engine's other states mint their own, which the
    /// constructor's union merge reconciles; term names make the two agree).
    /// </summary>
    sealed class ReferenceRuleDerivations
    {

        readonly ReferenceRuleClock clock;
        readonly Dictionary<string, ulong> mints = new();

        /// <summary>
        /// Initializes empty derivations minting from <paramref name="clock"/>.
        /// </summary>
        public ReferenceRuleDerivations(ReferenceRuleClock clock)
        {
            this.clock = clock;
        }

        /// <summary>
        /// Gets the derived rows, by table.
        /// </summary>
        public List<(SpikeTable Table, ulong[] Row)> Rows { get; } = new();

        /// <summary>
        /// Gets the derived unions.
        /// </summary>
        public List<(ulong A, ulong B)> Unions { get; } = new();

        /// <summary>
        /// Gets the id minted in this iteration for the missing term <paramref name="table"/>(<paramref name="key"/>),
        /// minting it and deriving its row the first time.
        /// </summary>
        public ulong Mint(SpikeTable table, ulong[] key)
        {
            var text = table.Name + ":" + string.Join(",", key);
            if (mints.TryGetValue(text, out var id))
                return id;

            id = clock.MintId();
            mints[text] = id;
            var row = new ulong[key.Length + 1];
            key.CopyTo(row, 0);
            row[^1] = id;
            Rows.Add((table, row));
            return id;
        }

    }

}
