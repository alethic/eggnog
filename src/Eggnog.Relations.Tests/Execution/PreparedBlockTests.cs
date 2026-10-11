using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    public class PreparedBlockTests
    {

        [Fact]
        public void DerivesBindingsAndScratchSizesOncePerStageList()
        {
            var c0 = new ColumnId(0);
            var c1 = new ColumnId(1);
            var a0 = new AtomId(0);
            var a1 = new AtomId(1);
            var v1 = new Variable(1);
            var v3 = new Variable(3);
            var v4 = new Variable(4);
            JoinStage[] stages =
            [
                new IntersectStage(v3, [new SingleScanSpec(a0, c1, []), new SingleScanSpec(a1, c0, [])]),
                new FusedIntersectStage(new ScanSpec(new SubAtom(a1, [c0, c1]), []), [new StageBinding(c1, v4), new StageBinding(c0, v1)],
                    [new Probe(new ScanSpec(new SubAtom(a0, [c0, c1]), []), [c0, c1])]),
                new FusedIntersectMatStage(new MatId(0), MatScanMode.Value, [v1, v3, v4], [new StageBinding(c0, v3)], []),
            ];

            var block = PreparedBlock.Get(stages);
            Assert.Same(block, PreparedBlock.Get(stages));
            Assert.Equal([v1, v3, v4], block.BoundVariables);
            Assert.Equal(2, block.MaxProbers);
            Assert.Equal(3, block.MaxKey);
            Assert.Equal(2, block.MaxBind);
            Assert.Equal([v3], block.Stages[0].BindVariables);
            Assert.Empty(block.Stages[0].Projection);
            Assert.Equal([c1, c0], block.Stages[1].Projection);
            Assert.Equal([v4, v1], block.Stages[1].BindVariables);
            Assert.Equal([c0], block.Stages[2].Projection);
        }

        [Fact]
        public void EmptyStageListsNeedMinimalScratch()
        {
            var block = PreparedBlock.Get(Array.Empty<JoinStage>());
            Assert.Empty(block.BoundVariables);
            Assert.Equal(1, block.MaxProbers);
            Assert.Equal(1, block.MaxKey);
        }

    }

}
