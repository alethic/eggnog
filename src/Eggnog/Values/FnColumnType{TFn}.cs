using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// The column type of a function value (<see cref="UnstableFn{T1, TResult}"/> and its arities): egglog's
    /// <c>UnstableFn</c> sort (src/sort/fn.rs). A value's cell is the id of its <see cref="Relations.FunctionContainer"/>,
    /// interned in the graph's container environment with an id from the graph's id counter, as egglog's are; the engine
    /// column is an id column. A value's partially applied arguments are of its target's types, not the sort's, so whether
    /// a value holds classes is the value's: each argument of an e-class or eq-container sort is rebuilt against the
    /// union-find, as egglog's <c>rebuild_contents</c> rebuilds it, and a value that holds classes is interned only at the
    /// root (decisions row 9). The type itself counts as one that holds classes
    /// (<see cref="ColumnType.HoldsClasses"/>), which every value of it may.
    /// </summary>
    /// <typeparam name="TFn">The value's CLR type.</typeparam>
    internal sealed class FnColumnType<TFn> : ColumnType<TFn>, IContainerColumnType, IFnColumnType
        where TFn : struct, IFnValue
    {

        readonly ColumnType[] inputs;
        readonly ColumnType output;
        readonly Func<FnValue, TFn> wrap;
        readonly Relations.ContainerEnv<Relations.FunctionContainer> env;
        readonly Relations.CounterSet counters;
        readonly FnTargets targets;
        readonly Relations.FunctionSort sort;
        readonly object sync = new();
        readonly System.Collections.Concurrent.ConcurrentDictionary<object, FnTarget> resolved = new(ReferenceEqualityComparer.Instance);
        ColumnType[] partialTypes = [];

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The type's name.</param>
        /// <param name="inputs">The column types of the inputs.</param>
        /// <param name="output">The column type of the output.</param>
        /// <param name="wrap">Makes a value of a content.</param>
        /// <param name="env">The graph's function value environment.</param>
        /// <param name="counters">The graph's counters, from which the environment mints ids.</param>
        /// <param name="targets">The graph's function values' targets.</param>
        /// <param name="stringType">The base value type of strings.</param>
        public FnColumnType(
            string name,
            ColumnType[] inputs,
            ColumnType output,
            Func<FnValue, TFn> wrap,
            Relations.ContainerEnv<Relations.FunctionContainer> env,
            Relations.CounterSet counters,
            FnTargets targets,
            Relations.BaseValueTypeId stringType)
            : base(name, ColumnKind.Value)
        {
            this.inputs = inputs;
            this.output = output;
            this.wrap = wrap;
            this.env = env;
            this.counters = counters;
            this.targets = targets;
            sort = new Relations.FunctionSort(name, SortsOf, stringType);
        }

        /// <summary>
        /// Gets the column types of the inputs.
        /// </summary>
        internal ColumnType[] Inputs => inputs;

        /// <summary>
        /// Gets the column type of the output.
        /// </summary>
        public ColumnType Output => output;

        /// <inheritdoc />
        /// <remarks>Every value may hold classes in its partially applied arguments, whatever the sort's inputs.</remarks>
        internal override bool HoldsClasses => true;

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => Relations.ColumnTy.Id;

        /// <inheritdoc />
        /// <remarks>The types of the partially applied arguments of the values made so far, as egglog's
        /// <c>inner_sorts</c>.</remarks>
        public IReadOnlyList<ColumnType> ElementTypes => Volatile.Read(ref partialTypes);

        /// <inheritdoc />
        public Relations.ContainerEnvBase Environment => env;

        /// <inheritdoc />
        internal override bool IsOrdered => true;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <summary>
        /// Resolves a value's target for the graph and the type, and notes its arguments' types.
        /// </summary>
        /// <param name="target">The table or the primitive.</param>
        /// <returns>The target.</returns>
        /// <exception cref="ArgumentException">The target's signature is not this type's.</exception>
        public FnTarget Resolve(object target)
        {
            var resolved = targets.Resolve(target, inputs, output);
            NoteTypes(resolved);
            return resolved;
        }

        /// <summary>
        /// Encodes a function value: its arguments, then the container, interned without an execution state from the
        /// graph's id counter, as egglog's <c>unstable-fn</c> registers its container. Thread-safe, from any scope.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="ArgumentException">The value is a default one, or its function's signature is not this
        /// type's.</exception>
        /// <exception cref="InvalidOperationException">The value holds classes, which encode in a scope
        /// (<see cref="EncodeIn"/>).</exception>
        internal override uint Encode(TFn value)
        {
            var (content, target) = Content(value);
            if (MaskOf(content, target) != 0)
                throw ContainerClasses.NeedScope(Name);

            var arguments = content.ArgumentArray;
            var cells = new uint[arguments.Length];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = target.Partial[i].EncodeObject(arguments[i]);

            return env.GetOrInsertShared(new Relations.FunctionContainer(target.Id, 0, cells), counters);
        }

        /// <summary>
        /// Encodes a function value a primitive returned in <paramref name="scope"/>: for one that holds classes, its
        /// classes usable there and the container interned at a writable root, or elsewhere the one the scope sees (its
        /// container view, <see cref="Scope.ContainerView"/>); else as <see cref="Encode"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="scope">The scope the call runs in.</param>
        /// <returns>The cell.</returns>
        internal override uint EncodeIn(TFn value, Scope? scope)
        {
            var (content, target) = Content(value);
            var mask = MaskOf(content, target);
            if (mask == 0)
                return Encode(value);

            var arguments = content.ArgumentArray;
            var cells = new uint[arguments.Length];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = target.Partial[i].EncodeInObject(arguments[i], scope);

            return ContainerClasses.Intern(env, new Relations.FunctionContainer(target.Id, mask, cells), counters, Name, scope);
        }

        /// <inheritdoc />
        internal override uint EncodeCanonical(TFn value, Scope scope, Func<uint, uint> find, ScopeOrigin? openBatch)
        {
            var (content, target) = Content(value);
            var mask = MaskOf(content, target);
            if (mask == 0)
                return Encode(value);

            var arguments = content.ArgumentArray;
            var cells = new uint[arguments.Length];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = target.Partial[i].EncodeCanonicalObject(arguments[i], scope, find, openBatch);

            return find(ContainerClasses.Intern(env, new Relations.FunctionContainer(target.Id, mask, cells), counters, Name, scope));
        }

        /// <inheritdoc />
        internal override TFn Canonical(TFn value, Scope scope, Func<uint, uint> find)
        {
            var (content, target) = Content(value);
            if (MaskOf(content, target) == 0)
                return value;

            var arguments = content.ArgumentArray;
            var canonical = new object[arguments.Length];
            for (var i = 0; i < canonical.Length; i++)
                canonical[i] = target.Partial[i].CanonicalObject(arguments[i], scope, find);

            return wrap(FnValue.Of(content.Target, canonical));
        }

        /// <inheritdoc />
        internal override bool TryEncodeExisting(TFn value, Relations.ContainerView view, out uint cell)
        {
            var (content, target) = Content(value);
            cell = 0;
            var arguments = content.ArgumentArray;
            var cells = new uint[arguments.Length];
            for (var i = 0; i < cells.Length; i++)
                if (target.Partial[i].TryEncodeExistingObject(arguments[i], view, out cells[i]) == false)
                    return false;

            return view.TryGetId(env, new Relations.FunctionContainer(target.Id, MaskOf(content, target), cells), out cell);
        }

        /// <inheritdoc />
        internal override bool TryEncodeCanonical(TFn value, Scope scope, Func<uint, uint> find, out uint cell)
        {
            var (content, target) = Content(value);
            var mask = MaskOf(content, target);
            if (mask == 0)
                return TryEncodeExisting(value, scope.ContainerView, out cell);

            cell = 0;
            var arguments = content.ArgumentArray;
            var cells = new uint[arguments.Length];
            for (var i = 0; i < cells.Length; i++)
                if (target.Partial[i].TryEncodeCanonicalObject(arguments[i], scope, find, out cells[i]) == false)
                    return false;

            if (scope.ContainerView.TryGetId(env, new Relations.FunctionContainer(target.Id, mask, cells), out var id) == false)
                return false;

            cell = find(id);
            return true;
        }

        /// <summary>
        /// Decodes a function value's cell, read through the scope's container view. Takes one lock of a container
        /// environment, and more for nested containers.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="scope">The scope serving the call.</param>
        /// <returns>The value.</returns>
        /// <exception cref="InvalidOperationException">No function value has the cell.</exception>
        internal override TFn Decode(uint cell, Scope? scope)
        {
            var (fn, target) = Read(cell, Scope.ViewOf(scope));
            var cells = fn.Arguments;
            var arguments = new object[cells.Length];
            for (var i = 0; i < arguments.Length; i++)
                arguments[i] = target.Partial[i].DecodeObject(cells[i], scope);

            return wrap(FnValue.Of(target.Target, arguments));
        }

        /// <summary>
        /// Decodes the container of a cell: the target and the arguments' cells.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <returns>The target and the arguments' cells.</returns>
        /// <exception cref="InvalidOperationException">No function value has the cell.</exception>
        public (FnTarget Target, uint[] Arguments) DecodeCells(uint cell, Relations.ContainerView view)
        {
            var (fn, target) = Read(cell, view);
            return (target, fn.Arguments.ToArray());
        }

        /// <inheritdoc />
        public bool HoldsClassesIn(object value)
        {
            var (content, target) = Content((TFn)value);
            return MaskOf(content, target) != 0;
        }

        /// <summary>
        /// Formats a function value as egglog reconstructs one: <c>(unstable-fn "name" args)</c> (fn.rs:225-238).
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The literal.</returns>
        internal override string FormatValue(TFn value)
        {
            var (content, target) = Content(value);
            var text = "(unstable-fn " + Relations.LiteralText.String(target.Name);
            var arguments = content.ArgumentArray;
            for (var i = 0; i < arguments.Length; i++)
                text += " " + target.Partial[i].FormatObject(arguments[i]);

            return text + ")";
        }

        /// <inheritdoc />
        internal override string DebugValue(TFn value) => FormatValue(value);

        /// <summary>
        /// Formats a function value's cell as <see cref="FormatValue"/> formats the value, through its arguments' cells, so
        /// that a value of classes formats without a scope; a cell no function value has in <paramref name="view"/> as
        /// <c>#17</c>.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <returns>The literal.</returns>
        internal override string FormatCell(uint cell, Relations.ContainerView view)
        {
            if (view.TryGetContainer(env, cell, out var fn) == false)
                return FormatUnresolvedCell(cell);

            var target = targets[fn.Target];
            var text = "(unstable-fn " + Relations.LiteralText.String(target.Name);
            var cells = fn.Arguments;
            for (var i = 0; i < cells.Length; i++)
                text += " " + target.Partial[i].FormatCell(cells[i], view);

            return text + ")";
        }

        /// <summary>
        /// Compares two function values: by name, by code point, then by target, then argument by argument. Never interns.
        /// </summary>
        /// <param name="x">One value.</param>
        /// <param name="y">The other value.</param>
        /// <returns>The order.</returns>
        internal override int CompareValues(TFn x, TFn y)
        {
            var (left, leftTarget) = Content(x);
            var (right, rightTarget) = Content(y);
            var order = CompareTargets(leftTarget, rightTarget);
            var a = left.ArgumentArray;
            var b = right.ArgumentArray;
            for (var i = 0; order == 0 && i < a.Length; i++)
                order = leftTarget.Partial[i].CompareObjects(a[i], b[i]);

            return order;
        }

        /// <summary>
        /// Compares two function values' cells as <see cref="CompareValues"/> compares the values, through their arguments'
        /// cells.
        /// </summary>
        /// <param name="x">One cell.</param>
        /// <param name="y">The other cell.</param>
        /// <param name="view">The containers of the scope the cells belong to.</param>
        /// <returns>The order.</returns>
        internal override int CompareCells(uint x, uint y, Relations.ContainerView view)
        {
            if (x == y)
                return 0;

            var (left, leftTarget) = Read(x, view);
            var (right, rightTarget) = Read(y, view);
            var order = CompareTargets(leftTarget, rightTarget);
            var a = left.Arguments;
            var b = right.Arguments;
            for (var i = 0; order == 0 && i < a.Length; i++)
                order = leftTarget.Partial[i].CompareCells(a[i], b[i], view);

            return order;
        }

        /// <inheritdoc />
        internal override bool ValuesEqual(TFn x, TFn y)
        {
            var (left, target) = Content(x);
            var (right, _) = Content(y);
            if (ReferenceEquals(left.Target, right.Target) == false || left.ArgumentArray.Length != right.ArgumentArray.Length)
                return false;

            for (var i = 0; i < left.ArgumentArray.Length; i++)
                if (target.Partial[i].ObjectsEqual(left.ArgumentArray[i], right.ArgumentArray[i]) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public void AppendElements(uint cell, Relations.ContainerView view, List<(ColumnType Type, uint Cell)> destination)
        {
            var (fn, target) = Read(cell, view);
            var cells = fn.Arguments;
            for (var i = 0; i < cells.Length; i++)
                destination.Add((target.Partial[i], cells[i]));
        }

        /// <inheritdoc />
        public void AppendDistinctElements(uint cell, Relations.ContainerView view, List<(ColumnType Type, uint Cell)> destination) =>
            AppendElements(cell, view, destination);

        /// <inheritdoc />
        /// <remarks>The partially applied arguments: egglog resolves the name's literal to its function before it interns
        /// a command's literals, so the name is no literal.</remarks>
        public void AppendLiteralParts(object value, List<(ColumnType Type, object Value)> destination)
        {
            var (content, target) = Content((TFn)value);
            var arguments = content.ArgumentArray;
            for (var i = 0; i < arguments.Length; i++)
                destination.Add((target.Partial[i], arguments[i]));
        }

        /// <inheritdoc />
        public bool RebuildsInOrder
        {
            get
            {
                foreach (var type in Volatile.Read(ref partialTypes))
                    if (type is IContainerColumnType { RebuildsInOrder: true })
                        return true;

                return false;
            }
        }

        /// <inheritdoc />
        /// <remarks>The target's id and the rebuild flags, then the arguments' cells.</remarks>
        public long[] Content(uint cell, Relations.ContainerView view)
        {
            var (fn, _) = Read(cell, view);
            var cells = fn.Arguments;
            var content = new long[cells.Length + 2];
            content[0] = fn.Target;
            content[1] = (long)fn.Rebuild;
            for (var i = 0; i < cells.Length; i++)
                content[i + 2] = cells[i];

            return content;
        }

        /// <inheritdoc />
        public long[] Rebuild(long[] content, Func<uint, uint> find)
        {
            var rebuilt = (long[])content.Clone();
            var flags = (ulong)content[1];
            for (var i = 2; i < content.Length; i++)
                if ((flags & (1UL << (i - 2))) != 0)
                    rebuilt[i] = find((uint)content[i]);

            return rebuilt;
        }

        /// <inheritdoc />
        /// <remarks>Any function value: its content carries its target and its arguments' flags.</remarks>
        public bool SharesLayout(uint cell, Relations.ContainerView view) => view.TryGetContainer(env, cell, out _);

        /// <inheritdoc />
        public int RebuildFlags => 0;

        /// <inheritdoc />
        public string CanonicalKey(uint cell, Relations.ContainerView view, Func<ColumnType, uint, string> part)
        {
            var (fn, target) = Read(cell, view);
            var cells = fn.Arguments;
            var key = "(fn" + target.Id;
            for (var i = 0; i < cells.Length; i++)
                key += " " + part(target.Partial[i], cells[i]);

            return key + ")";
        }

        /// <inheritdoc />
        /// <remarks>The function's name, as egglog's <c>serialized_name</c> (fn.rs:178-183).</remarks>
        public string SerializedName(uint cell, Relations.ContainerView view) => Read(cell, view).Target.Name;

        /// <summary>
        /// Gets a value's content and its target resolved for this type.
        /// </summary>
        /// <exception cref="ArgumentException">The value is a default one, or its target's signature is not this type's.</exception>
        (FnValue Content, FnTarget Target) Content(TFn value)
        {
            var content = value.Content ?? throw new ArgumentException($"A default {Name} applies nothing; make one with UnstableFn.Of or unstable-fn.", nameof(value));
            return (content, resolved.TryGetValue(content.Target, out var target) ? target : resolved.GetOrAdd(content.Target, Resolve(content.Target)));
        }

        /// <summary>
        /// Gets which of a value's partially applied arguments hold classes, which its container rebuilds: those of its
        /// target's that do (<see cref="FnTarget.Rebuild"/>), but a nested function value that holds none. egglog's
        /// <c>rebuild_contents</c> (src/sort/fn.rs:57-66) decides a nested one by its sort's inputs; a value's own arguments
        /// decide it here, so a function value of values is one in any scope, and one of classes is rebuilt.
        /// </summary>
        static ulong MaskOf(FnValue content, FnTarget target)
        {
            var mask = target.Rebuild;
            if (target.Nested == 0)
                return mask;

            var arguments = content.ArgumentArray;
            for (var i = 0; i < arguments.Length; i++)
                if ((target.Nested & (1UL << i)) != 0 && ((IFnColumnType)target.Partial[i]).HoldsClassesIn(arguments[i]) == false)
                    mask &= ~(1UL << i);

            return mask;
        }

        /// <summary>
        /// Reads the container of a cell and its target through <paramref name="view"/>, as every read of a function value
        /// by its cell does, so that a value of classes a child holds itself is read where the child holds it.
        /// </summary>
        /// <exception cref="InvalidOperationException">No function value has the cell.</exception>
        (Relations.FunctionContainer Fn, FnTarget Target) Read(uint cell, Relations.ContainerView view)
        {
            if (view.TryGetContainer(env, cell, out var fn) == false)
                throw new InvalidOperationException($"No {Name} has cell {cell}.");

            return (fn, targets[fn.Target]);
        }

        /// <summary>
        /// Compares two targets: by name, by code point, then by id.
        /// </summary>
        static int CompareTargets(FnTarget x, FnTarget y)
        {
            var order = Relations.CodePointComparer.Instance.Compare(x.Name, y.Name);
            return order != 0 ? order : x.Id.CompareTo(y.Id);
        }

        /// <summary>
        /// Gets a target's name and its arguments' extraction sorts.
        /// </summary>
        (string Name, Relations.Sort[] Partial) SortsOf(int id)
        {
            var target = targets[id];
            return (target.Name, Array.ConvertAll(target.Partial, type => type.ExtractionSort));
        }

        /// <summary>
        /// Notes the types of a target's arguments as the type's element types and the sort's inner sorts.
        /// </summary>
        void NoteTypes(FnTarget target)
        {
            if (target.Partial.Length == 0)
                return;

            var current = Volatile.Read(ref partialTypes);
            var known = true;
            foreach (var type in target.Partial)
                known &= Array.IndexOf(current, type) >= 0;

            if (known)
                return;

            lock (sync)
            {
                var grown = new List<ColumnType>(partialTypes);
                foreach (var type in target.Partial)
                    if (grown.Contains(type) == false)
                        grown.Add(type);

                Volatile.Write(ref partialTypes, grown.ToArray());
            }

            sort.AddInnerSorts(Array.ConvertAll(target.Partial, type => type.ExtractionSort));
        }

    }

}
