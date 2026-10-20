using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Eggnog
{

    /// <summary>
    /// A scope's e-graph as egglog serializes one (<c>EGraph::serialize</c>): the graph of the egraph-serialize crate,
    /// which egglog's visualizer reads, with what was left out. <see cref="ToJson"/> and <see cref="WriteJson"/> write
    /// that crate's JSON.
    /// </summary>
    public sealed class SerializedGraph
    {

        readonly Dictionary<string, SerializedNode> nodesById;
        readonly Dictionary<string, SerializedClass> classesById;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="nodes">The nodes, in order.</param>
        /// <param name="classes">The classes, in order.</param>
        /// <param name="roots">The root classes' ids.</param>
        /// <param name="truncated">The tables with rows left out.</param>
        /// <param name="discarded">The tables left out.</param>
        internal SerializedGraph(SerializedNode[] nodes, SerializedClass[] classes, string[] roots, string[] truncated, string[] discarded)
        {
            Nodes = Array.AsReadOnly(nodes);
            Classes = Array.AsReadOnly(classes);
            RootClasses = Array.AsReadOnly(roots);
            TruncatedTables = Array.AsReadOnly(truncated);
            DiscardedTables = Array.AsReadOnly(discarded);
            nodesById = new Dictionary<string, SerializedNode>(nodes.Length, StringComparer.Ordinal);
            foreach (var node in nodes)
                nodesById.Add(node.Id, node);

            classesById = new Dictionary<string, SerializedClass>(classes.Length, StringComparer.Ordinal);
            foreach (var @class in classes)
                classesById.Add(@class.Id, @class);
        }

        /// <summary>
        /// Gets the nodes, in egglog's order: each row's argument values, then the row; its own value first, for a
        /// function's row.
        /// </summary>
        public IReadOnlyList<SerializedNode> Nodes { get; }

        /// <summary>
        /// Gets the classes, in the order they were first reached.
        /// </summary>
        public IReadOnlyList<SerializedClass> Classes { get; }

        /// <summary>
        /// Gets the ids of the root classes (<see cref="SerializeOptions.Roots"/>), in order.
        /// </summary>
        public IReadOnlyList<string> RootClasses { get; }

        /// <summary>
        /// Gets the tables with more rows than <see cref="SerializeOptions.MaxRowsPerTable"/>, so that not every row is
        /// included.
        /// </summary>
        public IReadOnlyList<string> TruncatedTables { get; }

        /// <summary>
        /// Gets the tables left out after <see cref="SerializeOptions.MaxTables"/> tables with rows.
        /// </summary>
        public IReadOnlyList<string> DiscardedTables { get; }

        /// <summary>
        /// Gets whether nothing was left out: no table was truncated or discarded.
        /// </summary>
        public bool IsComplete => TruncatedTables.Count == 0 && DiscardedTables.Count == 0;

        /// <summary>
        /// Gets what was left out, as egglog words it: <c>Omitted: a, b</c> and <c>Truncated: c</c>, one line each; empty
        /// when <see cref="IsComplete"/>.
        /// </summary>
        public string OmittedDescription
        {
            get
            {
                var text = new StringBuilder();
                if (DiscardedTables.Count > 0)
                    text.Append("Omitted: ").Append(string.Join(", ", DiscardedTables)).Append('\n');

                if (TruncatedTables.Count > 0)
                    text.Append("Truncated: ").Append(string.Join(", ", TruncatedTables)).Append('\n');

                return text.ToString();
            }
        }

        /// <summary>
        /// Gets a node by its id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="node">The node, when there is one.</param>
        /// <returns>Whether there is one.</returns>
        public bool TryGetNode(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SerializedNode? node) =>
            nodesById.TryGetValue(id, out node);

        /// <summary>
        /// Gets a class by its id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="class">The class, when there is one.</param>
        /// <returns>Whether there is one.</returns>
        public bool TryGetClass(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SerializedClass? @class) =>
            classesById.TryGetValue(id, out @class);

        /// <summary>
        /// Gets the graph as the egraph-serialize crate's JSON, indented as its <c>to_json_file</c> writes it:
        /// <c>nodes</c> (each with <c>op</c>, <c>children</c>, <c>eclass</c>, <c>cost</c> and <c>subsumed</c>),
        /// <c>root_eclasses</c> and <c>class_data</c> (each with <c>type</c>). An infinite cost is <c>null</c>, as serde
        /// writes it.
        /// </summary>
        /// <returns>The JSON.</returns>
        public string ToJson() => Encoding.UTF8.GetString(Json());

        /// <summary>
        /// Writes <see cref="ToJson"/>'s JSON to a stream, as UTF-8.
        /// </summary>
        /// <param name="stream">The stream.</param>
        public void WriteJson(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            stream.Write(Json());
        }

        /// <summary>
        /// Writes the JSON as UTF-8, with <c>\n</c> line ends, as serde writes them, whatever the platform's: the writer
        /// ends lines with the platform's, and a string in JSON holds no raw carriage return, so every one is a line end's.
        /// </summary>
        byte[] Json()
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                Write(json);

            var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
            const byte carriageReturn = 0x0D;
            var result = new byte[bytes.Length - bytes.Count(carriageReturn)];
            var at = 0;
            foreach (var b in bytes)
                if (b != carriageReturn)
                    result[at++] = b;

            return result;
        }

        /// <summary>
        /// Writes the graph's JSON.
        /// </summary>
        void Write(Utf8JsonWriter json)
        {
            json.WriteStartObject();
            json.WriteStartObject("nodes");
            foreach (var node in Nodes)
            {
                json.WriteStartObject(node.Id);
                json.WriteString("op", node.Op);
                json.WriteStartArray("children");
                foreach (var child in node.Children)
                    json.WriteStringValue(child);

                json.WriteEndArray();
                json.WriteString("eclass", node.Class);
                json.WritePropertyName("cost");
                if (double.IsFinite(node.Cost))
                    json.WriteRawValue(Relations.RustFloatDebug.Format(node.Cost));
                else
                    json.WriteNullValue();

                json.WriteBoolean("subsumed", node.Subsumed);
                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteStartArray("root_eclasses");
            foreach (var root in RootClasses)
                json.WriteStringValue(root);

            json.WriteEndArray();
            json.WriteStartObject("class_data");
            foreach (var @class in Classes)
            {
                json.WriteStartObject(@class.Id);
                json.WriteString("type", @class.Type);
                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

    }

}
