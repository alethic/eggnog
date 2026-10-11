using System;
using System.Numerics;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests the registry behavior of <see cref="BaseValues"/> that egglog's tests leave implicit: id assignment,
    /// idempotent registration, which paths need registration, clones, and errors.
    /// </summary>
    public class BaseValuesRegistryTests
    {

        [Fact]
        public void IdsFollowRegistrationOrderAndRegistrationIsIdempotent()
        {
            var bases = new BaseValues();
            Assert.Equal(new BaseValueTypeId(0), bases.RegisterType<StringCodec, string>());
            Assert.Equal(new BaseValueTypeId(1), bases.RegisterType<Int64Codec, long>());
            Assert.Equal(new BaseValueTypeId(0), bases.RegisterType<StringCodec, string>());
            Assert.Equal(new BaseValueTypeId(2), bases.RegisterType<BooleanCodec, bool>());
            Assert.Equal(new BaseValueTypeId(1), bases.RegisterType<Int64Codec, long>());

            Assert.Equal(new BaseValueTypeId(0), bases.GetTypeId<StringCodec, string>());
            Assert.Equal(new BaseValueTypeId(1), bases.GetTypeId<Int64Codec, long>());
            Assert.Equal(new BaseValueTypeId(2), bases.GetTypeId<BooleanCodec, bool>());
            Assert.Equal(new BaseValueTypeId(1), bases.GetTypeId(typeof(Int64Codec)));
            Assert.Equal(new BaseValueTypeId(2), bases.GetTypeId(typeof(BooleanCodec)));

            // Ids are per registry: another registry numbers its types from 0 in its own order.
            var other = new BaseValues();
            Assert.Equal(new BaseValueTypeId(0), other.RegisterType<BooleanCodec, bool>());
            Assert.Equal(new BaseValueTypeId(1), other.RegisterType<StringCodec, string>());
        }

        [Fact]
        public void InlineValuesNeedNoRegistration()
        {
            // As in egglog, get and unwrap return before looking the type up when the value boxes inline.
            var bases = new BaseValues();
            Assert.Equal(5u, bases.Get<Int64Codec, long>(5).Rep);
            Assert.Equal(5L, bases.Unwrap<Int64Codec, long>(new Value(5)));
            Assert.Equal(1u, bases.Get<BooleanCodec, bool>(true).Rep);
            Assert.Equal(Value.StaleRep, bases.Get<Int32Codec, int>(-1).Rep);
            Assert.Equal((short)-1, bases.Unwrap<Int16Codec, short>(Value.Stale));
        }

        [Fact]
        public void InternedValuesNeedRegistration()
        {
            var bases = new BaseValues();
            Assert.Throws<InvalidOperationException>(() => bases.Get<Int64Codec, long>(-1));
            Assert.Throws<InvalidOperationException>(() => bases.Unwrap<Int64Codec, long>(new Value(BaseValues.InternedTag)));
            Assert.Throws<InvalidOperationException>(() => bases.Get<StringCodec, string>("x"));
            Assert.Throws<InvalidOperationException>(() => bases.Unwrap<StringCodec, string>(new Value(0)));
            Assert.Throws<InvalidOperationException>(() => bases.GetTypeId<StringCodec, string>());
            Assert.Throws<InvalidOperationException>(() => bases.GetTypeId(typeof(StringCodec)));
            Assert.Throws<ArgumentNullException>(() => bases.GetTypeId(null!));

            // Registering one type does not register another.
            bases.RegisterType<StringCodec, string>();
            Assert.Equal(0u, bases.Get<StringCodec, string>("x").Rep);
            Assert.Throws<InvalidOperationException>(() => bases.Get<DoubleCodec, double>(1.0));
            Assert.Throws<InvalidOperationException>(() => bases.Get<InternedCodec<string>, string>("x"));
        }

        [Fact]
        public void UnknownInternedValuesAndNullsThrow()
        {
            var bases = new BaseValues();
            bases.RegisterType<StringCodec, string>();
            bases.RegisterType<Int64Codec, long>();

            Assert.Throws<ArgumentOutOfRangeException>(() => bases.Unwrap<StringCodec, string>(new Value(0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => bases.Unwrap<Int64Codec, long>(new Value(BaseValues.InternedTag)));
            Assert.Throws<ArgumentOutOfRangeException>(() => bases.Unwrap<Int64Codec, long>(Value.Stale));
            Assert.Throws<ArgumentNullException>(() => bases.Get<StringCodec, string>(null!));

            bases.Get<Int64Codec, long>(long.MaxValue);
            Assert.Equal(long.MaxValue, bases.Unwrap<Int64Codec, long>(new Value(BaseValues.InternedTag)));
            Assert.Throws<ArgumentOutOfRangeException>(() => bases.Unwrap<Int64Codec, long>(new Value(BaseValues.InternedTag + 1)));
        }

        [Fact]
        public void TheLargestTaggedIndexStopsShortOfStale()
        {
            // egglog's checked_add lets index 2^31 - 1 through, which would tag to u32::MAX (mod.rs:164-171).
            Assert.Equal(Value.StaleRep - 1, BaseValues.InternedTag + BaseValues.MaxTaggedIndex);
            Assert.True((uint)Array.MaxLength <= BaseValues.MaxTaggedIndex + 1);
        }

        [Fact]
        public void TwoCodecsOverOneTypeAreSeparateTypes()
        {
            var bases = new BaseValues();
            var int64 = bases.RegisterType<Int64Codec, long>();
            var tiny = bases.RegisterType<BaseValueTestTinyLongCodec, long>();
            Assert.NotEqual(int64, tiny);

            Assert.Equal(100u, bases.Get<Int64Codec, long>(100).Rep);
            Assert.Equal(BaseValues.InternedTag, bases.Get<BaseValueTestTinyLongCodec, long>(100).Rep);
            Assert.Equal(BaseValues.InternedTag, bases.Get<Int64Codec, long>(-5).Rep);
            Assert.Equal(BaseValues.InternedTag + 1, bases.Get<BaseValueTestTinyLongCodec, long>(-5).Rep);
            Assert.Equal(7u, bases.Get<BaseValueTestTinyLongCodec, long>(7).Rep);

            Assert.Equal(-5L, bases.Unwrap<Int64Codec, long>(new Value(BaseValues.InternedTag)));
            Assert.Equal(100L, bases.Unwrap<BaseValueTestTinyLongCodec, long>(new Value(BaseValues.InternedTag)));
            Assert.Equal("tiny:100", bases.Format(tiny, new Value(BaseValues.InternedTag)));
            Assert.Equal("-5", bases.Format(int64, new Value(BaseValues.InternedTag)));
        }

        [Fact]
        public void ACodecDescribesOneValueType()
        {
            var bases = new BaseValues();
            Assert.Equal(new BaseValueTypeId(0), bases.RegisterType<BaseValueTestDualCodec, int>());
            Assert.Throws<InvalidOperationException>(() => bases.RegisterType<BaseValueTestDualCodec, long>());
            Assert.Throws<InvalidOperationException>(() => bases.Get<BaseValueTestDualCodec, long>(1));
            Assert.Equal(0u, bases.Get<BaseValueTestDualCodec, int>(42).Rep);
            Assert.Equal(new BaseValueTypeId(0), bases.GetTypeId(typeof(BaseValueTestDualCodec)));
            Assert.Equal(new BaseValueTypeId(1), bases.RegisterType<StringCodec, string>());
        }

        [Fact]
        public void ClonesShareInternedValuesButNotLaterRegistrations()
        {
            var bases = new BaseValues();
            bases.RegisterType<StringCodec, string>();
            bases.RegisterType<Int64Codec, long>();
            Assert.Equal(0u, bases.Get<StringCodec, string>("a").Rep);

            var clone = bases.Clone();
            Assert.Equal(new BaseValueTypeId(0), clone.GetTypeId<StringCodec, string>());
            Assert.Equal(new BaseValueTypeId(1), clone.RegisterType<Int64Codec, long>());

            // Interning through either registry is visible through both: the intern storage is shared.
            Assert.Equal(0u, clone.Get<StringCodec, string>("a").Rep);
            Assert.Equal(1u, clone.Get<StringCodec, string>("b").Rep);
            Assert.Equal("b", bases.Unwrap<StringCodec, string>(new Value(1)));
            Assert.Equal(2u, bases.Get<StringCodec, string>("c").Rep);
            Assert.Equal("c", clone.Unwrap<StringCodec, string>(new Value(2)));
            Assert.Equal(BaseValues.InternedTag, clone.Get<Int64Codec, long>(-7).Rep);
            Assert.Equal(-7L, bases.Unwrap<Int64Codec, long>(new Value(BaseValues.InternedTag)));

            // Types registered after the clone belong to one registry, and both number them from the same next id.
            Assert.Equal(new BaseValueTypeId(2), bases.RegisterType<DoubleCodec, double>());
            Assert.Equal(new BaseValueTypeId(2), clone.RegisterType<InternedCodec<BigInteger>, BigInteger>());
            Assert.Throws<InvalidOperationException>(() => clone.Get<DoubleCodec, double>(1.0));
            Assert.Throws<InvalidOperationException>(() => bases.Get<InternedCodec<BigInteger>, BigInteger>(BigInteger.One));
            Assert.Throws<InvalidOperationException>(() => clone.GetTypeId(typeof(DoubleCodec)));
            Assert.Equal("1.0", bases.Format(new BaseValueTypeId(2), bases.Get<DoubleCodec, double>(1.0)));
            Assert.Equal("1", clone.Format(new BaseValueTypeId(2), clone.Get<InternedCodec<BigInteger>, BigInteger>(BigInteger.One)));

            // Registering the same new type in both after the clone gives two tables.
            bases.RegisterType<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>();
            clone.RegisterType<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>();
            Assert.Equal(0u, bases.Get<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new BaseValueTestPoint(1, 1)).Rep);
            Assert.Equal(0u, clone.Get<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new BaseValueTestPoint(2, 2)).Rep);
            Assert.Equal(new BaseValueTestPoint(1, 1), bases.Unwrap<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new Value(0)));
            Assert.Equal(new BaseValueTestPoint(2, 2), clone.Unwrap<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new Value(0)));
        }

        [Fact]
        public void PrinterFormatsEveryType()
        {
            var bases = new BaseValues();
            var str = bases.RegisterType<StringCodec, string>();
            var i64 = bases.RegisterType<Int64Codec, long>();
            var f64 = bases.RegisterType<DoubleCodec, double>();
            var flag = bases.RegisterType<BooleanCodec, bool>();
            var unit = bases.RegisterType<UnitCodec, ValueTuple>();
            var i8 = bases.RegisterType<SByteCodec, sbyte>();

            Assert.Equal("\"hi\\n\"", new BaseValuePrinter(bases, str, bases.Get<StringCodec, string>("hi\n")).ToString());
            Assert.Equal("24", new BaseValuePrinter(bases, i64, bases.Get<Int64Codec, long>(24)).ToString());
            Assert.Equal("-24", new BaseValuePrinter(bases, i64, bases.Get<Int64Codec, long>(-24)).ToString());
            Assert.Equal("2.5", new BaseValuePrinter(bases, f64, bases.Get<DoubleCodec, double>(2.5)).ToString());
            Assert.Equal("true", new BaseValuePrinter(bases, flag, bases.Get<BooleanCodec, bool>(true)).ToString());
            Assert.Equal("()", new BaseValuePrinter(bases, unit, bases.Get<UnitCodec, ValueTuple>(default)).ToString());
            Assert.Equal("-1", new BaseValuePrinter(bases, i8, Value.Stale).ToString());

            var printer = new BaseValuePrinter(bases, i64, new Value(3));
            Assert.Same(bases, printer.Base);
            Assert.Equal(i64, printer.Type);
            Assert.Equal(new Value(3), printer.Value);

            Assert.Throws<ArgumentOutOfRangeException>(() => new BaseValuePrinter(bases, new BaseValueTypeId(6), new Value(0)).ToString());
            Assert.Throws<ArgumentOutOfRangeException>(() => bases.Format(new BaseValueTypeId(-1), new Value(0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => bases.Format(str, new Value(99)));
        }

    }

}
