using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace Eggnog.Relations
{

    /// <summary>
    /// The SwissTable control-byte primitives behind <see cref="RawHashTable{TEntry}"/>: 16-byte group loads and the
    /// tag, empty and full matches that hashbrown performs with SSE2 or NEON, here with <see cref="Vector128{T}"/>
    /// and a SWAR fallback for platforms without vector hardware.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A control byte is <see cref="Empty"/> (<c>0xFF</c>), <see cref="Deleted"/> (<c>0x80</c>), or the 7-bit tag of
    /// a full slot (top bit clear), which is the top seven bits of the slot's 64-bit hash (see <see cref="Tag"/>).
    /// </para>
    /// <para>
    /// A match returns a bit mask with one marker bit per matching byte of the group. On x64 and in the scalar
    /// fallback byte <c>i</c> is bit <c>i</c> (a <c>pmovmskb</c>); on ARM64 it is bit <c>4i + 3</c>, because a
    /// narrowing shift (<c>shrn</c>) is much cheaper there than a movemask emulation. Callers never look at bit
    /// positions directly: they use <see cref="LowestSetBit(ulong)"/>, <see cref="RemoveLowestBit"/>,
    /// <see cref="LeadingZeros(ulong)"/> and <see cref="TrailingZeros(ulong)"/>, which account for the stride. Every
    /// branch on the platform is on a JIT-time constant, so each method compiles to the straight-line sequence for
    /// the running hardware.
    /// </para>
    /// </remarks>
    internal static class ControlGroup
    {

        /// <summary>
        /// The number of control bytes in a group, and the number of trailing control bytes that mirror the first
        /// group so that an unaligned group load never wraps.
        /// </summary>
        public const int Width = 16;

        /// <summary>
        /// The control byte of a slot that has never held an entry since the last clear or rehash. A probe stops at
        /// the first group that contains one.
        /// </summary>
        public const byte Empty = 0xFF;

        /// <summary>
        /// The control byte of a tombstone: a slot whose entry was removed while it sat inside a run of at least
        /// <see cref="Width"/> non-empty slots, so probes must continue past it.
        /// </summary>
        public const byte Deleted = 0x80;

        const ulong RepeatedLsb = 0x0101_0101_0101_0101UL;
        const ulong RepeatedMsb = 0x8080_8080_8080_8080UL;
        const ulong RepeatedLow7 = 0x7F7F_7F7F_7F7F_7F7FUL;
        const ulong CompressMultiplier = 0x0102_0408_1020_4080UL;
        const ulong NibbleMarkers = 0x8888_8888_8888_8888UL;

        /// <summary>
        /// The control bytes of a table with no allocation: one group of <see cref="Empty"/>, shared by every empty
        /// <see cref="RawHashTable{TEntry}"/> and never written. Mirrors hashbrown's static empty group.
        /// </summary>
        internal static readonly byte[] EmptySingleton = CreateEmptySingleton();

        /// <summary>
        /// Gets the base-2 logarithm of the number of mask bits per control byte: 2 on ARM64 (nibble masks), else 0.
        /// A JIT-time constant.
        /// </summary>
        internal static int MaskShift
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => AdvSimd.Arm64.IsSupported ? 2 : 0;
        }

        /// <summary>
        /// Gets the 7-bit tag stored in the control byte of a full slot: the top seven bits of the hash (hashbrown's
        /// <c>h2</c>). The low bits pick the probe start (<c>h1</c>) and the bits just below the tag pick the shard
        /// (<see cref="ShardData"/>), so the three uses stay independent.
        /// </summary>
        /// <param name="hash">The 64-bit hash.</param>
        /// <returns>The tag, in <c>[0, 0x7F]</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Tag(ulong hash) => (byte)(hash >> 57);

        /// <summary>
        /// Gets whether a control byte marks a full slot (top bit clear).
        /// </summary>
        /// <param name="control">The control byte.</param>
        /// <returns><c>true</c> if full.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsFull(byte control) => control < 0x80;

        /// <summary>
        /// Loads the group of <see cref="Width"/> control bytes that starts at <paramref name="index"/>. The caller
        /// guarantees that <c>index + Width</c> does not exceed the control array, which holds for any
        /// <c>index &lt;= bucketMask</c> because of the mirrored trailing group.
        /// </summary>
        /// <param name="control">A reference to the first control byte.</param>
        /// <param name="index">The first byte of the group.</param>
        /// <returns>The group.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<byte> Load(ref byte control, int index) => Vector128.LoadUnsafe(ref control, (nuint)(uint)index);

        /// <summary>
        /// Stores a group of <see cref="Width"/> control bytes at <paramref name="index"/>.
        /// </summary>
        /// <param name="group">The group.</param>
        /// <param name="control">A reference to the first control byte.</param>
        /// <param name="index">The first byte of the group.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Store(Vector128<byte> group, ref byte control, int index) => group.StoreUnsafe(ref control, (nuint)(uint)index);

        /// <summary>
        /// Matches the bytes of a group equal to a full slot's tag.
        /// </summary>
        /// <param name="group">The group.</param>
        /// <param name="tag">The tag, from <see cref="Tag"/>.</param>
        /// <returns>The match mask.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong MatchTag(Vector128<byte> group, byte tag)
        {
            if (AdvSimd.Arm64.IsSupported)
                return NibbleMask(AdvSimd.CompareEqual(group, Vector128.Create(tag)));

            if (Vector128.IsHardwareAccelerated)
                return Vector128.Equals(group, Vector128.Create(tag)).ExtractMostSignificantBits();

            return ScalarMatchTag(Low(group), High(group), tag);
        }

        /// <summary>
        /// Matches the <see cref="Empty"/> bytes of a group.
        /// </summary>
        /// <param name="group">The group.</param>
        /// <returns>The match mask.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong MatchEmpty(Vector128<byte> group)
        {
            if (AdvSimd.Arm64.IsSupported)
                return NibbleMask(AdvSimd.CompareEqual(group, Vector128<byte>.AllBitsSet));

            if (Vector128.IsHardwareAccelerated)
                return Vector128.Equals(group, Vector128<byte>.AllBitsSet).ExtractMostSignificantBits();

            return ScalarMatchEmpty(Low(group), High(group));
        }

        /// <summary>
        /// Matches the <see cref="Empty"/> and <see cref="Deleted"/> bytes of a group: the slots an insert may use.
        /// </summary>
        /// <param name="group">The group.</param>
        /// <returns>The match mask.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong MatchEmptyOrDeleted(Vector128<byte> group)
        {
            if (AdvSimd.Arm64.IsSupported)
                return NibbleMask(AdvSimd.ShiftRightArithmetic(group.AsSByte(), 7).AsByte());

            if (Vector128.IsHardwareAccelerated)
                return group.ExtractMostSignificantBits();

            return ScalarMatchEmptyOrDeleted(Low(group), High(group));
        }

        /// <summary>
        /// Matches the full bytes of a group.
        /// </summary>
        /// <param name="group">The group.</param>
        /// <returns>The match mask.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong MatchFull(Vector128<byte> group)
        {
            if (AdvSimd.Arm64.IsSupported)
                return ~MatchEmptyOrDeleted(group) & NibbleMarkers;

            if (Vector128.IsHardwareAccelerated)
                return group.ExtractMostSignificantBits() ^ 0xFFFFu;

            return ScalarMatchFull(Low(group), High(group));
        }

        /// <summary>
        /// Maps <see cref="Empty"/> and <see cref="Deleted"/> to <see cref="Empty"/> and full to
        /// <see cref="Deleted"/>: the first step of an in-place rehash, after which <see cref="Deleted"/> means "still
        /// to be placed".
        /// </summary>
        /// <param name="group">The group.</param>
        /// <returns>The converted group.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<byte> ConvertSpecialToEmptyAndFullToDeleted(Vector128<byte> group)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                // special = 0xFF where the top bit is set (EMPTY or DELETED), else 0x00; OR-ing in 0x80 then gives
                // 0xFF (EMPTY) for special bytes and 0x80 (DELETED) for full ones.
                var special = Vector128.LessThan(group.AsSByte(), Vector128<sbyte>.Zero).AsByte();
                return special | Vector128.Create(Deleted);
            }

            return FromWords(ScalarConvertSpecialToEmptyAndFullToDeleted(Low(group)), ScalarConvertSpecialToEmptyAndFullToDeleted(High(group)));
        }

        /// <summary>
        /// Gets the index, within the group, of the first byte a non-zero mask marks.
        /// </summary>
        /// <param name="mask">A non-zero match mask.</param>
        /// <returns>The byte index, in <c>[0, Width)</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LowestSetBit(ulong mask) => LowestSetBit(mask, MaskShift);

        /// <summary>
        /// Removes the marker of the first matched byte from a mask.
        /// </summary>
        /// <param name="mask">A match mask.</param>
        /// <returns>The mask without its lowest marker.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong RemoveLowestBit(ulong mask) => mask & (mask - 1);

        /// <summary>
        /// Gets the number of unmatched bytes at the start of the group (<see cref="Width"/> if none matched).
        /// </summary>
        /// <param name="mask">A match mask.</param>
        /// <returns>The count.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int TrailingZeros(ulong mask) => TrailingZeros(mask, MaskShift);

        /// <summary>
        /// Gets the number of unmatched bytes at the end of the group (<see cref="Width"/> if none matched).
        /// </summary>
        /// <param name="mask">A match mask.</param>
        /// <returns>The count.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LeadingZeros(ulong mask) => LeadingZeros(mask, MaskShift);

        /// <summary>
        /// <see cref="LowestSetBit(ulong)"/> for an explicit mask stride; exposed so tests can check both strides on
        /// any platform.
        /// </summary>
        /// <param name="mask">A non-zero match mask.</param>
        /// <param name="shift">The base-2 logarithm of the bits per byte: 0 or 2.</param>
        /// <returns>The byte index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int LowestSetBit(ulong mask, int shift) => BitOperations.TrailingZeroCount(mask) >> shift;

        /// <summary>
        /// <see cref="TrailingZeros(ulong)"/> for an explicit mask stride.
        /// </summary>
        /// <param name="mask">A match mask.</param>
        /// <param name="shift">The base-2 logarithm of the bits per byte: 0 or 2.</param>
        /// <returns>The count.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int TrailingZeros(ulong mask, int shift)
        {
            // A 16-bit mask needs a sentinel at bit 16 so that an empty mask counts as Width; a nibble mask fills all
            // 64 bits, so 64 >> 2 is already Width.
            return shift == 0 ? BitOperations.TrailingZeroCount(mask | (1UL << Width)) : BitOperations.TrailingZeroCount(mask) >> shift;
        }

        /// <summary>
        /// <see cref="LeadingZeros(ulong)"/> for an explicit mask stride.
        /// </summary>
        /// <param name="mask">A match mask.</param>
        /// <param name="shift">The base-2 logarithm of the bits per byte: 0 or 2.</param>
        /// <returns>The count.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int LeadingZeros(ulong mask, int shift)
        {
            // A 16-bit mask sits in the low 16 bits of the word, so the top 48 zero bits are not bytes of the group.
            return shift == 0 ? BitOperations.LeadingZeroCount(mask) - (64 - Width) : BitOperations.LeadingZeroCount(mask) >> shift;
        }

        /// <summary>
        /// SWAR <see cref="MatchTag"/> over the two little-endian words of a group; exact (no false positives).
        /// </summary>
        /// <param name="low">Bytes 0 to 7.</param>
        /// <param name="high">Bytes 8 to 15.</param>
        /// <param name="tag">The tag.</param>
        /// <returns>A 16-bit mask, bit <c>i</c> for byte <c>i</c>.</returns>
        internal static ulong ScalarMatchTag(ulong low, ulong high, byte tag)
        {
            var repeated = RepeatedLsb * tag;
            return Compress(ZeroBytes(low ^ repeated)) | (Compress(ZeroBytes(high ^ repeated)) << 8);
        }

        /// <summary>
        /// SWAR <see cref="MatchEmpty"/>: <see cref="Empty"/> is the only control byte with its top two bits set.
        /// </summary>
        /// <param name="low">Bytes 0 to 7.</param>
        /// <param name="high">Bytes 8 to 15.</param>
        /// <returns>A 16-bit mask, bit <c>i</c> for byte <c>i</c>.</returns>
        internal static ulong ScalarMatchEmpty(ulong low, ulong high) => Compress(low & (low << 1) & RepeatedMsb) | (Compress(high & (high << 1) & RepeatedMsb) << 8);

        /// <summary>
        /// SWAR <see cref="MatchEmptyOrDeleted"/>: the bytes with their top bit set.
        /// </summary>
        /// <param name="low">Bytes 0 to 7.</param>
        /// <param name="high">Bytes 8 to 15.</param>
        /// <returns>A 16-bit mask, bit <c>i</c> for byte <c>i</c>.</returns>
        internal static ulong ScalarMatchEmptyOrDeleted(ulong low, ulong high) => Compress(low & RepeatedMsb) | (Compress(high & RepeatedMsb) << 8);

        /// <summary>
        /// SWAR <see cref="MatchFull"/>: the bytes with their top bit clear.
        /// </summary>
        /// <param name="low">Bytes 0 to 7.</param>
        /// <param name="high">Bytes 8 to 15.</param>
        /// <returns>A 16-bit mask, bit <c>i</c> for byte <c>i</c>.</returns>
        internal static ulong ScalarMatchFull(ulong low, ulong high) => Compress(~low & RepeatedMsb) | (Compress(~high & RepeatedMsb) << 8);

        /// <summary>
        /// SWAR <see cref="ConvertSpecialToEmptyAndFullToDeleted"/> on one word (hashbrown's generic group).
        /// </summary>
        /// <param name="word">Eight control bytes.</param>
        /// <returns>The converted bytes.</returns>
        internal static ulong ScalarConvertSpecialToEmptyAndFullToDeleted(ulong word)
        {
            // full = 0x80 in each full byte. ~0x80 + 0x01 = 0x80 (DELETED) and ~0x00 + 0x00 = 0xFF (EMPTY), with no
            // carry between bytes.
            var full = ~word & RepeatedMsb;
            return ~full + (full >> 7);
        }

        /// <summary>
        /// Gets bytes 0 to 7 of a group as a little-endian word.
        /// </summary>
        /// <param name="group">The group.</param>
        /// <returns>The word.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong Low(Vector128<byte> group) => ToLittleEndian(group.AsUInt64().GetElement(0));

        /// <summary>
        /// Gets bytes 8 to 15 of a group as a little-endian word.
        /// </summary>
        /// <param name="group">The group.</param>
        /// <returns>The word.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong High(Vector128<byte> group) => ToLittleEndian(group.AsUInt64().GetElement(1));

        /// <summary>
        /// Builds a group from two little-endian words.
        /// </summary>
        /// <param name="low">Bytes 0 to 7.</param>
        /// <param name="high">Bytes 8 to 15.</param>
        /// <returns>The group.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> FromWords(ulong low, ulong high) => Vector128.Create(ToLittleEndian(low), ToLittleEndian(high)).AsByte();

        /// <summary>
        /// Turns a byte-wise comparison result (each byte <c>0x00</c> or <c>0xFF</c>) into a nibble mask with bit
        /// <c>4i + 3</c> set for each matched byte <c>i</c>: <c>shrn #4</c> keeps the high nibble of each even byte
        /// and the low nibble of each odd byte, so every byte becomes one nibble of the 64-bit result.
        /// </summary>
        /// <param name="comparison">The comparison result.</param>
        /// <returns>The nibble mask.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong NibbleMask(Vector128<byte> comparison) => AdvSimd.ShiftRightLogicalNarrowingLower(comparison.AsUInt16(), 4).AsUInt64().ToScalar() & NibbleMarkers;

        /// <summary>
        /// Gets <c>0x80</c> in every zero byte of <paramref name="x"/> and <c>0x00</c> elsewhere. Unlike the classic
        /// <c>(x - 0x01..) &amp; ~x</c> trick it has no false positives, because the add never carries across bytes.
        /// </summary>
        /// <param name="x">The word.</param>
        /// <returns>The marker word.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong ZeroBytes(ulong x) => ~(((x & RepeatedLow7) + RepeatedLow7) | x | RepeatedLow7);

        /// <summary>
        /// Gathers the top bit of each byte of a word whose bytes are <c>0x80</c> or <c>0x00</c> into an 8-bit mask:
        /// shifting the markers to bit 0 and multiplying by the sum of <c>2^(7j+7)</c> for <c>j</c> in <c>[0, 8)</c>
        /// lands byte <c>k</c>'s marker at bit <c>56 + k</c>, and every other partial product stays below bit 56
        /// without colliding, so nothing carries.
        /// </summary>
        /// <param name="markers">The marker word.</param>
        /// <returns>The 8-bit mask.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Compress(ulong markers) => ((markers >> 7) * CompressMultiplier) >> 56;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong ToLittleEndian(ulong word) => BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word);

        static byte[] CreateEmptySingleton()
        {
            var control = new byte[Width];
            control.AsSpan().Fill(Empty);
            return control;
        }

    }

}
