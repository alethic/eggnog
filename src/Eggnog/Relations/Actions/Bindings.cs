using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// A batch of matches for an action, stored struct-of-arrays: egglog's <c>Bindings</c>
    /// (core-relations/src/action/mod.rs:105-280). Each variable owns a column (a segment) of
    /// <see cref="MaxBatchSize"/> cells in one flat array; lane <c>i</c> of variable <c>v</c> is
    /// <c>Data[OffsetOf(v) + i]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout, as egglog's. Segments are appended in the order variables are first mapped; cells of a segment at or
    /// past <see cref="Matches"/> hold <see cref="Value.StaleRep"/>. The first <see cref="Push(DenseIdMap{Variable, Value}, ReadOnlySpan{Variable})"/>
    /// of a batch gives a segment to every variable the join has bound, used by the action or not; later pushes write
    /// only the action's used variables, one strided store each, so the other bound variables hold a value in lane 0
    /// only. Instructions append a segment for each variable they define (<see cref="Insert"/>,
    /// <see cref="AllocateColumn"/>); a variable defined again gets a new segment and its old one is orphaned until
    /// <see cref="Clear"/>.
    /// </para>
    /// <para>
    /// egglog moves a column out (<c>take</c>) and back (<c>replace</c>) to write it while reading others, which Rust's
    /// borrow rules require; here instructions write a column in place through <see cref="Data"/> and
    /// <see cref="OffsetOf"/>, which has the same effect.
    /// </para>
    /// <para>
    /// <see cref="IdCount"/> is egglog's <c>var_offsets.n_ids()</c>: zero until a variable is mapped. The interpreter
    /// reads it to run a rule whose join binds no variable once per batch
    /// (<see cref="ExecutionState.RunInstrs(ReadOnlySpan{Instr}, Bindings)"/>).
    /// </para>
    /// <para>
    /// The arrays are reused across batches: after the first few batches pushing and clearing allocate nothing. Not
    /// thread-safe; each action buffer owns its bindings.
    /// </para>
    /// </remarks>
    internal sealed class Bindings
    {

        /// <summary>
        /// The batch size of an action buffer: egglog's <c>VAR_BATCH_SIZE</c> (core-relations/src/free_join/execute.rs:1807).
        /// </summary>
        public const int DefaultBatchSize = Mask.MaxLanes;

        readonly int maxBatchSize;
        uint[] data;
        int length;
        int[] offsets;
        int idCount;
        int matches;

        /// <summary>
        /// Initializes an empty batch. egglog's <c>Bindings::new</c>.
        /// </summary>
        /// <param name="maxBatchSize">The most matches the batch holds, at most <see cref="Mask.MaxLanes"/>.</param>
        public Bindings(int maxBatchSize = DefaultBatchSize)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBatchSize, Mask.MaxLanes);

            this.maxBatchSize = maxBatchSize;
            data = Array.Empty<uint>();
            offsets = Array.Empty<int>();
        }

        /// <summary>
        /// Gets the most matches the batch holds, which is also the length of every segment.
        /// </summary>
        public int MaxBatchSize => maxBatchSize;

        /// <summary>
        /// Gets or sets the number of matches (lanes) in the batch. The interpreter sets it to one for a rule whose join
        /// binds no variable, as egglog's <c>run_instrs</c> does.
        /// </summary>
        public int Matches
        {
            get => matches;
            set
            {
                if ((uint)value > (uint)maxBatchSize)
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"A batch holds at most {maxBatchSize} matches.");

                matches = value;
            }
        }

        /// <summary>
        /// Gets one more than the largest variable index mapped since the last <see cref="Clear"/>, or zero if none is.
        /// egglog's <c>var_offsets.n_ids()</c>.
        /// </summary>
        public int IdCount => idCount;

        /// <summary>
        /// Gets the flat storage of every segment. Replaced when a new segment does not fit, so read it again after
        /// <see cref="Insert"/>, <see cref="AllocateColumn"/> or a first push.
        /// </summary>
        public uint[] Data => data;

        /// <summary>
        /// Gets the index in <see cref="Data"/> of lane 0 of <paramref name="variable"/>'s segment, or -1 if the
        /// variable is not mapped.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns>The offset, or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int OffsetOf(Variable variable)
        {
            var index = variable.Index;
            return (uint)index < (uint)idCount ? offsets[index] : -1;
        }

        /// <summary>
        /// Gets the offset of <paramref name="variable"/>'s segment, or throws: what indexing egglog's bindings with an
        /// unbound variable does (a panic).
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns>The offset.</returns>
        /// <exception cref="InvalidOperationException">The variable is not mapped.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int RequireOffset(Variable variable)
        {
            var offset = OffsetOf(variable);
            if (offset < 0)
                ThrowUnbound(variable);

            return offset;
        }

        /// <summary>
        /// Gets whether <paramref name="variable"/> is mapped.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns><c>true</c> if mapped.</returns>
        public bool Contains(Variable variable) => OffsetOf(variable) >= 0;

        /// <summary>
        /// Gets the values of <paramref name="variable"/> in the <see cref="Matches"/> lanes. egglog's
        /// <c>Index&lt;Variable&gt;</c>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <exception cref="InvalidOperationException">The variable is not mapped.</exception>
        public ReadOnlySpan<uint> this[Variable variable] => new(data, RequireOffset(variable), matches);

        /// <summary>
        /// Gets the values of <paramref name="variable"/> in the <see cref="Matches"/> lanes, if it is mapped. egglog's
        /// <c>get</c>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <param name="values">The values.</param>
        /// <returns><c>true</c> if mapped.</returns>
        public bool TryGet(Variable variable, out ReadOnlySpan<uint> values)
        {
            var offset = OffsetOf(variable);
            if (offset < 0)
            {
                values = default;
                return false;
            }

            values = new ReadOnlySpan<uint>(data, offset, matches);
            return true;
        }

        /// <summary>
        /// Empties the batch: no matches, no variables. egglog's <c>clear</c>. O(<see cref="IdCount"/>); keeps the
        /// arrays.
        /// </summary>
        public void Clear()
        {
            Array.Fill(offsets, -1, 0, idCount);
            idCount = 0;
            length = 0;
            matches = 0;
        }

        /// <summary>
        /// Maps <paramref name="variable"/> to a new segment holding <paramref name="values"/>, one per lane: egglog's
        /// <c>insert</c>, for columns an action defines. The first variable mapped into an empty batch fixes
        /// <see cref="Matches"/>; later ones must match it.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <param name="values">The values; copied.</param>
        /// <exception cref="ArgumentException">The number of values differs from <see cref="Matches"/>, or exceeds
        /// <see cref="MaxBatchSize"/>.</exception>
        public void Insert(Variable variable, ReadOnlySpan<uint> values)
        {
            if (values.Length > maxBatchSize)
                throw new ArgumentException($"A batch holds at most {maxBatchSize} matches, not {values.Length}.", nameof(values));

            if (idCount == 0)
                matches = values.Length;
            else if (matches != values.Length)
                throw new ArgumentException($"The batch has {matches} matches but {values.Length} values were given.", nameof(values));

            var start = AddSegment(variable);
            values.CopyTo(data.AsSpan(start));
        }

        /// <summary>
        /// Maps <paramref name="variable"/> to a new segment of stale cells and returns its offset, for an instruction
        /// to fill its <see cref="Matches"/> lanes in place: <see cref="Insert"/> without the copy. Call it before
        /// capturing <see cref="Data"/>, which it may replace.
        /// </summary>
        /// <param name="variable">The variable the instruction defines.</param>
        /// <returns>The index of lane 0 of the segment in <see cref="Data"/>.</returns>
        public int AllocateColumn(Variable variable) => AddSegment(variable);

        /// <summary>
        /// Appends one match: egglog's <c>push</c>. The first push of a batch maps every variable present in
        /// <paramref name="map"/>; later pushes write the values of <paramref name="usedVariables"/> only, which must be
        /// the same list each time. O(bound variables) for the first push, O(used variables) afterwards.
        /// </summary>
        /// <remarks>
        /// egglog reads the used variables unchecked (undefined behaviour when one is unbound) and debug-asserts on every
        /// push that they are mapped (core-relations/src/action/mod.rs:214-224). Here every push checks it, in Release
        /// builds too: a used variable with no segment throws instead of writing into another variable's segment. The
        /// check is one compare per used variable.
        /// </remarks>
        /// <param name="map">The join's current bindings.</param>
        /// <param name="usedVariables">The variables the action reads that the join binds (egglog's <c>used_vars</c>).</param>
        /// <exception cref="InvalidOperationException">The batch is full, or a used variable is not bound.</exception>
        public void Push(DenseIdMap<Variable, Value> map, ReadOnlySpan<Variable> usedVariables)
        {
            ArgumentNullException.ThrowIfNull(map);

            if (matches != 0)
            {
                if (matches >= maxBatchSize)
                    ThrowFull();

                var cells = data;
                foreach (var variable in usedVariables)
                {
                    if (map.TryGetValue(variable, out var value) == false)
                        ThrowUnbound(variable);

                    cells[LaterPushOffset(variable)] = value.Rep;
                }
            }
            else
            {
                foreach (var (variable, value) in map)
                    AddSegment(variable, value.Rep);

                CheckUsed(usedVariables);
            }

            matches++;
        }

        /// <summary>
        /// Appends one match from a dense value array: the form of <see cref="Push(DenseIdMap{Variable, Value}, ReadOnlySpan{Variable})"/>
        /// for a join that keeps its bindings in an array indexed by variable. The first push of a batch maps every
        /// variable of <paramref name="boundVariables"/>, in that order; later pushes write the values of
        /// <paramref name="usedVariables"/> only, which must be the same list each time.
        /// </summary>
        /// <param name="values">The join's current bindings: <c>values[v.Index]</c> is the value of a bound variable <c>v</c>.</param>
        /// <param name="boundVariables">The variables the join has bound.</param>
        /// <param name="usedVariables">The variables the action reads that the join binds.</param>
        /// <remarks>
        /// Every push checks that each used variable has a segment, as
        /// <see cref="Push(DenseIdMap{Variable, Value}, ReadOnlySpan{Variable})"/> does. Whether a used variable is bound
        /// in <paramref name="values"/> is the caller's contract: the array has no presence bits.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The batch is full, or a used variable is not mapped.</exception>
        public void Push(ReadOnlySpan<uint> values, ReadOnlySpan<Variable> boundVariables, ReadOnlySpan<Variable> usedVariables)
        {
            if (matches != 0)
            {
                if (matches >= maxBatchSize)
                    ThrowFull();

                var cells = data;
                foreach (var variable in usedVariables)
                    cells[LaterPushOffset(variable)] = values[variable.Index];
            }
            else
            {
                foreach (var variable in boundVariables)
                    AddSegment(variable, values[variable.Index]);

                CheckUsed(usedVariables);
            }

            matches++;
        }

        /// <summary>
        /// Appends one match from a dense value array: the array form of
        /// <see cref="Push(ReadOnlySpan{uint}, ReadOnlySpan{Variable}, ReadOnlySpan{Variable})"/>, for a caller that
        /// holds the join's bindings and variable lists as arrays (<see cref="ActionBuffer.Push"/>, once per match).
        /// </summary>
        /// <param name="values">The join's current bindings, indexed by variable.</param>
        /// <param name="boundVariables">The variables the join has bound.</param>
        /// <param name="usedVariables">The variables the action reads that the join binds.</param>
        public void Push(uint[] values, Variable[] boundVariables, Variable[] usedVariables)
        {
            ArgumentNullException.ThrowIfNull(values);
            ArgumentNullException.ThrowIfNull(boundVariables);
            ArgumentNullException.ThrowIfNull(usedVariables);
            Push(new ReadOnlySpan<uint>(values), new ReadOnlySpan<Variable>(boundVariables), new ReadOnlySpan<Variable>(usedVariables));
        }

        /// <summary>
        /// Formats the batch as egglog's <c>Debug</c> does: each mapped variable with its values, in variable order.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder("{");
            var first = true;
            for (var i = 0; i < idCount; i++)
            {
                if (offsets[i] < 0)
                    continue;

                if (first == false)
                    b.Append(", ");

                b.Append(new Variable(i)).Append(": [");
                for (var lane = 0; lane < matches; lane++)
                {
                    if (lane > 0)
                        b.Append(", ");

                    b.Append(new Value(data[offsets[i] + lane]));
                }

                b.Append(']');
                first = false;
            }

            return b.Append('}').ToString();
        }

        /// <summary>
        /// Appends a segment for <paramref name="variable"/> holding <paramref name="lane0"/> in lane 0 and stale cells
        /// elsewhere: egglog's <c>add_mapping(var, &amp;[val])</c>.
        /// </summary>
        void AddSegment(Variable variable, uint lane0)
        {
            var start = AddSegment(variable);
            data[start] = lane0;
        }

        /// <summary>
        /// Appends a segment of stale cells for <paramref name="variable"/> and maps the variable to it: egglog's
        /// <c>add_mapping</c>, padding included. Amortized O(<see cref="MaxBatchSize"/>).
        /// </summary>
        int AddSegment(Variable variable)
        {
            var index = variable.Index;
            if (index < 0 || variable.IsPlaceholder)
                throw new ArgumentOutOfRangeException(nameof(variable), variable, "Only query variables can be bound.");

            if (index >= offsets.Length)
            {
                var old = offsets.Length;
                Array.Resize(ref offsets, Math.Max(Math.Max(8, old * 2), index + 1));
                Array.Fill(offsets, -1, old, offsets.Length - old);
            }

            if (index >= idCount)
                idCount = index + 1;

            var start = length;
            if (data.Length - start < maxBatchSize)
                Array.Resize(ref data, Math.Max(Math.Max(4 * maxBatchSize, data.Length * 2), start + maxBatchSize));

            data.AsSpan(start, maxBatchSize).Fill(Value.StaleRep);
            length = start + maxBatchSize;
            offsets[index] = start;
            return start;
        }

        /// <summary>
        /// Gets the index in <see cref="Data"/> of lane <see cref="Matches"/> of <paramref name="variable"/>'s segment,
        /// for a push after the first, or throws if the variable has no segment. egglog only debug-asserts this
        /// (core-relations/src/action/mod.rs:214-224); without the check an unmapped variable's offset of -1 would write
        /// into lane <c>Matches - 1</c> of the segment at offset 0, overwriting another variable's match.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int LaterPushOffset(Variable variable)
        {
            var offset = OffsetOf(variable);
            if (offset < 0)
                ThrowUnbound(variable);

            return offset + matches;
        }

        /// <summary>
        /// Throws unless every variable of <paramref name="usedVariables"/> is mapped.
        /// </summary>
        void CheckUsed(ReadOnlySpan<Variable> usedVariables)
        {
            foreach (var variable in usedVariables)
                if (OffsetOf(variable) < 0)
                    ThrowUnbound(variable);

            Debug.Assert(length <= data.Length, "segments past the end of the data");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowUnbound(Variable variable)
        {
            throw new InvalidOperationException($"Variable {variable} is not bound in this batch.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowFull()
        {
            throw new InvalidOperationException($"The batch already holds {maxBatchSize} matches.");
        }

    }

}
