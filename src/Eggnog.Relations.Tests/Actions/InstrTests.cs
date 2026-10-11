using System;

using Xunit;

using static Eggnog.Relations.Tests.ActionTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the <see cref="Instr"/> classes, <see cref="WriteVal"/> and <see cref="MergeVal"/>: construction checks,
    /// kinds and formatting.
    /// </summary>
    public class InstrTests
    {

        static readonly TableId T = new(1);
        static readonly ExternalFunctionId F = new(2);
        static readonly ExternalFunctionId G = new(3);

        [Fact]
        public void EveryInstructionHasItsKind()
        {
            Assert.Equal(InstrKind.LookupOrInsertDefault, new LookupOrInsertDefaultInstr(T, [V(0)], [new CounterId(0)], C(1), V(1)).Kind);
            Assert.Equal(InstrKind.LookupWithDefault, new LookupWithDefaultInstr(T, [V(0)], C(1), V(1), K(0)).Kind);
            Assert.Equal(InstrKind.Lookup, new LookupInstr(T, [V(0)], C(1), V(1)).Kind);
            Assert.Equal(InstrKind.LookupWithFallback, new LookupWithFallbackInstr(T, [V(0)], F, [V(0)], C(1), V(1)).Kind);
            Assert.Equal(InstrKind.Insert, new InsertInstr(T, [V(0)]).Kind);
            Assert.Equal(InstrKind.InsertIfEq, new InsertIfEqInstr(T, V(0), K(1), [V(0)]).Kind);
            Assert.Equal(InstrKind.Remove, new RemoveInstr(T, [V(0)]).Kind);
            Assert.Equal(InstrKind.External, new ExternalInstr(F, [V(0)], V(1)).Kind);
            Assert.Equal(InstrKind.ExternalWithFallback, new ExternalWithFallbackInstr(F, [V(0)], G, [], V(1)).Kind);
            Assert.Equal(InstrKind.AssertEq, new AssertEqInstr(V(0), K(1)).Kind);
            Assert.Equal(InstrKind.AssertNe, new AssertNeInstr(V(0), K(1)).Kind);
            Assert.Equal(InstrKind.AssertAnyNe, new AssertAnyNeInstr([V(0), V(1)], 1).Kind);
            Assert.Equal(InstrKind.ReadCounter, new ReadCounterInstr(new CounterId(0), V(1)).Kind);
        }

        [Fact]
        public void OperandsAreCopied()
        {
            QueryEntry[] args = [V(0), K(5)];
            var instr = new InsertInstr(T, args);
            args[0] = K(9);
            Assert.Equal(V(0), instr.Values[0].Variable);
            Assert.True(instr.Values[0].IsVariable);
        }

        [Fact]
        public void WildcardsAndPlaceholdersAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new InsertInstr(T, [QueryEntry.Wildcard()]));
            Assert.Throws<ArgumentException>(() => new AssertEqInstr(QueryEntry.Wildcard(), K(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LookupInstr(T, [V(0)], C(1), Variable.Placeholder));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReadCounterInstr(new CounterId(0), new Variable(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LookupInstr(T, [V(0)], C(-1), V(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExternalInstr(F, [new Variable(-2)], V(1)));
            Assert.Throws<ArgumentException>(() => new LookupOrInsertDefaultInstr(T, [V(0)], [QueryEntry.Wildcard()], C(1), V(1)));
        }

        [Fact]
        public void LookupOrInsertDefaultChecksItsRow()
        {
            // The destination column must lie in key ++ defaults.
            Assert.Throws<ArgumentOutOfRangeException>(() => new LookupOrInsertDefaultInstr(T, [V(0)], [K(1)], C(2), V(1)));

            // A current-value default copies an earlier cell.
            Assert.Throws<ArgumentOutOfRangeException>(() => new LookupOrInsertDefaultInstr(T, [V(0)], [WriteVal.CurrentValue(1)], C(1), V(1)));
            var ok = new LookupOrInsertDefaultInstr(T, [V(0)], [K(1), WriteVal.CurrentValue(1)], C(2), V(1));
            Assert.Equal(2, ok.Defaults.Length);
        }

        [Fact]
        public void OperandsEvaluatedWhileTheDestinationIsTakenCannotReadIt()
        {
            // egglog takes the destination's column out of the bindings while it evaluates these operands, so reading it
            // there panics.
            Assert.Throws<ArgumentException>(() => new LookupOrInsertDefaultInstr(T, [V(1)], [K(0)], C(1), V(1)));
            Assert.Throws<ArgumentException>(() => new LookupOrInsertDefaultInstr(T, [V(0)], [V(1)], C(1), V(1)));
            Assert.Throws<ArgumentException>(() => new LookupWithFallbackInstr(T, [V(0)], F, [V(1)], C(1), V(1)));
            Assert.Throws<ArgumentException>(() => new ExternalWithFallbackInstr(F, [V(0)], G, [V(1)], V(1)));

            // The operands read before the destination is inserted may name it.
            _ = new LookupWithFallbackInstr(T, [V(1)], F, [V(0)], C(1), V(1));
            _ = new ExternalWithFallbackInstr(F, [V(1)], G, [V(0)], V(1));
            _ = new LookupInstr(T, [V(1)], C(1), V(1));
        }

        [Fact]
        public void AssertAnyNeChecksItsDivider()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AssertAnyNeInstr([V(0)], 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AssertAnyNeInstr([V(0)], -1));
            Assert.Throws<ArgumentException>(() => AssertAnyNeInstr.Create([V(0)], [V(1), V(2)]));
            var instr = AssertAnyNeInstr.Create([V(0), V(1)], [V(2), K(3)]);
            Assert.Equal(2, instr.Divider);
            Assert.Equal(2, instr.PairCount);
            Assert.Equal(1, new AssertAnyNeInstr([V(0), V(1), V(2)], 2).PairCount);
        }

        [Fact]
        public void InstructionsFormatReadably()
        {
            Assert.Equal("v1 <- LookupOrInsertDefault t1 [v0, 5] [fresh(ctr0), v2, row[0]] c2",
                new LookupOrInsertDefaultInstr(T, [V(0), K(5)], [new CounterId(0), V(2), WriteVal.CurrentValue(0)], C(2), V(1)).ToString());
            Assert.Equal("v1 <- LookupWithDefault t1 [v0] c1 ?? 7", new LookupWithDefaultInstr(T, [V(0)], C(1), V(1), K(7)).ToString());
            Assert.Equal("v1 <- Lookup t1 [v0] c1", new LookupInstr(T, [V(0)], C(1), V(1)).ToString());
            Assert.Equal("Insert t1 [v0, 3]", new InsertInstr(T, [V(0), K(3)]).ToString());
            Assert.Equal("InsertIfEq t1 v0 == 1 [v0]", new InsertIfEqInstr(T, V(0), K(1), [V(0)]).ToString());
            Assert.Equal("Remove t1 [v0]", new RemoveInstr(T, [V(0)]).ToString());
            Assert.Equal("AssertEq v0 1", new AssertEqInstr(V(0), K(1)).ToString());
            Assert.Equal("AssertNe v0 1", new AssertNeInstr(V(0), K(1)).ToString());
            Assert.Equal("AssertAnyNe [v0] [v1]", AssertAnyNeInstr.Create([V(0)], [V(1)]).ToString());
            Assert.Equal("v1 <- ReadCounter ctr0", new ReadCounterInstr(new CounterId(0), V(1)).ToString());
            Assert.Contains("ExternalWithFallback", new ExternalWithFallbackInstr(F, [V(0)], G, [], V(1)).ToString());
            Assert.Contains("LookupWithFallback", new LookupWithFallbackInstr(T, [V(0)], F, [], C(1), V(1)).ToString());
            Assert.Contains("External", new ExternalInstr(F, [], V(1)).ToString());
        }

        [Fact]
        public void WriteAndMergeValuesConvert()
        {
            WriteVal fromVar = V(3);
            WriteVal fromConst = new Value(4);
            WriteVal fromCounter = new CounterId(2);
            Assert.Equal(WriteValKind.Entry, fromVar.Kind);
            Assert.Equal(V(3), fromVar.Entry.Variable);
            Assert.Equal(4u, fromConst.Entry.Constant.Rep);
            Assert.Equal(WriteValKind.IncrementCounter, fromCounter.Kind);
            Assert.Equal(new CounterId(2), fromCounter.Counter);
            Assert.Equal(WriteValKind.CurrentValue, WriteVal.CurrentValue(1).Kind);
            Assert.Equal(1, WriteVal.CurrentValue(1).Column);

            MergeVal counter = new CounterId(1);
            MergeVal constant = new Value(9);
            Assert.True(counter.IsCounter);
            Assert.False(constant.IsCounter);
            Assert.Equal(9u, constant.Constant.Rep);
            Assert.Equal("fresh(ctr1)", counter.ToString());
            Assert.Equal("9", constant.ToString());
        }

    }

}
