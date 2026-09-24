using System.Buffers.Binary;
using System.Text;
using Wino.Mapi.Autocomplete;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>
/// Outlook's autocomplete list, as stored in the mailbox.
///
/// The claim these tests have to earn is that a list can be read out of somebody's mailbox, changed,
/// and written back without losing anything - so the ones that matter most are not about the names
/// at all. They are about the bytes nobody understands: the metadata blocks, the extra information a
/// newer Outlook may have written, and the properties on a row that mean nothing to us. All of those
/// have to come back untouched, because the alternative is quietly damaging a file another program
/// owns.
/// </summary>
public class AutocompleteStreamTests
{
    private sealed class StreamBuilder
    {
        private readonly List<byte> _bytes = [];

        public StreamBuilder(uint metadata = 0xDEADBEEF, uint major = 12, uint minor = 0)
        {
            UInt32(metadata);
            UInt32(major);
            UInt32(minor);
        }

        public List<(uint Tag, byte[] Value)> Pending { get; } = [];

        private void UInt32(uint value)
        {
            Span<byte> four = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(four, value);
            _bytes.AddRange(four.ToArray());
        }

        private void UInt64(ulong value)
        {
            Span<byte> eight = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(eight, value);
            _bytes.AddRange(eight.ToArray());
        }

        public static byte[] UnicodeValue(string text)
        {
            var encoded = Encoding.Unicode.GetBytes(text + '\0');
            var data = new byte[4 + encoded.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)encoded.Length);
            encoded.CopyTo(data, 4);
            return data;
        }

        public static byte[] BinaryValue(params byte[] content)
        {
            var data = new byte[4 + content.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)content.Length);
            content.CopyTo(data, 4);
            return data;
        }

        public byte[] Build(IReadOnlyList<IReadOnlyList<(uint Tag, byte[] Value)>> rows, byte[]? extra = null, byte[]? trailing = null)
        {
            UInt32((uint)rows.Count);

            foreach (var row in rows)
            {
                UInt32((uint)row.Count);

                foreach (var (tag, value) in row)
                {
                    UInt32(tag);
                    UInt32(0);
                    UInt64(0);
                    _bytes.AddRange(value);
                }
            }

            extra ??= [];
            UInt32((uint)extra.Length);
            _bytes.AddRange(extra);
            _bytes.AddRange(trailing ?? new byte[8]);

            return [.. _bytes];
        }
    }

    private static (uint, byte[]) Text(uint tag, string value) => (tag, StreamBuilder.UnicodeValue(value));

    private static (uint, byte[]) Weight(int value)
    {
        // A fixed-size property lives in the union, so its value data is empty. Building it by hand
        // here rather than through the production helper keeps the test honest about the layout.
        return (AutocompleteEntry.WeightTag, []);
    }

    private static byte[] OneRealisticRow(int weight = 0x4000, byte[]? extra = null)
    {
        var builder = new StreamBuilder();
        var bytes = builder.Build(
        [
            [
                Text(AutocompleteEntry.NickName, "Matthew Johnson"),
                Text(AutocompleteEntry.DisplayNameTag, "Matthew Johnson"),
                Text(AutocompleteEntry.SmtpAddressTag, "mjohnson@example.test"),
                Text(AutocompleteEntry.AddressTypeTag, "SMTP"),
                (0x0FFF0102u, StreamBuilder.BinaryValue(1, 2, 3, 4)),
                (AutocompleteEntry.WeightTag, []),
            ],
        ], extra);

        // Patch the weight into the union of the last property, which is where a PT_LONG lives.
        var unionAt = bytes.Length - 8 - (extra?.Length ?? 0) - 4 - 8;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(unionAt), (uint)weight);

        return bytes;
    }

    [Fact]
    public void ReadsTheNamesAndTheWeight()
    {
        var stream = AutocompleteStream.Parse(OneRealisticRow(weight: 0x4000));

        stream.Entries.Should().HaveCount(1);
        stream.Entries[0].DisplayName.Should().Be("Matthew Johnson");
        stream.Entries[0].SmtpAddress.Should().Be("mjohnson@example.test");
        stream.Entries[0].Weight.Should().Be(0x4000);
    }

    [Fact]
    public void RoundTripsByteForByte()
    {
        // The whole feature rests on this: what is written back is what was read, save for the rows
        // that were deliberately changed.
        var original = OneRealisticRow();

        AutocompleteStream.Parse(original).Serialize().Should().Equal(original);
    }

    [Fact]
    public void RoundTripsTheExtraInformationItDoesNotUnderstand()
    {
        // The format says a newer Outlook may write here and expects to find it again. Dropping it
        // loses that Outlook's data, silently.
        var extra = new byte[] { 9, 8, 7, 6, 5 };
        var original = OneRealisticRow(extra: extra);

        var parsed = AutocompleteStream.Parse(original);

        parsed.ExtraInformation.Should().Equal(extra);
        parsed.Serialize().Should().Equal(original);
    }

    [Fact]
    public void KeepsPropertiesItHasNoUseFor()
    {
        var parsed = AutocompleteStream.Parse(OneRealisticRow());

        // The entry id is meaningless to us and must still be there afterwards.
        parsed.Entries[0].Find(0x0FFF0102u).Should().NotBeNull();
        parsed.Entries[0].Properties.Should().HaveCount(6);
    }

    [Fact]
    public void RefusesAMajorVersionItDoesNotKnow()
    {
        var future = new StreamBuilder(major: 13).Build([]);

        var act = () => AutocompleteStream.Parse(future);

        act.Should().Throw<AutocompleteFormatException>().WithMessage("*13*");
    }

    [Fact]
    public void RefusesAPropertyTypeItCannotMeasure()
    {
        // Not knowing a value's length means not knowing where the next property starts, so carrying
        // on would corrupt every row after this one.
        var builder = new StreamBuilder();
        var bytes = builder.Build([[(0x30010999u, [])]]);

        var act = () => AutocompleteStream.Parse(bytes);

        act.Should().Throw<AutocompleteFormatException>();
    }

    [Fact]
    public void RefusesAStreamThatEndsMidValue()
    {
        var truncated = OneRealisticRow()[..30];

        var act = () => AutocompleteStream.Parse(truncated);

        act.Should().Throw<AutocompleteFormatException>();
    }

    [Fact]
    public void RecordingANewAddressAddsItAtTheTop()
    {
        var stream = AutocompleteStream.Parse(OneRealisticRow(weight: AutocompleteEntry.MinimumWeight));

        stream.Record("someone@example.test", "Someone Else");

        stream.Entries.Should().HaveCount(2);
        stream.Entries[0].SmtpAddress.Should().Be("someone@example.test");
        stream.Entries[0].Weight.Should().Be(AutocompleteEntry.MinimumWeight + AutocompleteEntry.WeightPerSend);
    }

    [Fact]
    public void RecordingAKnownAddressMakesItHeavierRatherThanAddingItTwice()
    {
        var stream = AutocompleteStream.Parse(OneRealisticRow(weight: 0x4000));

        stream.Record("MJOHNSON@EXAMPLE.TEST");

        stream.Entries.Should().HaveCount(1, "the address differs only in case");
        stream.Entries[0].Weight.Should().Be(0x4000 + AutocompleteEntry.WeightPerSend);
    }

    [Fact]
    public void TheListStaysSortedHeaviestFirst()
    {
        var stream = AutocompleteStream.Parse(OneRealisticRow(weight: AutocompleteEntry.MinimumWeight));

        stream.Record("a@example.test");
        stream.Record("b@example.test");
        stream.Record("b@example.test");

        stream.Entries.Select(e => e.SmtpAddress).First().Should().Be("b@example.test");
        stream.Entries.Select(e => e.Weight).Should().BeInDescendingOrder();
    }

    [Fact]
    public void AddedEntriesSurviveARoundTrip()
    {
        var stream = AutocompleteStream.Parse(OneRealisticRow());
        stream.Record("someone@example.test", "Someone Else");

        var reparsed = AutocompleteStream.Parse(stream.Serialize());

        reparsed.Entries.Should().HaveCount(2);
        reparsed.Entries.Select(e => e.SmtpAddress).Should().Contain("someone@example.test");
        reparsed.Entries.Select(e => e.DisplayName).Should().Contain("Someone Else");
    }

    [Fact]
    public void AWeightNeverOverflowsIntoNonsense()
    {
        AutocompleteEntry.Heavier(int.MaxValue - 1).Should().Be(int.MaxValue);
        AutocompleteEntry.Heavier(0).Should().Be(AutocompleteEntry.MinimumWeight + AutocompleteEntry.WeightPerSend);
    }

    [Fact]
    public void ForgettingRemovesAnAddress()
    {
        var stream = AutocompleteStream.Parse(OneRealisticRow());

        stream.Forget("mjohnson@example.test").Should().BeTrue();
        stream.Entries.Should().BeEmpty();
    }

    [Fact]
    public void AListThatReproducesExactlySaysSo()
        => AutocompleteStream.Parse(OneRealisticRow()).RoundTripsExactly.Should().BeTrue();

    [Fact]
    public void AListWithSomethingUnaccountedForSaysItDoesNotReproduce()
    {
        // Bytes past the trailing metadata: the parser has no place for them, so writing this list
        // back would silently drop them. The flag is what stops the writer from doing that, and it
        // stands in for the real worry - an Exchange storing something this code has not seen.
        var padded = OneRealisticRow().Concat(new byte[] { 0xAA, 0xBB }).ToArray();

        AutocompleteStream.Parse(padded).RoundTripsExactly.Should().BeFalse();
    }

    [Fact]
    public void AListBuiltFromNothingHasNothingToContradict()
        => AutocompleteStream.Empty().RoundTripsExactly.Should().BeTrue();

    [Fact]
    public void AnEmptyListSerialisesAndReadsBack()
    {
        var empty = AutocompleteStream.Empty();

        var reparsed = AutocompleteStream.Parse(empty.Serialize());

        reparsed.Entries.Should().BeEmpty();
    }
}
