using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

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
        /// Gets the ids of the root classes (<see cref="SerializeOptions.Roots"/>), in order. As in egglog, a root whose
        /// every row was left out names a class that is not among <see cref="Classes"/>.
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
        /// Gets the graph as the egraph-serialize crate's JSON, byte for byte as its <c>to_json_file</c> writes it with
        /// serde_json's pretty printer: <c>nodes</c> (each with <c>op</c>, <c>children</c>, <c>eclass</c>, <c>cost</c> and
        /// <c>subsumed</c>), <c>root_eclasses</c> and <c>class_data</c> (each with <c>type</c>), indented by two spaces,
        /// lines ended by a line feed. An infinite cost is <c>null</c>. Strings escape only what serde_json escapes: the
        /// quote, the backslash and the control characters below U+0020.
        /// </summary>
        /// <returns>The JSON.</returns>
        public string ToJson()
        {
            var json = new StringBuilder();
            json.Append("{\n  \"nodes\": ");
            if (Nodes.Count == 0)
                json.Append("{}");
            else
            {
                json.Append("{\n");
                for (var i = 0; i < Nodes.Count; i++)
                {
                    var node = Nodes[i];
                    json.Append("    ");
                    Quote(json, node.Id);
                    json.Append(": {\n      \"op\": ");
                    Quote(json, node.Op);
                    json.Append(",\n      \"children\": ");
                    Strings(json, node.Children, "      ");
                    json.Append(",\n      \"eclass\": ");
                    Quote(json, node.Class);
                    json.Append(",\n      \"cost\": ").Append(double.IsFinite(node.Cost) ? Relations.RustFloatDebug.Format(node.Cost) : "null");
                    json.Append(",\n      \"subsumed\": ").Append(node.Subsumed ? "true" : "false");
                    json.Append(i + 1 < Nodes.Count ? "\n    },\n" : "\n    }\n");
                }

                json.Append("  }");
            }

            json.Append(",\n  \"root_eclasses\": ");
            Strings(json, RootClasses, "  ");
            json.Append(",\n  \"class_data\": ");
            if (Classes.Count == 0)
                json.Append("{}");
            else
            {
                json.Append("{\n");
                for (var i = 0; i < Classes.Count; i++)
                {
                    json.Append("    ");
                    Quote(json, Classes[i].Id);
                    json.Append(": {\n      \"type\": ");
                    Quote(json, Classes[i].Type);
                    json.Append(i + 1 < Classes.Count ? "\n    },\n" : "\n    }\n");
                }

                json.Append("  }");
            }

            return json.Append("\n}").ToString();
        }

        /// <summary>
        /// Writes <see cref="ToJson"/>'s JSON to a stream, as UTF-8.
        /// </summary>
        /// <param name="stream">The stream.</param>
        public void WriteJson(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            stream.Write(Encoding.UTF8.GetBytes(ToJson()));
        }

        /// <summary>
        /// Writes an array of strings as serde_json's pretty printer does: <c>[]</c> when empty, else one element a line,
        /// two spaces deeper than <paramref name="indent"/>.
        /// </summary>
        static void Strings(StringBuilder json, IReadOnlyList<string> values, string indent)
        {
            if (values.Count == 0)
            {
                json.Append("[]");
                return;
            }

            json.Append("[\n");
            for (var i = 0; i < values.Count; i++)
            {
                json.Append(indent).Append("  ");
                Quote(json, values[i]);
                json.Append(i + 1 < values.Count ? ",\n" : "\n");
            }

            json.Append(indent).Append(']');
        }

        /// <summary>
        /// Writes a string as serde_json's <c>format_escaped_str</c> does: the quote and the backslash escaped, the
        /// control characters below U+0020 as <c>\b</c>, <c>\t</c>, <c>\n</c>, <c>\f</c>, <c>\r</c> or a lowercase
        /// <c>\u00xx</c>, and every other character as it is.
        /// </summary>
        static void Quote(StringBuilder json, string value)
        {
            json.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        json.Append("\\\"");
                        break;
                    case '\\':
                        json.Append("\\\\");
                        break;
                    case '\b':
                        json.Append("\\b");
                        break;
                    case '\t':
                        json.Append("\\t");
                        break;
                    case '\n':
                        json.Append("\\n");
                        break;
                    case '\f':
                        json.Append("\\f");
                        break;
                    case '\r':
                        json.Append("\\r");
                        break;
                    default:
                        if (c < ' ')
                            json.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                        else
                            json.Append(c);

                        break;
                }
            }

            json.Append('"');
        }
    }

}
