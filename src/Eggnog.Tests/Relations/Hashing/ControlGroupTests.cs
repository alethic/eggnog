using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests <see cref="ControlGroup"/>: the vector path, the SWAR fallback and the mask helpers for both mask strides
    /// must agree with a byte-by-byte reference on random control groups.
    /// </summary>
    public class ControlGroupTests
    {

        const int Iterations = 20_000;

        /// <summary>
        /// Draws a control group mixing EMPTY, DELETED and tags, sometimes with many repeats of one tag.
        /// </summary>
        static byte[] RandomGroup(Random random)
        {
            var bytes = new byte[ControlGroup.Width];
            var favorite = (byte)random.Next(0x80);
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = random.Next(5) switch
                {
                    0 => ControlGroup.Empty,
                    1 => ControlGroup.Deleted,
                    2 => favorite,
                    _ => (byte)random.Next(0x80),
                };
            }

            return bytes;
        }

        static Vector128<byte> AsVector(byte[] bytes) => Vector128.Create(bytes);

        /// <summary>
        /// Decodes a platform match mask into the set of matched byte indices with the stride-aware helpers.
        /// </summary>
        static List<int> Decode(ulong mask)
        {
            var result = new List<int>();
            for (; mask != 0; mask = ControlGroup.RemoveLowestBit(mask))
                result.Add(ControlGroup.LowestSetBit(mask));

            return result;
        }

        /// <summary>
        /// Decodes a 16-bit scalar mask (bit i for byte i).
        /// </summary>
        static List<int> DecodeBytewise(ulong mask)
        {
            Assert.Equal(0UL, mask >> ControlGroup.Width);
            var result = new List<int>();
            for (var i = 0; i < ControlGroup.Width; i++)
                if ((mask & (1UL << i)) != 0)
                    result.Add(i);

            return result;
        }

        static List<int> Expected(byte[] bytes, Func<byte, bool> predicate)
        {
            var result = new List<int>();
            for (var i = 0; i < bytes.Length; i++)
                if (predicate(bytes[i]))
                    result.Add(i);

            return result;
        }

        [Fact]
        public void TagIsTopSevenBits()
        {
            Assert.Equal(0, ControlGroup.Tag(0));
            Assert.Equal(0x7F, ControlGroup.Tag(ulong.MaxValue));
            Assert.Equal(0x01, ControlGroup.Tag(1UL << 57));
            Assert.Equal(0x00, ControlGroup.Tag((1UL << 57) - 1));
            Assert.Equal(0x40, ControlGroup.Tag(1UL << 63));
        }

        [Fact]
        public void VectorMatchesAgreeWithReference()
        {
            var random = new Random(1);
            for (var n = 0; n < Iterations; n++)
            {
                var bytes = RandomGroup(random);
                var group = AsVector(bytes);
                var tag = random.Next(2) == 0 ? bytes[random.Next(bytes.Length)] : (byte)random.Next(0x80);
                if (tag >= 0x80)
                    tag = (byte)random.Next(0x80);

                Assert.Equal(Expected(bytes, b => b == tag), Decode(ControlGroup.MatchTag(group, tag)));
                Assert.Equal(Expected(bytes, b => b == ControlGroup.Empty), Decode(ControlGroup.MatchEmpty(group)));
                Assert.Equal(Expected(bytes, b => b >= 0x80), Decode(ControlGroup.MatchEmptyOrDeleted(group)));
                Assert.Equal(Expected(bytes, b => b < 0x80), Decode(ControlGroup.MatchFull(group)));
            }
        }

        [Fact]
        public void ScalarMatchesAgreeWithReference()
        {
            var random = new Random(2);
            for (var n = 0; n < Iterations; n++)
            {
                var bytes = RandomGroup(random);
                var group = AsVector(bytes);
                var low = ControlGroup.Low(group);
                var high = ControlGroup.High(group);
                var tag = random.Next(2) == 0 && bytes[0] < 0x80 ? bytes[0] : (byte)random.Next(0x80);

                Assert.Equal(Expected(bytes, b => b == tag), DecodeBytewise(ControlGroup.ScalarMatchTag(low, high, tag)));
                Assert.Equal(Expected(bytes, b => b == ControlGroup.Empty), DecodeBytewise(ControlGroup.ScalarMatchEmpty(low, high)));
                Assert.Equal(Expected(bytes, b => b >= 0x80), DecodeBytewise(ControlGroup.ScalarMatchEmptyOrDeleted(low, high)));
                Assert.Equal(Expected(bytes, b => b < 0x80), DecodeBytewise(ControlGroup.ScalarMatchFull(low, high)));
            }
        }

        [Fact]
        public void ScalarTagMatchHasNoFalsePositivesNextToAMatch()
        {
            // The classic (x - 0x01..) & ~x & 0x80.. zero-byte trick reports a false match in a byte holding 0x01 above
            // a true match (the borrow); the exact form must not.
            var bytes = new byte[ControlGroup.Width];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)(i % 2 == 0 ? 0x10 : 0x11);

            var group = AsVector(bytes);
            var mask = ControlGroup.ScalarMatchTag(ControlGroup.Low(group), ControlGroup.High(group), 0x10);
            Assert.Equal(Expected(bytes, b => b == 0x10), DecodeBytewise(mask));
        }

        [Fact]
        public void ConvertSpecialToEmptyAndFullToDeletedAgreesWithReference()
        {
            var random = new Random(3);
            for (var n = 0; n < Iterations; n++)
            {
                var bytes = RandomGroup(random);
                var expected = new byte[bytes.Length];
                for (var i = 0; i < bytes.Length; i++)
                    expected[i] = bytes[i] >= 0x80 ? ControlGroup.Empty : ControlGroup.Deleted;

                var group = AsVector(bytes);
                var converted = ControlGroup.ConvertSpecialToEmptyAndFullToDeleted(group);
                var actual = new byte[ControlGroup.Width];
                converted.CopyTo(actual);
                Assert.Equal(expected, actual);

                var scalar = ControlGroup.FromWords(
                    ControlGroup.ScalarConvertSpecialToEmptyAndFullToDeleted(ControlGroup.Low(group)),
                    ControlGroup.ScalarConvertSpecialToEmptyAndFullToDeleted(ControlGroup.High(group)));
                scalar.CopyTo(actual);
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void MaskHelpersAgreeForBothStrides()
        {
            // Byte-wise (x64, scalar) masks put byte i at bit i; nibble masks (ARM64) at bit 4i + 3. Check both encodings
            // on every platform through the stride-parameterized helpers.
            var random = new Random(4);
            for (var n = 0; n < Iterations; n++)
            {
                var set = new List<int>();
                var density = random.Next(1, 8);
                for (var i = 0; i < ControlGroup.Width; i++)
                    if (random.Next(density) == 0)
                        set.Add(i);

                ulong bytewise = 0;
                ulong nibbles = 0;
                foreach (var i in set)
                {
                    bytewise |= 1UL << i;
                    nibbles |= 8UL << (4 * i);
                }

                var leading = set.Count == 0 ? ControlGroup.Width : ControlGroup.Width - 1 - set[^1];
                var trailing = set.Count == 0 ? ControlGroup.Width : set[0];

                foreach (var (mask, shift) in new[] { (bytewise, 0), (nibbles, 2) })
                {
                    Assert.Equal(leading, ControlGroup.LeadingZeros(mask, shift));
                    Assert.Equal(trailing, ControlGroup.TrailingZeros(mask, shift));

                    var decoded = new List<int>();
                    for (var m = mask; m != 0; m = ControlGroup.RemoveLowestBit(m))
                        decoded.Add(ControlGroup.LowestSetBit(m, shift));

                    Assert.Equal(set, decoded);
                }
            }
        }

        [Fact]
        public void EmptySingletonIsOneEmptyGroup()
        {
            Assert.Equal(ControlGroup.Width, ControlGroup.EmptySingleton.Length);
            Assert.All(ControlGroup.EmptySingleton, b => Assert.Equal(ControlGroup.Empty, b));
        }

    }

}
