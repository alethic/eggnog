using System;
using System.Collections.Concurrent;

namespace Eggnog
{

    /// <summary>
    /// Reads and writes of tables from host code an engine call runs, a rule callback's (<see cref="RuleCallbackContext"/>)
    /// or a primitive's (<see cref="EnginePrimitiveTables"/>): egglog's <c>WriteState</c> and <c>ReadState</c> over the
    /// call's <c>ExecutionState</c>, so that writes are staged with the iteration's other writes. Values are of their
    /// columns' types, a class a sort value usable in the scope the call runs in. A write is checked as a head's is, when
    /// it is made: a function whose merge is not a lattice is written only at the root, a deletion only where the scope may
    /// delete, a subsumption only of a subsumable table, and no analysis table and no scoped view is written. The first
    /// refusal is recorded, so that it fails the call's rule even when the host code caught it. Valid only during the call.
    /// </summary>
    internal sealed class EngineTables
    {

        readonly Relations.ExecutionState state;
        readonly ConcurrentDictionary<Relations.FunctionId, Relations.TableAction> actions;
        readonly string who;
        readonly bool canonical;
        Exception? refusal;
        bool done;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scope">The scope the call runs in.</param>
        /// <param name="state">The engine's state for the call.</param>
        /// <param name="actions">The table actions of the call site, made once per table.</param>
        /// <param name="who">What calls, for messages: "A rule callback", "A primitive".</param>
        /// <param name="canonical">Whether classes are read and written canonical through the state; not for a merge's
        /// primitive, which takes them as they are, as egglog's does.</param>
        public EngineTables(Scope scope, Relations.ExecutionState state, ConcurrentDictionary<Relations.FunctionId, Relations.TableAction> actions, string who, bool canonical = true)
        {
            Scope = scope;
            this.state = state;
            this.actions = actions;
            this.who = who;
            this.canonical = canonical;
        }

        /// <summary>
        /// Gets the scope the call runs in.
        /// </summary>
        public Scope Scope { get; }

        /// <summary>
        /// Gets the engine's state, checking that the call has not returned.
        /// </summary>
        public Relations.ExecutionState State
        {
            get
            {
                if (done)
                    throw Refuse(new InvalidOperationException($"{who}'s tables are valid only during the call."));

                return state;
            }
        }

        /// <summary>
        /// Gets the first write or read refused, which fails the call's rule whether or not the host code caught it.
        /// </summary>
        public Exception? Refusal => refusal;

        /// <summary>
        /// Ends the call: the tables answer nothing after.
        /// </summary>
        public void End() => done = true;

        /// <summary>
        /// Gets a class cell canonical through the state, or as it is for a merge's primitive.
        /// </summary>
        /// <param name="cell">The class cell.</param>
        /// <returns>The cell to use.</returns>
        uint Find(uint cell) => canonical ? State.Canonicalize(cell) : cell;

        /// <summary>
        /// Records a refusal, the first one only, and returns it to throw.
        /// </summary>
        /// <param name="exception">The refusal.</param>
        /// <returns>The refusal.</returns>
        public Exception Refuse(Exception exception)
        {
            refusal ??= exception;
            return exception;
        }

        /// <summary>
        /// Gets the engine's table action of a table, made once per table and call site.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The action.</returns>
        public Relations.TableAction Action(TableHandle table) =>
            actions.GetOrAdd(table.Function, static (function, engine) => new Relations.TableAction(engine, function), Scope.Graph.Engine);

        /// <inheritdoc cref="ITableWriter.Insert"/>
        public void Insert(RelationHandle relation, ReadOnlySpan<object> keys)
        {
            var row = Row(relation, keys, 1, writes: true);
            row[^1] = Scope.Graph.Unit.Unit;
            Action(relation).Insert(State, row);
        }

        /// <inheritdoc cref="ITableWriter.Set"/>
        public void Set(FunctionHandle function, ReadOnlySpan<object> keys, object value)
        {
            if (value is null)
                throw Refuse(new ArgumentNullException(nameof(value)));

            var row = Row(function, keys, 1, writes: true);
            row[^1] = Encode(function.Columns[^1], value, nameof(value));
            Action(function).Insert(State, row);
        }

        /// <inheritdoc cref="ITableWriter.Make"/>
        public TSort Make<TSort>(ConstructorHandle constructor, ReadOnlySpan<object> keys) where TSort : struct, ISort<TSort>
        {
            var key = Row(constructor, keys, 0, writes: true);
            if (constructor.Output!.ClrType != typeof(TSort))
                throw Refuse(new ArgumentException(
                    $"Constructor '{constructor.Name}' makes {constructor.Output.Name}, not {typeof(TSort).Name}.", nameof(constructor)));

            Action(constructor).TryLookupOrInsert(State, key, out var cell);
            return (TSort)constructor.Output.DecodeObject(cell, Scope);
        }

        /// <inheritdoc cref="ITableWriter.Union"/>
        public void Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>
        {
            var type = Scope.Graph.Values.TypeOf<TSort>();
            UnionCells(Encode(type, a, nameof(a)), Encode(type, b, nameof(b)));
        }

        /// <summary>
        /// Unions two class cells, canonical or stale: egglog's <c>union</c>, staged through the call's state.
        /// </summary>
        /// <param name="a">One class's cell.</param>
        /// <param name="b">The other's.</param>
        public void UnionCells(uint a, uint b) => new Relations.UnionAction(Scope.Graph.Engine).Union(State, a, b);

        /// <inheritdoc cref="ITableWriter.Remove"/>
        public void Remove(TableHandle table, ReadOnlySpan<object> keys)
        {
            var key = Row(table, keys, 0, writes: true);
            if (Scope.Engine.RetractionRefusal is { } why)
                throw Refuse(new InvalidOperationException($"'{table.Name}' cannot lose a row in '{Scope.Path}': {why}."));

            Action(table).Remove(State, key);
            Scope.Runtime.NoteCallbackDeletion(table);
        }

        /// <inheritdoc cref="ITableWriter.Subsume"/>
        public void Subsume(TableHandle table, ReadOnlySpan<object> keys)
        {
            var key = Row(table, keys, 0, writes: true);
            if (table.Subsumable == false)
                throw Refuse(new ArgumentException($"'{table.Name}' is not subsumable.", nameof(table)));

            // egglog's subsume looks the row up and panics without one, which the call cannot catch.
            var action = Action(table);
            if (action.TryLookup(State, key, out _) == false)
                throw Refuse(new InvalidOperationException($"'{table.Name}' holds no row for the keys, which a subsumption needs, as egglog's subsume does."));

            action.Subsume(State, key);
        }

        /// <inheritdoc cref="ITableReader.TryLookup"/>
        public bool TryLookup<T>(TableHandle table, ReadOnlySpan<object> keys, out T value)
        {
            value = default!;
            var found = TryRow(table, keys, 0, writes: false, out var key);
            if (table.IsRelation)
                throw Refuse(new ArgumentException(
                    $"'{table.Name}' is a relation, which has no value to read; use Contains.", nameof(table)));

            if (table.Columns[^1].ClrType != typeof(T))
                throw Refuse(new ArgumentException(
                    $"'{table.Name}' outputs {table.Columns[^1].Name}, not {typeof(T).Name}.", nameof(table)));

            if (found == false || Action(table).TryLookup(State, key, out var cell) == false)
                return false;

            var output = table.Columns[^1];
            value = (T)output.DecodeObject(output.InvolvesClasses ? Find(cell) : cell, Scope);
            return true;
        }

        /// <summary>
        /// Reads the output of a function's row at keys' cells, the class canonical through the state (<see cref="Find"/>).
        /// </summary>
        /// <exception cref="ArgumentException">The function belongs to another graph or is a scoped view.</exception>
        public bool TryLookupCell(FunctionHandle table, ReadOnlySpan<uint> keys, out uint value)
        {
            CheckTable(table, writes: false);
            if (Action(table).TryLookup(State, keys, out value) == false)
                return false;

            if (table.Columns[^1].InvolvesClasses)
                value = Find(value);

            return true;
        }

        /// <summary>
        /// Reads the output of a function's row at keys, as a value.
        /// </summary>
        /// <exception cref="ArgumentException">The function belongs to another graph or is a scoped view, or a key is not
        /// of its column's type or not usable here.</exception>
        public bool TryLookupObject(FunctionHandle table, ReadOnlySpan<object> keys, out object? value)
        {
            value = null;
            if (TryRow(table, keys, 0, writes: false, out var key) == false || Action(table).TryLookup(State, key, out var cell) == false)
                return false;

            var output = table.Columns[^1];
            value = output.DecodeObject(output.InvolvesClasses ? Find(cell) : cell, Scope);
            return true;
        }

        /// <summary>
        /// Gets or makes the class of a constructor's term at keys' cells: egglog's <c>lookup_or_insert</c>.
        /// </summary>
        /// <exception cref="ArgumentException">The constructor belongs to another graph, or may not be written
        /// here.</exception>
        public uint MakeCell(ConstructorHandle constructor, ReadOnlySpan<uint> keys)
        {
            CheckTable(constructor, writes: true);
            Action(constructor).TryLookupOrInsert(State, keys, out var cell);
            return cell;
        }

        /// <inheritdoc cref="ITableReader.Contains"/>
        public bool Contains(RelationHandle relation, ReadOnlySpan<object> keys) =>
            TryRow(relation, keys, 0, writes: false, out var row) && Action(relation).TryLookup(State, row, out _);

        /// <inheritdoc cref="ITableReader.TableSize"/>
        public long TableSize(TableHandle table)
        {
            CheckTable(table, writes: false);
            return Action(table).RowCount(State);
        }

        /// <summary>
        /// Encodes the keys of a row of <paramref name="table"/>, with <paramref name="extra"/> free cells after them,
        /// checking that the call may use the table.
        /// </summary>
        /// <exception cref="ArgumentException">The table belongs to another graph, is a scoped view, or may not be written
        /// here, or a value is not of its column's type or not usable here.</exception>
        public uint[] Row(TableHandle table, ReadOnlySpan<object> keys, int extra, bool writes)
        {
            if (TryRow(table, keys, extra, writes, out var row) == false)
                throw new InvalidOperationException($"A key of '{table.Name}' has no cell, and a write makes one.");

            return row;
        }

        /// <summary>
        /// Encodes the keys of a row of <paramref name="table"/> as <see cref="Row"/> does, except that a read finds no
        /// row for a key that is a container of classes no write made, which it does not intern.
        /// </summary>
        /// <returns>Whether every key has a cell; always <c>true</c> for a write.</returns>
        /// <exception cref="ArgumentException">As for <see cref="Row"/>.</exception>
        public bool TryRow(TableHandle table, ReadOnlySpan<object> keys, int extra, bool writes, out uint[] row)
        {
            CheckTable(table, writes);
            var columns = table.KeyArray;
            if (keys.Length != columns.Length)
                throw Refuse(new ArgumentException($"'{table.Name}' takes {columns.Length} keys, not {keys.Length}.", nameof(keys)));

            row = new uint[columns.Length + extra];
            for (var i = 0; i < columns.Length; i++)
            {
                var key = keys[i] ?? throw Refuse(new ArgumentNullException(nameof(keys), $"Key {i} is null."));
                if (writes == false && columns[i].HoldsClasses)
                {
                    if (TryLookUp(columns[i], key, nameof(keys), out row[i]) == false)
                        return false;

                    continue;
                }

                row[i] = Encode(columns[i], key, nameof(keys));
            }

            return true;
        }

        /// <summary>
        /// Gets the cell of a container of classes a read names, its classes usable and canonical (<see cref="Find"/>),
        /// without interning it.
        /// </summary>
        /// <returns>Whether a write made it.</returns>
        /// <exception cref="ArgumentException">The value is not of the column's type, or holds a class not usable
        /// here.</exception>
        bool TryLookUp(ColumnType type, object value, string parameter, out uint cell)
        {
            if (type.ClrType.IsInstanceOfType(value) == false)
                throw Refuse(new ArgumentException($"A {ValueStore.DisplayName(value.GetType())} is not a {type.Name}.", parameter));

            try
            {
                return type.TryEncodeCanonicalObject(value, Scope, Find, out cell);
            }
            catch (ArgumentException refused)
            {
                throw Refuse(refused);
            }
        }

        /// <summary>
        /// Checks that the call may read, or with <paramref name="writes"/> write, <paramref name="table"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The table belongs to another graph, is a scoped view, or may not be written
        /// here.</exception>
        public void CheckTable(TableHandle table, bool writes)
        {
            ArgumentNullException.ThrowIfNull(table);
            _ = State;
            if (ReferenceEquals(table.Graph, Scope.Graph) == false || table.Index < 0)
                throw Refuse(new ArgumentException($"Table '{table.Name}' belongs to another graph.", nameof(table)));

            if (table.ReadScope is not null)
                throw Refuse(new ArgumentException(
                    $"'{table.Name}@{table.ReadScope.Name}' is a scoped view, which {Lower(who)} does not use.", nameof(table)));

            if (writes)
            {
                if (table.Role == TableRole.Analysis)
                    throw Refuse(new ArgumentException(
                        $"{who} does not write analysis table '{table.Name}': analyses depend only on terms, which {Lower(who)} "
                        + "cannot show.",
                        nameof(table)));

                if (Scope.IsRoot == false && table is FunctionHandle { Merge: not null })
                    throw Refuse(new ArgumentException(
                        $"'{table.Name}' merges with a function, not a lattice, and only the root writes it.", nameof(table)));
            }
        }

        /// <summary>
        /// Encodes a value of a column, a class checked usable in the scope.
        /// </summary>
        /// <exception cref="ArgumentException">The value is not of the column's type, or is a class not usable here.</exception>
        public uint Encode(ColumnType type, object value, string parameter)
        {
            if (type.ClrType.IsInstanceOfType(value) == false)
                throw Refuse(new ArgumentException($"A {ValueStore.DisplayName(value.GetType())} is not a {type.Name}.", parameter));

            // Canonical through the state, not the scope, whose find enters a thread domain a parallel run's worker
            // does not hold: a row of a stale id, such as a class the host kept, would join as no class until a rebuild.
            if (type.Kind == ColumnKind.Sort)
            {
                uint cell;
                try
                {
                    cell = ClassChecks.Require(Scope, type.ClassOf(value), parameter);
                }
                catch (ArgumentException refused)
                {
                    throw Refuse(refused);
                }

                return Find(cell);
            }

            try
            {
                // A container of classes: its classes usable and canonical (Find), as a class is, and the
                // container interned at a writable root or child, or in a frozen scope the one it sees.
                if (type.HoldsClasses)
                    return type.EncodeCanonicalObject(value, Scope, Find, null);

                return type.EncodeObject(value);
            }
            catch (Exception refused) when (refused is ArgumentException or UninternedContainerException)
            {
                throw Refuse(refused);
            }
        }

        /// <summary>
        /// Lowercases the first letter of a subject for the middle of a sentence.
        /// </summary>
        static string Lower(string subject) => char.ToLowerInvariant(subject[0]) + subject[1..];

    }

}
