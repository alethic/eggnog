using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    public class MaterializationTests
    {

        static string Dump(Materialization mat)
        {
            var groups = new List<string>();
            for (var g = 0; g < mat.Count; g++)
            {
                var rows = mat.GetValues(g);
                var cells = Enumerable.Range(0, rows.Count).Select(r => "(" + string.Join(",", rows.GetRow(r).ToArray()) + ")");
                groups.Add("[" + string.Join(",", mat.GetKey(g).ToArray()) + "]:" + string.Join("", cells));
            }

            return string.Join(" ", groups);
        }

        [Fact]
        public void GroupsKeepInsertionOrderAndRowsAreAMultiset()
        {
            var mat = new Materialization(2, 1);
            mat.Add([5, 1], [10]);
            mat.Add([2, 2], [20]);
            mat.Add([5, 1], [11]);
            mat.Add([5, 1], [10]);
            Assert.Equal(2, mat.Count);
            Assert.Equal("[5,1]:(10)(11)(10) [2,2]:(20)", Dump(mat));

            Assert.True(mat.TryFind([2, 2], 0, out var g));
            Assert.Equal(1, g);
            Assert.True(mat.TryFind([9, 5, 1], 1, out g));
            Assert.Equal(0, g);
            Assert.False(mat.TryFind([1, 5], 0, out g));
            Assert.Equal(-1, g);
        }

        [Fact]
        public void KeysWithoutCellsFormOneGroup()
        {
            var mat = new Materialization(0, 2);
            mat.Add([], [1, 2]);
            mat.Add([], [3, 4]);
            Assert.Equal(1, mat.Count);
            Assert.True(mat.TryFind([], 0, out var g));
            Assert.Equal(0, g);
            Assert.Equal(2, mat.GetValues(0).Count);
        }

        [Fact]
        public void AddAllAppendsRowsAndNewKeys()
        {
            var a = new Materialization(1, 1);
            a.Add([1], [10]);
            a.Add([2], [20]);
            var b = new Materialization(1, 1);
            b.Add([3], [30]);
            b.Add([1], [11]);
            a.AddAll(b);
            Assert.Equal("[1]:(10)(11) [2]:(20) [3]:(30)", Dump(a));
            Assert.Throws<ArgumentException>(() => a.AddAll(new Materialization(2, 1)));
        }

        [Fact]
        public void DisposeEmpties()
        {
            var mat = new Materialization(1, 1);
            mat.Add([1], [10]);
            mat.Dispose();
            Assert.True(mat.IsEmpty);
            Assert.False(mat.TryFind([1], 0, out _));
            mat.Add([4], [40]);
            Assert.Equal("[4]:(40)", Dump(mat));
        }

        [Fact]
        public void RejectsShortKeysAndRows()
        {
            var mat = new Materialization(2, 2);
            Assert.Throws<ArgumentOutOfRangeException>(() => mat.Add([1], [1, 2]));
            Assert.Throws<ArgumentException>(() => mat.Add([1, 2], [1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => mat.TryFind([1, 2], 1, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Materialization(1, 0));
        }

        [Fact]
        public void MatchesADictionaryOnRandomInput()
        {
            for (var seed = 0; seed < 50; seed++)
            {
                var rng = new Random(seed);
                var keyArity = rng.Next(3);
                var mat = new Materialization(keyArity, 1);
                var order = new List<string>();
                var reference = new Dictionary<string, List<uint>>();
                for (var i = rng.Next(300); i > 0; i--)
                {
                    var key = Enumerable.Range(0, keyArity).Select(_ => (uint)rng.Next(6)).ToArray();
                    var value = (uint)rng.Next(100);
                    mat.Add(key, [value]);
                    var k = string.Join(",", key);
                    if (reference.TryGetValue(k, out var list) == false)
                    {
                        reference.Add(k, list = new List<uint>());
                        order.Add(k);
                    }

                    list.Add(value);
                }

                Assert.Equal(order.Count, mat.Count);
                for (var g = 0; g < mat.Count; g++)
                {
                    Assert.Equal(order[g], string.Join(",", mat.GetKey(g).ToArray()));
                    var rows = mat.GetValues(g);
                    Assert.Equal(reference[order[g]], Enumerable.Range(0, rows.Count).Select(r => rows.GetRow(r)[0]));
                }
            }
        }

    }

}
