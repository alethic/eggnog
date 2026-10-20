using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// <see cref="Scope.Serialize"/>: a port of egglog's <c>EGraph::serialize</c> (<c>src/serialize.rs</c>, as at 3.0.0,
    /// whose class ids allow a dash in a sort's name) over a scope's view. Every table but the mirrors and the built-in
    /// contradiction relation, in definition order, gives a node per row, in content order; each argument is an edge to
    /// one node of its class, the class's nodes taken in turn, as egglog spreads its edges; a value is a node of its own
    /// class; and a class whose every node was left out gets a placeholder. Called under the scope's guard; never interns.
    /// </summary>
    /// <remarks>
    /// egglog makes a relation a constructor over a sort of its own that cannot be unioned (<c>desugar_relation</c>,
    /// <c>src/ast/desugar.rs</c>), named <c>@{relation}Sort</c> by its symbol generator; a relation's row here is
    /// likewise a class of its own, of that type, numbered by its row. A set's elements are its node's children in cell
    /// order, as an egglog set's are (a <c>BTreeSet</c> of values); an element that was never interned on its own has no
    /// cell, so it comes after those that have one, in a class of its own numbered per type.
    /// </remarks>
    internal sealed class GraphSerializer
    {

        readonly Scope scope;
        readonly Relations.BaseValues values;
        readonly Dictionary<string, Queue<string>> nodeIds = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> nodeIndex = new(StringComparer.Ordinal);
        readonly List<SerializedNode> nodes = new();
        readonly Dictionary<string, int> classIndex = new(StringComparer.Ordinal);
        readonly List<SerializedClass> classes = new();
        readonly Dictionary<TableHandle, string> relationSorts = new();
        readonly Dictionary<string, int> sortHints = new(StringComparer.Ordinal);
        readonly Dictionary<uint, string> classSorts = new();
        readonly Dictionary<(ColumnType Type, object Value), string> literalClasses = new();
        readonly Dictionary<ColumnType, int> literalCounts = new();

        /// <summary>
        /// Initializes a serialization of a scope.
        /// </summary>
        /// <param name="scope">The scope.</param>
        GraphSerializer(Scope scope)
        {
            this.scope = scope;
            values = scope.Graph.BaseValues;
        }

        /// <summary>
        /// Serializes a scope's view.
        /// </summary>
        /// <param name="scope">The scope, under its guard.</param>
        /// <param name="options">What to include.</param>
        /// <returns>The serialized graph.</returns>
        /// <exception cref="ArgumentException">A root fails the class checks.</exception>
        public static SerializedGraph Serialize(Scope scope, SerializeOptions options) => new GraphSerializer(scope).Run(options);

        /// <summary>
        /// Collects the rows to include, then serializes each: its output's class, its arguments, then its node.
        /// </summary>
        SerializedGraph Run(SerializeOptions options)
        {
            var maxTables = options.MaxTables ?? int.MaxValue;
            var maxRows = options.MaxRowsPerTable ?? int.MaxValue;
            var calls = new List<SerializedCall>();
            var truncated = new List<string>();
            var discarded = new List<string>();
            var unread = new List<TableHandle>();
            var kept = 0;
            foreach (var table in scope.Graph.Tables)
            {
                // The mirrors are hidden, and egglog has no contradiction relation.
                if (table.IsMirror || ReferenceEquals(table, scope.Graph.Contradiction))
                    continue;

                // egglog's symbol generator names the relations' sorts in definition order.
                if (table.IsRelation)
                    relationSorts[table] = RelationSort(table.Name);

                if (kept >= maxTables)
                {
                    discarded.Add(table.Name);
                    unread.Add(table);
                    continue;
                }

                var rows = RowReader.ReadCells(scope, table);
                NoteSorts(table, rows);
                var count = 0;
                foreach (var (cells, subsumed) in rows)
                {
                    if (count >= maxRows)
                    {
                        truncated.Add(table.Name);
                        break;
                    }

                    calls.Add(new SerializedCall(table, cells, subsumed, OutputClass(table, cells, count), $"function-{count}-{table.Name}"));
                    count++;
                }

                if (count != 0)
                    kept++;
            }

            // Each class's nodes, which the edges into the class take in turn.
            foreach (var call in calls)
                if (call.Table.IsRelation || call.Table.Columns[^1].Kind == ColumnKind.Sort)
                {
                    if (nodeIds.TryGetValue(call.ClassId, out var queue) == false)
                        nodeIds.Add(call.ClassId, queue = new Queue<string>());

                    queue.Enqueue(call.NodeId);
                }

            foreach (var call in calls)
            {
                var table = call.Table;
                var keys = table.KeyArray;
                if (table.IsRelation)
                    SerializeClass(call.ClassId, relationSorts[table]);
                else
                    SerializeValue(table.Columns[keys.Length], call.Cells[keys.Length], call.ClassId);

                var children = new string[keys.Length];
                for (var i = 0; i < keys.Length; i++)
                    children[i] = SerializeValue(keys[i], call.Cells[i], ClassOf(keys[i], call.Cells[i]));

                var cost = table is ConstructorHandle constructor ? constructor.Cost : 1;
                AddNode(new SerializedNode(call.NodeId, table.Name, call.ClassId, cost, Array.AsReadOnly(children), call.Subsumed));
            }

            var roots = new string[options.Roots.Count];
            for (var i = 0; i < roots.Length; i++)
                roots[i] = RootClass(options.Roots[i], unread);

            return new SerializedGraph(nodes.ToArray(), classes.ToArray(), roots, truncated.ToArray(), discarded.ToArray());
        }

        /// <summary>
        /// Gets the id of a row's class: its output's, or for a relation's row a class of its own.
        /// </summary>
        string OutputClass(TableHandle table, uint[] cells, int row) =>
            table.IsRelation ? relationSorts[table] + "-" + row.ToString(CultureInfo.InvariantCulture) : ClassOf(table.Columns[^1], cells[^1]);

        /// <summary>
        /// Gets the id of the class of a cell: egglog's <c>value_to_class_id</c>, the type's name and the cell, which for
        /// a class is canonical in the scope.
        /// </summary>
        static string ClassOf(ColumnType type, uint cell) => type.Name + "-" + cell.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Serializes a value as an argument or output: for a class, the next of its nodes (egglog's
        /// <c>serialize_value</c> rotates them); for a value, its node, which it adds.
        /// </summary>
        /// <returns>The id of the node an edge to the value points at.</returns>
        string SerializeValue(ColumnType type, uint cell, string classId)
        {
            if (type.Kind == ColumnKind.Sort)
                return SerializeClass(classId, type.Name);

            var nodeId = "primitive-" + classId;
            if (ValueStore.IsSet(type.ClrType))
            {
                // egglog's set is a BTreeSet of values, so its elements come in cell order; an element with no cell of
                // its own comes after those that have one, in the set's order.
                var element = scope.Graph.Values.TypeOf(type.ClrType.GetGenericArguments()[0]);
                var interned = new List<uint>();
                var literals = new List<object>();
                foreach (var member in (IEnumerable)type.DecodeObject(cell, scope))
                    if (element.TryEncodeExistingObject(member!, out var memberCell))
                        interned.Add(memberCell);
                    else
                        literals.Add(member!);

                interned.Sort();
                var children = new List<string>(interned.Count + literals.Count);
                foreach (var memberCell in interned)
                    children.Add(SerializeValue(element, memberCell, ClassOf(element, memberCell)));

                foreach (var member in literals)
                    children.Add(SerializeLiteral(element, member));

                AddNode(new SerializedNode(nodeId, "set-of", classId, 1, children.AsReadOnly(), false));
            }
            else
            {
                AddNode(new SerializedNode(nodeId, values.Format(type.EngineType.BaseType, new Relations.Value(cell)), classId, 1, Array.Empty<string>(),
                    false));
            }

            AddClass(classId, type.Name);
            return nodeId;
        }

        /// <summary>
        /// Serializes a set's element that has no cell: a node of a class of its own, <c>{type}-~{n}</c>, numbered per
        /// type in the order the elements are first met, one per distinct value; its operator is the text its codec would
        /// print for its cell (<see cref="ColumnType.FormatDebug"/>), as for any other value.
        /// </summary>
        string SerializeLiteral(ColumnType type, object value)
        {
            if (literalClasses.TryGetValue((type, value), out var classId) == false)
            {
                literalCounts.TryGetValue(type, out var count);
                literalCounts[type] = count + 1;
                classId = type.Name + "-~" + count.ToString(CultureInfo.InvariantCulture);
                literalClasses.Add((type, value), classId);
            }

            var nodeId = "primitive-" + classId;
            AddNode(new SerializedNode(nodeId, type.FormatDebug(value), classId, 1, Array.Empty<string>(), false));
            AddClass(classId, type.Name);
            return nodeId;
        }

        /// <summary>
        /// Serializes a class as an argument or output: takes its next node, adding a placeholder when every node of the
        /// class was left out, and records the class's type.
        /// </summary>
        string SerializeClass(string classId, string type)
        {
            if (nodeIds.TryGetValue(classId, out var queue) == false)
            {
                var dummy = "dummy-" + classId;
                AddNode(new SerializedNode(dummy, "[...]", classId, double.PositiveInfinity, Array.Empty<string>(), false));
                nodeIds.Add(classId, queue = new Queue<string>());
                queue.Enqueue(dummy);
            }

            queue.Enqueue(queue.Dequeue());
            AddClass(classId, type);
            return queue.Peek();
        }

        /// <summary>
        /// Adds a node, or replaces the node of its id in place, as egglog's <c>IndexMap::insert</c> does.
        /// </summary>
        void AddNode(SerializedNode node)
        {
            if (nodeIndex.TryGetValue(node.Id, out var index))
            {
                nodes[index] = node;
                return;
            }

            nodeIndex.Add(node.Id, nodes.Count);
            nodes.Add(node);
        }

        /// <summary>
        /// Adds a class, or replaces the class of its id in place.
        /// </summary>
        void AddClass(string id, string type)
        {
            var @class = new SerializedClass(id, type);
            if (classIndex.TryGetValue(id, out var index))
            {
                classes[index] = @class;
                return;
            }

            classIndex.Add(id, classes.Count);
            classes.Add(@class);
        }

        /// <summary>
        /// Names a relation's sort as egglog's symbol generator does: <c>@</c>, the name without dashes, then
        /// <c>Sort</c>, with a count from the second use of the same hint on. egglog reserves <c>@</c>, so its names never
        /// meet a user's; a sort or value type here may hold one, and its name is skipped as a used one.
        /// </summary>
        string RelationSort(string relation)
        {
            var hint = relation.Replace("-", "", StringComparison.Ordinal) + "Sort";
            while (true)
            {
                sortHints.TryGetValue(hint, out var before);
                sortHints[hint] = before + 1;
                var name = "@" + hint + (before == 0 ? "" : before.ToString(CultureInfo.InvariantCulture));
                if (scope.Graph.Values.IsNameTaken(name) == false)
                    return name;
            }
        }

        /// <summary>
        /// Records the sort of every class a table's rows hold, for the roots.
        /// </summary>
        void NoteSorts(TableHandle table, List<(uint[] Cells, bool Subsumed)> rows)
        {
            var columns = table.Columns;
            foreach (var (cells, _) in rows)
                for (var i = 0; i < cells.Length; i++)
                    if (columns[i].Kind == ColumnKind.Sort)
                        classSorts.TryAdd(cells[i], columns[i].Name);
        }

        /// <summary>
        /// Gets the id of a root's class, canonical in the scope; its sort is read from the rows that hold it, those of
        /// the discarded tables too.
        /// </summary>
        /// <exception cref="ArgumentException">The root fails the class checks, or no row holds it.</exception>
        string RootClass(ClassId root, List<TableHandle> unread)
        {
            var cell = scope.Engine.Find(ClassChecks.Require(scope, root, "options"));
            foreach (var table in unread)
            {
                if (classSorts.ContainsKey(cell))
                    break;

                NoteSorts(table, RowReader.ReadCells(scope, table));
            }

            if (classSorts.TryGetValue(cell, out var sort) == false)
                throw new ArgumentException($"Root {root} is in no row of this scope.", "options");

            return sort + "-" + cell.ToString(CultureInfo.InvariantCulture);
        }

    }

}
