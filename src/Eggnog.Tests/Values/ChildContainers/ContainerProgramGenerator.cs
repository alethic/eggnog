using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Generates random container programs from a seed (<see cref="Generate"/>): operations over containers of classes
    /// (<see cref="ContainerProgramOp"/>) in a tree of scopes up to three levels below the root, with snapshots to
    /// compare. A program has one of three shapes: a writable root that keeps writing while a child and its descendants
    /// exist; a root that writes a little and freezes, with two or three subtrees of children beneath it, each with its
    /// own frozen and writable levels; or a writable root that may freeze along the way. Deterministic for a seed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every write takes a key fresh in its scope's range, so no write conflicts with another, at the root or in the
    /// replay, but for an echo: a writable scope re-issues, at most once, an earlier container write of a live descendant
    /// at the descendant's key, each class it names replaced by one the descendant's chain has unioned with it by then.
    /// The two values are then one in every view that holds both rows, the descendant's and those below it, and in the
    /// replay, which applies the descendant's write and those unions first; the descendant's catch-up meets the
    /// ancestor's row beside its own, which is where a writable ancestor's later writes collide with a child's. A write
    /// whose value makes a term, <c>VectorOfAdd</c>'s <c>(Add (Num A) (Num B))</c>, is not echoed: the child and the
    /// ancestor would each make a class for it, and the child's catch-up compares the two rows before congruence makes
    /// the classes one, which faults the child (decisions row 9).
    /// </para>
    /// <para>
    /// Keys read (<c>K1</c>, <c>K2</c>) are mostly keys the scope's chain gave a container (a vector, for the operations
    /// that read <c>fv</c>), sometimes a key that holds nothing, so reads, rule triggers and host <c>vec-union</c>s
    /// mostly meet a container. Unions are rarer than writes, so containers survive long enough to be rebuilt. A scope
    /// with live descendants never runs the rules or computes a host <c>vec-union</c>
    /// (<see cref="ContainerProgramStep.LeafOnly"/>), and every live scope ends with a snapshot.
    /// </para>
    /// </remarks>
    static class ContainerProgramGenerator
    {

        /// <summary>
        /// The number of operations.
        /// </summary>
        const int OperationCount = (int)ContainerProgramOp.Query + 1;

        /// <summary>
        /// Generates the program of <paramref name="seed"/>.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <param name="shape">Receives the program's shape, for messages.</param>
        /// <returns>The steps.</returns>
        public static List<ContainerProgramStep> Generate(int seed, out string shape)
        {
            var rng = new Random(seed);
            var mode = rng.Next(3);
            var nextId = 0;
            var root = new ContainerProgramScopeModel(0, null);

            // The setup writes key 0 of every function.
            root.Keys.Add(0);
            root.VectorKeys.Add(0);
            var steps = new List<ContainerProgramStep>();
            for (var i = rng.Next(0, 6); i > 0; i--)
                steps.Add(RandomOperation(rng, root));

            if (mode == 1)
            {
                shape = "frozen root";
                root.Frozen = true;
                steps.Add(new ContainerProgramStep(ContainerProgramStepKind.Freeze, 0, 0, default, 0, 0, 0, 0, 0, 0));
                for (var t = rng.Next(2, 4); t > 0; t--)
                {
                    var top = new ContainerProgramScopeModel(++nextId, root);
                    steps.Add(new ContainerProgramStep(ContainerProgramStepKind.Create, top.Id, 0, default, 0, 0, 0, 0, 0, 0));
                    steps.AddRange(Subtree(rng, root, top, false, false, 30, ref nextId));
                }
            }
            else
            {
                shape = mode == 0 ? "writable root" : "root that may freeze";
                var top = new ContainerProgramScopeModel(++nextId, root);
                steps.Add(new ContainerProgramStep(ContainerProgramStepKind.Create, top.Id, 0, default, 0, 0, 0, 0, 0, 0));
                steps.AddRange(Subtree(rng, root, top, true, mode == 2, 50, ref nextId));
            }

            return steps;
        }

        /// <summary>
        /// Generates the steps of the subtree under <paramref name="top"/>, and of the root too when
        /// <paramref name="rootActs"/>, which freezes only when <paramref name="rootFreezes"/>.
        /// </summary>
        static List<ContainerProgramStep> Subtree(Random rng, ContainerProgramScopeModel root, ContainerProgramScopeModel top, bool rootActs, bool rootFreezes,
            int count, ref int nextId)
        {
            var steps = new List<ContainerProgramStep>();
            var nodes = new List<ContainerProgramScopeModel> { top };
            if (rootActs)
                nodes.Add(root);

            var writes = new List<(ContainerProgramScopeModel Node, ContainerProgramStep Step)>();
            var unions = new List<(ContainerProgramScopeModel Node, long A, long B)>();

            for (var step = 0; step < count; step++)
            {
                var live = nodes.Where(n => n.Disposed == false).ToList();
                var node = live[rng.Next(live.Count)];
                var roll = rng.Next(100);
                if (roll < 8)
                {
                    if (node.Chain().Count < 4)
                    {
                        var child = new ContainerProgramScopeModel(++nextId, node);
                        nodes.Add(child);
                        steps.Add(new ContainerProgramStep(ContainerProgramStepKind.Create, child.Id, node.Id, default, 0, 0, 0, 0, 0, 0));
                    }

                    continue;
                }

                if (roll < 11)
                {
                    if (node != root && node != top)
                    {
                        steps.Add(new ContainerProgramStep(ContainerProgramStepKind.Snapshot, node.Id, 0, default, 0, 0, 0, 0, 0, 0));
                        steps.Add(new ContainerProgramStep(ContainerProgramStepKind.Dispose, node.Id, 0, default, 0, 0, 0, 0, 0, 0));
                        node.Disposed = true;
                    }

                    continue;
                }

                if (roll < 13)
                {
                    if (node.Frozen == false && (node.Parent is null ? rootFreezes : node.Parent.Frozen))
                    {
                        node.Frozen = true;
                        steps.Add(new ContainerProgramStep(ContainerProgramStepKind.Freeze, node.Id, 0, default, 0, 0, 0, 0, 0, 0));
                    }

                    continue;
                }

                if (roll < 17)
                {
                    steps.Add(new ContainerProgramStep(ContainerProgramStepKind.Snapshot, node.Id, 0, default, 0, 0, 0, 0, 0, 0));
                    continue;
                }

                if (node.Frozen == false && rng.Next(4) == 0 && Echo(rng, node, steps, writes, unions))
                    continue;

                var operation = RandomOperation(rng, node);
                steps.Add(operation);
                if (operation.Op == ContainerProgramOp.Union)
                    unions.Add((node, operation.A, operation.B));
                else if (operation.Op == ContainerProgramOp.Batch && operation.C % 2 == 0)
                    unions.Add((node, operation.A, operation.C));
                else if (operation.Op is ContainerProgramOp.Vector or ContainerProgramOp.NestedVector or ContainerProgramOp.Set or ContainerProgramOp.Map
                    or ContainerProgramOp.MultiSet or ContainerProgramOp.Pair or ContainerProgramOp.Function)
                    writes.Add((node, operation));
            }

            foreach (var node in nodes.Where(n => n.Disposed == false))
                steps.Add(new ContainerProgramStep(ContainerProgramStepKind.Snapshot, node.Id, 0, default, 0, 0, 0, 0, 0, 0));

            return steps;
        }

        /// <summary>
        /// Adds an echo to <paramref name="steps"/> when <paramref name="node"/> has a live descendant with a container
        /// write not echoed yet: the write, in <paramref name="node"/>, at the descendant's key, each class it names
        /// replaced by a random member of its class under the unions the descendant's chain made so far.
        /// </summary>
        /// <returns>Whether an echo was added.</returns>
        static bool Echo(Random rng, ContainerProgramScopeModel node, List<ContainerProgramStep> steps,
            List<(ContainerProgramScopeModel Node, ContainerProgramStep Step)> writes, List<(ContainerProgramScopeModel Node, long A, long B)> unions)
        {
            var candidates = writes.Where(w => w.Node != node && w.Node.Disposed == false && w.Node.Chain().Contains(node)).ToList();
            if (candidates.Count == 0)
                return false;

            var (descendant, step) = candidates[rng.Next(candidates.Count)];
            writes.Remove((descendant, step));
            var chain = descendant.Chain();
            var parent = new long[6];
            for (var i = 0; i < parent.Length; i++)
                parent[i] = i;

            long Find(long x) => parent[x] == x ? x : Find(parent[x]);
            foreach (var (scope, a, b) in unions)
                if (chain.Contains(scope))
                    parent[Find(a)] = Find(b);

            long Member(long x)
            {
                var members = Enumerable.Range(0, parent.Length).Where(i => Find(i) == Find(x)).ToArray();
                return members[rng.Next(members.Length)];
            }

            var echo = step with { Scope = node.Id, A = Member(step.A), B = Member(step.B), C = step.Op == ContainerProgramOp.NestedVector ? Member(step.C) : step.C };
            steps.Add(echo);
            node.Keys.Add(echo.K);
            if (echo.Op == ContainerProgramOp.Vector)
                node.VectorKeys.Add(echo.K);

            return true;
        }

        /// <summary>
        /// Generates an operation the scope may apply: one that only reads in a frozen scope, and none that reads the
        /// scope's own facts to write in a scope with live descendants; records the key a container write takes.
        /// </summary>
        static ContainerProgramStep RandomOperation(Random rng, ContainerProgramScopeModel node)
        {
            var leaf = node.HasLiveDescendants == false;
            var chain = node.Chain();
            while (true)
            {
                var op = (ContainerProgramOp)rng.Next(OperationCount);
                if (op is ContainerProgramOp.Union or ContainerProgramOp.UnionAdd && rng.Next(3) == 0)
                    continue;

                var step = new ContainerProgramStep(ContainerProgramStepKind.Operation, node.Id, 0, op, rng.Next(6), rng.Next(6), rng.Next(6), 0, 0, 0);
                if (node.Frozen && step.ReadsOnly == false)
                    continue;

                if (leaf == false && step.LeafOnly)
                    continue;

                // A key of the chain that holds a container (a vector for the operations that read fv), or now and then
                // one that holds nothing, past every key a scope's range gives out.
                var vectors = op is ContainerProgramOp.HostVectorUnion or ContainerProgramOp.CheckVectors or ContainerProgramOp.CheckConstantVector
                    or ContainerProgramOp.VectorUnion;
                var keys = chain.SelectMany(n => vectors ? n.VectorKeys : n.Keys).ToArray();
                long Read() => keys.Length == 0 || rng.Next(8) == 0 ? chain[rng.Next(chain.Count)].Base + 98 : keys[rng.Next(keys.Length)];
                var key = step.ReadsOnly ? node.Base : node.Base + node.NextKey++;
                var k1 = rng.Next(10) == 0 ? 99 : Read();
                step = step with { K = key, K1 = k1, K2 = Read() };
                if (op is ContainerProgramOp.Vector or ContainerProgramOp.VectorOfAdd or ContainerProgramOp.Batch)
                    node.VectorKeys.Add(key);

                if (op is ContainerProgramOp.Vector or ContainerProgramOp.VectorOfAdd or ContainerProgramOp.Batch or ContainerProgramOp.NestedVector
                    or ContainerProgramOp.Set or ContainerProgramOp.Map or ContainerProgramOp.MultiSet or ContainerProgramOp.Pair or ContainerProgramOp.Function)
                    node.Keys.Add(key);

                return step;
            }
        }

    }

}
