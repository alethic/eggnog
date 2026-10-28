using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Runs one random container program (<see cref="ContainerProgramGenerator"/>) in its tree of scopes and checks it
    /// (<see cref="Run"/>): after every step, the invariants of every live context; at every snapshot, that the scope's
    /// view (<see cref="ContainerProgramView"/>) equals that of a fresh root replaying, in order, the operations of the
    /// scope's chain until then, that each of the scope's own operations had the outcome it has in the replay, and so did
    /// each write of the chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The replay is the reference egglog has no form of for a child under a writable ancestor (decisions row 9): the
    /// root's and the scope's ancestors' operations and its own, applied to one root in the order they ran, refused ones
    /// too. An operation the program refused (an outcome starting with <c>E:</c>) must be refused by the replay as well:
    /// the program writes a key twice only in an echo, whose value is the first write's in the replay, so a refusal is a
    /// read of a missing key, a primitive without a result, or a refusal the root makes as well, and a child that refuses
    /// a write the root accepts fails the property. A read of an ancestor is not compared, since the replay has applied
    /// the operations of the chain below it that ran before it; its writes are, and its <see cref="ContainerProgramStep.LeafOnly"/>
    /// operations, which ran before any scope below it in the chain existed. An exception other than a refusal, a faulted
    /// scope, a disposal that fails, or an invariant violation is a failure on its own.
    /// </para>
    /// <para>
    /// Single-threaded and deterministic: the program's scopes run serially, and every comparison is free of ids.
    /// </para>
    /// </remarks>
    sealed class ContainerProgramRunner
    {

        readonly List<ContainerProgramStep> steps;
        readonly Dictionary<int, string> outcomes = new();
        readonly List<(int Index, int Scope, int[] Chain, string View)> snapshots = new();

        /// <summary>
        /// Initializes a runner of <paramref name="steps"/>.
        /// </summary>
        public ContainerProgramRunner(List<ContainerProgramStep> steps)
        {
            this.steps = steps;
        }

        /// <summary>Gets the failures <see cref="Run"/> found.</summary>
        public List<string> Failures { get; } = new();

        /// <summary>Gets the number of snapshots compared.</summary>
        public int Snapshots => snapshots.Count;

        /// <summary>Gets the number of operations a child applied without a refusal.</summary>
        public int ChildOperations { get; private set; }

        /// <summary>
        /// Runs the program, checks every snapshot against its replay, and records the failures.
        /// </summary>
        public void Run()
        {
            var theory = new ContainerProgramTheory();
            var root = theory.Graph.Root;
            theory.Setup(root);
            var scopes = new Dictionary<int, Scope> { [0] = root };
            var parents = new Dictionary<int, int> { [0] = 0 };
            try
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    try
                    {
                        Apply(theory, scopes, parents, i, step);
                    }
                    catch (Exception exception)
                    {
                        Failures.Add($"step {i} ({step}): {exception.GetType().Name}: {exception.Message}");
                    }

                    CheckAll(scopes, i, step);
                    if (Failures.Count > 0)
                        return;
                }
            }
            finally
            {
                foreach (var (id, scope) in scopes.OrderByDescending(entry => entry.Key))
                    if (id != 0)
                        scope.Dispose();
            }

            foreach (var snapshot in snapshots)
            {
                var (view, diverged) = Replay(snapshot.Index, snapshot.Scope, snapshot.Chain);
                foreach (var difference in diverged)
                    Failures.Add($"snapshot of s{snapshot.Scope} at step {snapshot.Index}: {difference}");

                if (view != snapshot.View)
                    Failures.Add($"snapshot of s{snapshot.Scope} at step {snapshot.Index} differs from the replay:\n{Diff(snapshot.View, view)}");
            }
        }

        /// <summary>
        /// Applies one step in the program's scopes.
        /// </summary>
        void Apply(ContainerProgramTheory theory, Dictionary<int, Scope> scopes, Dictionary<int, int> parents, int index, ContainerProgramStep step)
        {
            switch (step.Kind)
            {
                case ContainerProgramStepKind.Create:
                    scopes[step.Scope] = scopes[step.Parent].CreateChild();
                    parents[step.Scope] = step.Parent;
                    break;
                case ContainerProgramStepKind.Dispose:
                    scopes[step.Scope].Dispose();
                    scopes.Remove(step.Scope);
                    break;
                case ContainerProgramStepKind.Freeze:
                    scopes[step.Scope].Freeze();
                    break;
                case ContainerProgramStepKind.Snapshot:
                    snapshots.Add((index, step.Scope, Chain(parents, step.Scope), ContainerProgramView.Of(theory, scopes[step.Scope])));
                    break;
                default:
                {
                    var scope = scopes[step.Scope];
                    var outcome = ContainerProgramOps.Apply(theory, scope, step);
                    outcomes[index] = outcome;
                    if (scope.IsFaulted)
                        Failures.Add($"step {index} ({step}) faulted s{step.Scope}: {outcome}");

                    if (step.Scope != 0 && outcome.StartsWith("E:", StringComparison.Ordinal) == false)
                        ChildOperations++;

                    break;
                }
            }
        }

        /// <summary>
        /// Checks the invariants of every live scope's context, cleaning a writable one first.
        /// </summary>
        void CheckAll(Dictionary<int, Scope> scopes, int index, ContainerProgramStep step)
        {
            foreach (var (id, scope) in scopes.OrderBy(entry => entry.Key))
            {
                try
                {
                    if (scope.IsFrozen == false)
                        scope.Engine.EnsureClean();

                    scope.Engine.Context.CheckInvariants();
                }
                catch (Exception exception)
                {
                    Failures.Add($"after step {index} ({step}), s{id}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }

        /// <summary>
        /// Replays the operations of <paramref name="chain"/> before step <paramref name="index"/> in a fresh root, and
        /// gets its view and the outcomes that differ from the program's: of <paramref name="scope"/>'s own operations,
        /// and of the writes of the chain.
        /// </summary>
        (string View, List<string> Diverged) Replay(int index, int scope, int[] chain)
        {
            var diverged = new List<string>();
            var theory = new ContainerProgramTheory();
            var root = theory.Graph.Root;
            theory.Setup(root);
            for (var i = 0; i < index; i++)
            {
                var step = steps[i];
                if (step.Kind != ContainerProgramStepKind.Operation || Array.IndexOf(chain, step.Scope) < 0)
                    continue;

                var outcome = outcomes[i];
                var replayed = ContainerProgramOps.Apply(theory, root, step);
                if ((step.Scope == scope || step.ReadsOnly == false) && replayed != outcome)
                    diverged.Add($"step {i} ({step}) in s{step.Scope} gave '{outcome}', the replay '{replayed}'");
            }

            return (ContainerProgramView.Of(theory, root), diverged);
        }

        /// <summary>
        /// Gets the chain of scope <paramref name="id"/>, from the root.
        /// </summary>
        static int[] Chain(Dictionary<int, int> parents, int id)
        {
            var chain = new List<int>();
            for (var walk = id; ; walk = parents[walk])
            {
                chain.Add(walk);
                if (walk == 0)
                    break;
            }

            chain.Reverse();
            return chain.ToArray();
        }

        /// <summary>
        /// Gets the lines in which two views differ.
        /// </summary>
        static string Diff(string program, string replay)
        {
            var left = program.Split('\n');
            var right = replay.Split('\n');
            var text = new StringBuilder();
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var x = i < left.Length ? left[i] : "";
                var y = i < right.Length ? right[i] : "";
                if (x != y)
                    text.Append("  program: ").Append(x).Append("\n  replay:  ").Append(y).Append('\n');
            }

            return text.ToString();
        }

    }

}
