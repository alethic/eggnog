using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The sorts and extraction annotations of an <see cref="EGraph"/>'s functions: the part of egglog's frontend
    /// (<c>EGraph.functions</c> with their <c>FunctionDecl</c>s and resolved schemas) that extraction reads
    /// (src/extract.rs:207-317). The bridge e-graph knows only whether a column holds ids or base values; the schema adds
    /// which sort each column has, which functions are constructors, and their costs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declare each function extraction should see with <see cref="Declare"/>. Functions are kept in function id order,
    /// which is the e-graph's declaration order and egglog's iteration order over its functions. Sorts are registered as
    /// declarations mention them; names must be unique, as egglog identifies sorts by name.
    /// </para>
    /// <para>
    /// An <see cref="Extractor"/> reads the schema when it is created; declarations made later do not affect it. Not
    /// thread-safe while declaring; any number of threads may read it otherwise.
    /// </para>
    /// </remarks>
    public sealed class ExtractionSchema
    {

        readonly List<ExtractionFunction?> functions;
        readonly List<Sort> sorts;
        readonly Dictionary<string, Sort> sortsByName;

        /// <summary>
        /// Initializes a schema with no declarations for <paramref name="egraph"/>.
        /// </summary>
        /// <param name="egraph">The e-graph.</param>
        public ExtractionSchema(EGraph egraph)
        {
            ArgumentNullException.ThrowIfNull(egraph);

            EGraph = egraph;
            functions = new List<ExtractionFunction?>();
            sorts = new List<Sort>();
            sortsByName = new Dictionary<string, Sort>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Gets the e-graph the schema describes.
        /// </summary>
        public EGraph EGraph { get; }

        /// <summary>
        /// Gets the sorts the declarations mention, inner sorts of containers included, in the order they were first
        /// mentioned.
        /// </summary>
        public IReadOnlyList<Sort> Sorts => sorts;

        /// <summary>
        /// Declares the sorts and extraction annotations of <paramref name="function"/>.
        /// </summary>
        /// <param name="function">A function of <see cref="EGraph"/>, not yet declared here.</param>
        /// <param name="signature">Its declaration.</param>
        /// <exception cref="ArgumentException">The function is not declared in the e-graph or is already declared here; a
        /// sort does not fit its column, or its name belongs to another sort; the number of input sorts is not the number
        /// of keys; a constructor's output, or a view's last input, is not an e-class sort; or a view's term constructor
        /// is not a function of the e-graph.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The cost is negative.</exception>
        public void Declare(FunctionId function, FunctionSignature signature)
        {
            ArgumentNullException.ThrowIfNull(signature);
            ArgumentNullException.ThrowIfNull(signature.Inputs, nameof(signature));
            ArgumentNullException.ThrowIfNull(signature.Output, nameof(signature));

            var info = EGraph.GetFunctionInfo(function);
            if (IsDeclared(function))
                throw new ArgumentException($"Function {info.Name} is already declared in this schema.", nameof(function));

            var math = info.Math;
            if (signature.Inputs.Count != math.KeyCount)
                throw new ArgumentException(
                    $"{info.Name} takes {math.KeyCount} keys, not {signature.Inputs.Count} input sorts.",
                    nameof(signature));

            if (signature.Cost is < 0)
                throw new ArgumentOutOfRangeException(nameof(signature), signature.Cost, $"The cost of {info.Name} must not be negative.");

            // Validate everything before registering anything, so that a bad declaration leaves the schema unchanged.
            var columns = new Sort[math.FunctionColumns];
            for (var i = 0; i < columns.Length; i++)
            {
                var sort = i < math.KeyCount ? signature.Inputs[i] : signature.Output;
                if (sort is null)
                    throw new ArgumentException($"Column {i} of {info.Name} has no sort.", nameof(signature));

                if (sort.FitsColumn(EGraph, info.Schema[i]) == false)
                    throw new ArgumentException(
                        $"Sort {sort.Name} does not fit column {i} of {info.Name}, of type {info.Schema[i]}.",
                        nameof(signature));

                columns[i] = sort;
            }

            if (signature.IsConstructor && signature.Output.Kind != SortKind.Eq)
                throw new ArgumentException(
                    $"Constructor {info.Name} must produce an e-class sort, not {signature.Output.Name}.",
                    nameof(signature));

            var termName = info.Name;
            if (signature.TermConstructor is { } termConstructor)
            {
                if (math.KeyCount == 0 || signature.Inputs[^1].Kind != SortKind.Eq)
                    throw new ArgumentException(
                        $"View {info.Name} must have its e-class, of an e-class sort, as its last key.",
                        nameof(signature));

                termName = EGraph.GetFunctionName(termConstructor);
            }

            var mentioned = new List<Sort>();
            foreach (var sort in columns)
                CollectSorts(sort, mentioned);

            foreach (var sort in mentioned)
                if (sortsByName.TryGetValue(sort.Name, out var existing) && ReferenceEquals(existing, sort) == false)
                    throw new ArgumentException($"Another sort is already named {sort.Name}.", nameof(signature));

            foreach (var sort in mentioned)
                if (sortsByName.TryAdd(sort.Name, sort))
                    sorts.Add(sort);

            while (functions.Count <= function.Rep)
                functions.Add(null);

            functions[function.Rep] = new ExtractionFunction(function, info, columns, signature, termName);
        }

        /// <summary>
        /// Gets whether <paramref name="function"/> is declared here.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>Whether it is declared.</returns>
        public bool IsDeclared(FunctionId function) => (uint)function.Rep < (uint)functions.Count && functions[function.Rep] is not null;

        /// <summary>
        /// Gets the declared functions, in function id order.
        /// </summary>
        internal IEnumerable<ExtractionFunction> Functions
        {
            get
            {
                foreach (var function in functions)
                    if (function is not null)
                        yield return function;
            }
        }

        /// <summary>
        /// Gets the declaration of <paramref name="function"/>.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>Its resolved declaration.</returns>
        /// <exception cref="ArgumentException">The function is not declared here.</exception>
        internal ExtractionFunction GetFunction(FunctionId function)
        {
            if (IsDeclared(function))
                return functions[function.Rep]!;

            throw new ArgumentException($"Function {function} is not declared in this extraction schema.", nameof(function));
        }

        /// <summary>
        /// Appends <paramref name="sort"/> and, for a container sort, its inner sorts, depth first, skipping sorts already
        /// listed.
        /// </summary>
        static void CollectSorts(Sort sort, List<Sort> mentioned)
        {
            foreach (var listed in mentioned)
                if (ReferenceEquals(listed, sort))
                    return;

            mentioned.Add(sort);
            if (sort is ContainerSort container)
                foreach (var inner in container.InnerSorts)
                    CollectSorts(inner ?? throw new ArgumentException($"Container sort {sort.Name} has a null inner sort."), mentioned);
        }

    }

}
