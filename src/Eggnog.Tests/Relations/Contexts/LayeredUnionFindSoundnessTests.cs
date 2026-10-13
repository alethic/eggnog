using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Soundness tests for the layered union-find: structural calls from inside a visitor of a frozen context, the
    /// precondition that the root's displaced ids only grow while it has live descendants, and random programs run
    /// concurrently under a frozen chain against the naive model.
    /// </summary>
    public class LayeredUnionFindSoundnessTests
    {

        /// <summary>
        /// CreateChild, Freeze and Dispose throw <see cref="InvalidOperationException"/> from inside a ScanMerges
        /// visitor of the context. A frozen context's ScanMerges takes no domain, so disposing the context from inside
        /// its own visitor must still be refused; otherwise the scan runs on over a released overlay and returns a
        /// partial result without any error.
        /// </summary>
        [Fact]
        public void DisposingAFrozenContextFromInsideItsOwnScanMergesIsRefused()
        {
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 8).Select(_ => root.NewId()).ToArray();
            var child = root.CreateChild();
            child.Union(ids[1], ids[0]);
            child.Union(ids[3], ids[2]);
            child.Union(ids[5], ids[4]);
            child.Rebuild();
            root.Freeze();
            child.Freeze();

            var all = new MergeCollector();
            child.ScanMerges(ref all);
            Assert.Equal(6, all.Pairs.Count);

            Exception? disposeError = null;
            var first = true;
            var visitor = new MergeCollector((_, _) =>
            {
                if (first == false)
                    return;

                first = false;
                disposeError = Record.Exception(() => child.Dispose());
            });

            var scanError = Record.Exception(() => child.ScanMerges(ref visitor));

            // Either the re-entrant Dispose is refused, or the scan reports that the context went away under it.
            // Silently returning a partial scan of a context that is now disposed is neither.
            Assert.True(
                disposeError is InvalidOperationException || scanError is not null,
                $"Dispose inside the visitor succeeded (disposed: {child.IsDisposed}) and ScanMerges returned {visitor.Pairs.Count} of {all.Pairs.Count} pairs without an error.");
        }

        /// <summary>
        /// A parent's displaced count only grows while it has live descendants, because a child's cursor indexes the
        /// parent's displaced ids. Clearing the root's union-find under a live child must be refused; otherwise the
        /// child's cursor points past the parent's rows and its catch-up silently skips every later displacement.
        /// </summary>
        [Fact]
        public void ClearingTheRootUnionFindUnderALiveChildIsRefused()
        {
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 6).Select(_ => root.NewId()).ToArray();
            root.Union(ids[1], ids[0]);
            root.Union(ids[2], ids[0]);
            root.Rebuild();
            var child = root.CreateChild();
            Assert.Equal(ids[0], child.Find(ids[2]));

            var clear = Record.Exception(() => root.Database.ClearTable(store.UnionFindTable));
            if (clear is InvalidOperationException)
                return;

            // The clear went through: the child must at least stay a copy of its parent.
            root.Union(ids[4], ids[3]);
            root.Rebuild();
            var rebuild = Record.Exception(() => child.Rebuild());
            Assert.True(rebuild is null, $"the child's rebuild failed after the root's union-find was cleared: {rebuild?.Message}");
            Assert.Equal(root.Find(ids[4]), child.Find(ids[4]));
        }

        /// <summary>
        /// A context's epochs never decrease, because its operations run one at a time in its domain. A write batch
        /// whose action rebuilds the same context (catching up with a parent that changed) would run a nested epoch and
        /// journal imports at a later timestamp than the batch's own; the batch's union, stamped with the batch's
        /// earlier epoch, would then trip the journal's order check and fault the child. Either the nested operation
        /// must be refused, leaving the child usable, or the batch must succeed.
        /// </summary>
        [Fact]
        public void ANestedRebuildInsideAWriteDoesNotFaultTheChild()
        {
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 6).Select(_ => root.NewId()).ToArray();
            var child = root.CreateChild();

            Exception? nested = null;
            var error = Record.Exception(() => child.Write(state =>
            {
                // The parent changes (it shares the domain), and the child catches up inside its own batch.
                nested = Record.Exception(() =>
                {
                    root.Union(ids[1], ids[0]);
                    child.Rebuild();
                });

                Span<uint> row = stackalloc uint[3];
                row[0] = ids[3];
                row[1] = ids[2];
                row[2] = state.Timestamp;
                state.StageInsert(store.UnionFindTable, row);
                return 0;
            }));

            Assert.True(
                child.IsFaulted == false,
                $"the child was faulted by its own write batch (nested: {nested?.Message ?? "allowed"}; batch: {error?.Message})");
        }

        /// <summary>
        /// Children of a frozen chain run random union programs concurrently, each with a grandchild sharing its domain
        /// and a freeze half-way, and every find is checked against the naive model of the chain.
        /// </summary>
        [Fact]
        public void ConcurrentProgramsUnderAFrozenChainAgreeWithTheModel()
        {
            var store = new Store();
            var root = store.Root;
            var model = new ReferenceUnionFindContext();
            var setup = new Random(5);
            for (var i = 0; i < 300; i++)
                model.Mint(root.NewId());

            var rootIds = model.VisibleIds();
            for (var i = 0; i < 150; i++)
            {
                var a = rootIds[setup.Next(rootIds.Length)];
                var b = rootIds[setup.Next(rootIds.Length)];
                Assert.Equal(model.Union(a, b), root.Union(a, b));
            }

            root.Rebuild();
            var middle = root.CreateChild();
            var middleModel = model.CreateChild();
            for (var i = 0; i < 20; i++)
                middleModel.Mint(middle.NewId());

            var middleIds = middleModel.VisibleIds();
            for (var i = 0; i < 120; i++)
            {
                var a = middleIds[setup.Next(middleIds.Length)];
                var b = middleIds[setup.Next(middleIds.Length)];
                Assert.Equal(middleModel.Union(a, b), middle.Union(a, b));
            }

            // The root changes after its child exists, so the middle context re-keys before both freeze.
            for (var i = 0; i < 40; i++)
            {
                var a = rootIds[setup.Next(rootIds.Length)];
                var b = rootIds[setup.Next(rootIds.Length)];
                Assert.Equal(model.Union(a, b), root.Union(a, b));
            }

            root.Rebuild();
            root.Freeze();
            middle.Freeze();
            middle.CheckInvariants();

            var failures = new List<string>();
            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, seed =>
            {
                try
                {
                    RunChild(middle, middleModel, seed);
                }
                catch (Exception e)
                {
                    lock (failures)
                        failures.Add($"seed {seed}: {e}");
                }
            });

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(3)));
            Assert.Equal(0, middle.Gate.LiveDescendants);
            Assert.Equal(1, root.Gate.LiveDescendants);
        }

        /// <summary>
        /// A frozen context is readable from any thread without its domain, and the checker requires only that it be
        /// clean, which a frozen context always is. Checking it while its children are created and disposed
        /// on other threads (as concurrent-children harnesses do) must not report violations that do not exist.
        /// </summary>
        [Fact]
        public void CheckingAFrozenContextWhileItsChildrenComeAndGoReportsNothing()
        {
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            root.Union(ids[1], ids[0]);
            root.Rebuild();
            root.Freeze();

            using var stop = new System.Threading.CancellationTokenSource();
            var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                while (stop.IsCancellationRequested == false)
                {
                    var child = root.CreateChild();
                    child.Dispose();
                }
            })).ToArray();

            Exception? error = null;
            try
            {
                for (var i = 0; i < 20_000 && error is null; i++)
                    error = Record.Exception(() => root.CheckInvariants());
            }
            finally
            {
                stop.Cancel();
                Task.WaitAll(workers);
            }

            Assert.True(error is null, error?.Message);
        }

        /// <summary>
        /// Runs a random union program in a new child of <paramref name="parent"/> and, from some step on, in a
        /// grandchild, freezing the child at a random step; every check compares each find with the model.
        /// </summary>
        static void RunChild(Context parent, ReferenceUnionFindContext parentModel, int seed)
        {
            var random = new Random(seed);
            var child = parent.CreateChild();
            var model = parentModel.CreateChild();
            Context? grandchild = null;
            ReferenceUnionFindContext? grandModel = null;

            void Check(Context context, ReferenceUnionFindContext m)
            {
                context.Rebuild();
                foreach (var (id, canonical) in m.Canonicals())
                    Assert.True(canonical == context.Find(id), $"find({id}) = {context.Find(id)}, the model says {canonical}");

                context.CheckInvariants();
            }

            for (var step = 0; step < 300; step++)
            {
                var useGrandchild = grandchild is not null && random.Next(2) == 0;
                var context = useGrandchild ? grandchild! : child;
                var m = useGrandchild ? grandModel! : model;
                var roll = random.Next(100);
                if (context.IsFrozen)
                {
                    if (roll < 20)
                        Check(context, m);

                    continue;
                }

                var ids = m.VisibleIds();
                if (roll < 15 || ids.Length < 2)
                {
                    m.Mint(context.NewId());
                    continue;
                }

                if (roll < 75)
                {
                    var a = ids[random.Next(ids.Length)];
                    var b = ids[random.Next(ids.Length)];
                    Assert.Equal(m.Union(a, b), context.Union(a, b));
                }
                else if (roll < 80 && grandchild is null)
                {
                    grandchild = child.CreateChild();
                    grandModel = model.CreateChild();
                }
                else if (roll < 82 && context == child && child.IsFrozen == false)
                {
                    child.Freeze();
                    model.IsFrozen = true;
                }
                else
                {
                    Check(context, m);
                }
            }

            if (grandchild is not null)
            {
                Check(grandchild, grandModel!);
                grandchild.Dispose();
            }

            Check(child, model);
            child.Dispose();
        }

    }

}
